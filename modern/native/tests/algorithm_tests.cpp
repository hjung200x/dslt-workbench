#include "operations.hpp"

#include <algorithm>
#include <cassert>
#include <cmath>
#include <cstdint>
#include <limits>
#include <numbers>
#include <numeric>
#include <stdexcept>
#include <string_view>
#include <utility>
#include <vector>

namespace {

dslt_volume_descriptor descriptor(std::uint32_t width, std::uint32_t height, std::uint32_t depth) {
    dslt_volume_descriptor value{};
    value.width = width;
    value.height = height;
    value.depth = depth;
    value.channels = 1;
    value.voxel_type = DSLT_VOXEL_FLOAT32;
    value.element_count = static_cast<std::uint64_t>(width) * height * depth;
    value.calibration = {1.0, 1.0, 1.0, 1, {}};
    return value;
}

bool close(float actual, float expected, float tolerance = 1.0e-6F) {
    return std::abs(actual - expected) <= tolerance;
}

std::size_t offset(
    std::size_t x,
    std::size_t y,
    std::size_t z,
    const dslt_volume_descriptor& desc) {
    return z * desc.width * desc.height + y * desc.width + x;
}

float oracle_trilinear(
    std::span<const float> source,
    const dslt_volume_descriptor& desc,
    float x,
    float y,
    float z) {
    const auto cx = std::clamp(x, 0.0F, static_cast<float>(desc.width - 1));
    const auto cy = std::clamp(y, 0.0F, static_cast<float>(desc.height - 1));
    const auto cz = std::clamp(z, 0.0F, static_cast<float>(desc.depth - 1));
    const auto x0 = static_cast<std::size_t>(cx);
    const auto y0 = static_cast<std::size_t>(cy);
    const auto z0 = static_cast<std::size_t>(cz);
    const auto x1 = std::min<std::size_t>(x0 + 1, desc.width - 1);
    const auto y1 = std::min<std::size_t>(y0 + 1, desc.height - 1);
    const auto z1 = std::min<std::size_t>(z0 + 1, desc.depth - 1);
    const auto tx = cx - static_cast<float>(x0);
    const auto ty = cy - static_cast<float>(y0);
    const auto tz = cz - static_cast<float>(z0);
    const auto lerp = [](float left, float right, float amount) { return left + (right - left) * amount; };
    const auto c00 = lerp(source[offset(x0, y0, z0, desc)], source[offset(x1, y0, z0, desc)], tx);
    const auto c10 = lerp(source[offset(x0, y1, z0, desc)], source[offset(x1, y1, z0, desc)], tx);
    const auto c01 = lerp(source[offset(x0, y0, z1, desc)], source[offset(x1, y0, z1, desc)], tx);
    const auto c11 = lerp(source[offset(x0, y1, z1, desc)], source[offset(x1, y1, z1, desc)], tx);
    return lerp(lerp(c00, c10, ty), lerp(c01, c11, ty), tz);
}

dslt::ops::DsltResponse oracle_response(
    std::span<const float> source,
    const dslt_volume_descriptor& desc,
    int radius,
    int level,
    bool gaussian) {
    dslt::ops::DsltResponse response{
        std::vector<float>(source.size(), std::numeric_limits<float>::max()),
        std::vector<float>(source.size(), 0.0F),
    };
    const auto directions = dslt::ops::geodesic_directions(level);
    for (int current_radius = radius; current_radius > 0; --current_radius) {
        const auto weights = dslt::ops::line_weights(current_radius, gaussian);
        for (const auto& direction : directions) {
            const auto xy = std::sqrt(direction.x * direction.x + direction.y * direction.y);
            const auto alpha = std::abs(2.0F * std::acos(std::clamp(xy, 0.0F, 1.0F)) /
                std::numbers::pi_v<float>);
            for (std::size_t z = 0; z < desc.depth; ++z) {
                for (std::size_t y = 0; y < desc.height; ++y) {
                    for (std::size_t x = 0; x < desc.width; ++x) {
                        float sum = 0.0F;
                        for (int sample = -current_radius; sample <= current_radius; ++sample) {
                            sum += oracle_trilinear(
                                source, desc,
                                static_cast<float>(x) + direction.x * sample,
                                static_cast<float>(y) + direction.y * sample,
                                static_cast<float>(z) + direction.z * sample) *
                                weights[static_cast<std::size_t>(sample + current_radius)];
                        }
                        const auto id = offset(x, y, z, desc);
                        if (response.minimum[id] > sum) {
                            response.minimum[id] = sum;
                            response.alpha[id] = alpha;
                        }
                    }
                }
            }
        }
    }
    return response;
}

void direction_fixture() {
    for (const auto [level, expected] : std::vector<std::pair<int, std::size_t>>{{1, 21}, {2, 81}, {3, 321}}) {
        const auto directions = dslt::ops::geodesic_directions(level);
        assert(directions.size() == expected);
        for (std::size_t index = 0; index < directions.size(); ++index) {
            const auto& direction = directions[index];
            assert(close(std::sqrt(direction.x * direction.x + direction.y * direction.y + direction.z * direction.z), 1.0F));
            for (std::size_t other = index + 1; other < directions.size(); ++other) {
                const auto& candidate = directions[other];
                const auto dot = direction.x * candidate.x + direction.y * candidate.y + direction.z * candidate.z;
                assert(dot > -1.0F + 1.0e-5F);
            }
        }
    }
}

void weight_fixture() {
    for (const auto radius : {1, 2, 14}) {
        const auto mean = dslt::ops::line_weights(radius, false);
        assert(mean.size() == static_cast<std::size_t>(radius * 2 + 1));
        assert(close(std::reduce(mean.begin(), mean.end()), 1.0F));
        for (const auto value : mean) assert(close(value, 1.0F / static_cast<float>(radius * 2 + 1)));

        const auto gaussian = dslt::ops::line_weights(radius, true);
        assert(close(std::reduce(gaussian.begin(), gaussian.end()), 1.0F));
        const auto sigma = 0.3F * static_cast<float>(radius - 1) + 0.8F;
        std::vector<float> oracle(gaussian.size());
        for (int offset = -radius; offset <= radius; ++offset) {
            oracle[static_cast<std::size_t>(offset + radius)] =
                std::exp(-static_cast<float>(offset * offset) / (2.0F * sigma * sigma));
        }
        const auto sum = std::reduce(oracle.begin(), oracle.end());
        for (std::size_t index = 0; index < oracle.size(); ++index) {
            assert(close(gaussian[index], oracle[index] / sum));
        }
    }
}

void interpolation_fixture() {
    const auto desc = descriptor(3, 3, 3);
    std::vector<float> ramp(desc.element_count);
    for (std::size_t z = 0; z < desc.depth; ++z) {
        for (std::size_t y = 0; y < desc.height; ++y) {
            for (std::size_t x = 0; x < desc.width; ++x) {
                ramp[z * desc.width * desc.height + y * desc.width + x] =
                    static_cast<float>(x + 2 * y + 4 * z);
            }
        }
    }
    assert(close(dslt::ops::trilinear_clamp(ramp, desc, 0.25F, 0.5F, 0.75F), 4.25F));
    assert(close(dslt::ops::trilinear_clamp(ramp, desc, -1.0F, 0.5F, 5.0F), 9.0F));
}

void response_and_boundary_fixture() {
    const auto desc = descriptor(3, 3, 3);
    std::vector<float> constant(desc.element_count, 0.5F);
    const dslt::Volume volume(desc, constant);
    const auto response = dslt::ops::dslt_response(volume, 2, 1, 1, {});
    for (std::size_t index = 0; index < constant.size(); ++index) {
        assert(close(response.minimum[index], 0.5F));
        assert(close(response.alpha[index], 1.0F));
    }

    const auto boundary_desc = descriptor(2, 1, 1);
    const std::vector<float> boundary{0.5F, std::nextafter(0.5F, 1.0F)};
    const dslt::ops::DsltResponse boundary_response{{0.5F, 0.5F}, {0.0F, 0.0F}};
    const auto mask = dslt::ops::apply_dslt_threshold(
        boundary, boundary_desc, boundary_response, 0.0F, 0.2F, {});
    assert(mask[0] == 0.0F);
    assert(mask[1] == 0.8F);
}

void ramp_response_fixture() {
    const auto desc = descriptor(4, 4, 4);
    for (int ramp_kind = 0; ramp_kind < 4; ++ramp_kind) {
        std::vector<float> ramp(desc.element_count);
        for (std::size_t z = 0; z < desc.depth; ++z) {
            for (std::size_t y = 0; y < desc.height; ++y) {
                for (std::size_t x = 0; x < desc.width; ++x) {
                    const auto value = ramp_kind == 0 ? static_cast<float>(x) :
                        ramp_kind == 1 ? static_cast<float>(y) :
                        ramp_kind == 2 ? static_cast<float>(z) :
                        static_cast<float>(x + 2 * y + 3 * z);
                    ramp[offset(x, y, z, desc)] = value;
                }
            }
        }
        const dslt::Volume volume(desc, ramp);
        for (const auto kernel : {0, 1}) {
            const auto actual = dslt::ops::dslt_response(volume, 2, 1, kernel, {});
            const auto expected = oracle_response(ramp, desc, 2, 1, kernel == 0);
            for (std::size_t index = 0; index < ramp.size(); ++index) {
                assert(close(actual.minimum[index], expected.minimum[index], 2.0e-6F));
                assert(close(actual.alpha[index], expected.alpha[index], 2.0e-6F));
            }
        }
    }
}

void work_estimate_fixture() {
    const auto tiny = descriptor(1, 1, 1);
    const auto estimate = dslt::ops::estimate_dslt_work(tiny, 2, 1, false);
    assert(estimate.voxel_count == 1);
    assert(estimate.direction_count == 21);
    assert(estimate.line_samples_per_voxel == 8);
    assert(estimate.directional_work_items == 168);
    assert(estimate.estimated_host_bytes == 268);
    assert(estimate.sweep_passes == 1);
    assert(estimate.within_limits);

    auto huge = descriptor(2048, 2048, 2048);
    const auto oversized = dslt::ops::estimate_dslt_work(huge, 14, 5, true, 0.0F, 1.0F, 0.1F);
    assert(!oversized.within_limits);
    bool rejected = false;
    try {
        dslt::ops::enforce_dslt_work_limits(oversized);
    } catch (const dslt::ResourceLimitError& error) {
        const std::string_view message(error.what());
        rejected = message.find("work=") != std::string_view::npos &&
            message.find("host_bytes=") != std::string_view::npos &&
            message.find("directions=5121") != std::string_view::npos;
    }
    assert(rejected);
}

void closing_and_component_fixture() {
    const auto closing_desc = descriptor(5, 5, 5);
    std::vector<float> solid(closing_desc.element_count, 0.8F);
    solid[2 * 25 + 2 * 5 + 2] = 0.0F;
    const auto closed = dslt::ops::spherical_closing(solid, closing_desc, 1, {});
    assert(std::all_of(closed.begin(), closed.end(), [](float value) { return value == 0.8F; }));

    const auto component_desc = descriptor(5, 1, 1);
    const std::vector<float> mask{0.0F, 0.0F, 0.8F, 0.0F, 0.8F};
    const auto components = dslt::ops::connected_components_low_6(mask, component_desc, 0.1F, 1, {}, {});
    assert(components.component_count == 1);
    assert(components.labels[0] == 0 && components.labels[1] == 0);
    assert(components.labels[3] == -1);
}

void iterative_sweep_fixture() {
    const auto desc = descriptor(9, 5, 1);
    std::vector<float> source(desc.element_count, 0.0F);
    dslt::ops::DsltResponse response{
        std::vector<float>(source.size(), -1.0F),
        std::vector<float>(source.size(), 0.0F),
    };
    const auto index = [&desc](std::size_t x, std::size_t y) { return y * desc.width + x; };
    response.minimum[index(0, 2)] = 1.0F;
    for (std::size_t y = 1; y <= 3; ++y) {
        for (std::size_t x = 4; x <= 6; ++x) {
            if (x != 5 || y != 2) response.minimum[index(x, y)] = 1.0F;
        }
    }

    const dslt::ops::DsltSegmentationParameters parameters{
        1, 1, 1,
        0.0F, 0.1F, 0.1F, 0.2F,
        0, 0, 0,
    };
    const auto segmented = dslt::ops::dslt_segmentation_from_response(
        source, desc, response, parameters, {});
    assert(segmented.passes_completed == 2);
    assert(segmented.component_count == 2);
    assert(segmented.labels[index(0, 2)] == 0);
    for (std::size_t y = 1; y <= 3; ++y) {
        for (std::size_t x = 4; x <= 6; ++x) {
            if (x == 5 && y == 2) assert(segmented.labels[index(x, y)] == -1);
            else assert(segmented.labels[index(x, y)] == 1);
        }
    }

    bool cancelled = false;
    try {
        static_cast<void>(dslt::ops::dslt_segmentation_from_response(
            source, desc, response, parameters,
            [](float progress) { return progress < 0.5F; }));
    } catch (const std::runtime_error& error) {
        cancelled = std::string_view(error.what()) == "cancelled";
    }
    assert(cancelled);
}

void threshold_sweep_fixture() {
    const auto desc = descriptor(9, 5, 1);
    std::vector<float> source(desc.element_count, 1.0F);
    const auto index = [&desc](std::size_t x, std::size_t y) { return y * desc.width + x; };
    source[index(0, 2)] = 0.0F;
    for (std::size_t y = 1; y <= 3; ++y) {
        for (std::size_t x = 4; x <= 6; ++x) {
            if (x != 5 || y != 2) source[index(x, y)] = 0.0F;
        }
    }
    const dslt::Volume volume(desc, source);
    const dslt::ops::ThresholdSweepParameters parameters{
        0.4F, 0.8F, 0.4F,
        0, 0, 0,
    };
    const auto segmented = dslt::ops::threshold_sweep(volume, parameters, {});
    assert(segmented.passes_completed == 2);
    assert(segmented.component_count == 2);
    assert(segmented.labels[index(0, 2)] == 0);
    for (std::size_t y = 1; y <= 3; ++y) {
        for (std::size_t x = 4; x <= 6; ++x) {
            if (x == 5 && y == 2) assert(segmented.labels[index(x, y)] == -1);
            else assert(segmented.labels[index(x, y)] == 1);
        }
    }

    bool rejected = false;
    try {
        auto invalid = parameters;
        invalid.minimum_threshold = 0.9F;
        static_cast<void>(dslt::ops::threshold_sweep(volume, invalid, {}));
    } catch (const std::invalid_argument&) {
        rejected = true;
    }
    assert(rejected);

    bool cancelled = false;
    try {
        static_cast<void>(dslt::ops::threshold_sweep(
            volume, parameters, [](float progress) { return progress < 0.5F; }));
    } catch (const std::runtime_error& error) {
        cancelled = std::string_view(error.what()) == "cancelled";
    }
    assert(cancelled);
}

} // namespace

int main() {
    direction_fixture();
    weight_fixture();
    interpolation_fixture();
    response_and_boundary_fixture();
    ramp_response_fixture();
    work_estimate_fixture();
    closing_and_component_fixture();
    iterative_sweep_fixture();
    threshold_sweep_fixture();
    return 0;
}
