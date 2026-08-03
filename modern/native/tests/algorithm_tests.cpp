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

std::vector<float> oracle_adaptive_threshold(
    std::span<const float> source,
    const dslt_volume_descriptor& desc,
    int radius,
    bool gaussian,
    float constant_c,
    bool include_z) {
    std::vector<float> weights(static_cast<std::size_t>(radius * 2 + 1));
    if (gaussian) {
        const auto sigma = 0.3 * static_cast<double>(radius - 1) + 0.8;
        const auto denominator = 2.0 * sigma * sigma;
        double sum = 0.0;
        for (int k = -radius; k <= radius; ++k) {
            const auto weight = std::exp(-static_cast<double>(k * k) / denominator);
            weights[static_cast<std::size_t>(k + radius)] = static_cast<float>(weight);
            sum += weight;
        }
        for (auto& weight : weights) weight = static_cast<float>(weight / sum);
    } else {
        std::fill(weights.begin(), weights.end(), 1.0F / static_cast<float>(weights.size()));
    }
    const auto convolve = [&](std::span<const float> input, int axis) {
        std::vector<float> output(input.size());
        for (std::size_t z = 0; z < desc.depth; ++z) {
            for (std::size_t y = 0; y < desc.height; ++y) {
                for (std::size_t x = 0; x < desc.width; ++x) {
                    double sum = 0.0;
                    for (int k = -radius; k <= radius; ++k) {
                        const auto xx = static_cast<std::size_t>(axis == 0
                            ? std::clamp<long long>(static_cast<long long>(x) + k, 0, desc.width - 1)
                            : static_cast<long long>(x));
                        const auto yy = static_cast<std::size_t>(axis == 1
                            ? std::clamp<long long>(static_cast<long long>(y) + k, 0, desc.height - 1)
                            : static_cast<long long>(y));
                        const auto zz = static_cast<std::size_t>(axis == 2
                            ? std::clamp<long long>(static_cast<long long>(z) + k, 0, desc.depth - 1)
                            : static_cast<long long>(z));
                        sum += input[offset(xx, yy, zz, desc)] * weights[static_cast<std::size_t>(k + radius)];
                    }
                    output[offset(x, y, z, desc)] = static_cast<float>(sum);
                }
            }
        }
        return output;
    };
    auto local = convolve(source, 0);
    local = convolve(local, 1);
    if (include_z) local = convolve(local, 2);
    for (std::size_t index = 0; index < local.size(); ++index) {
        local[index] = source[index] > local[index] - constant_c ? 0.8F : 0.0F;
    }
    return local;
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

void adaptive_threshold_fixture() {
    const auto desc = descriptor(3, 2, 2);
    const std::vector<float> source{
        0.0F, 0.2F, 0.4F,
        0.1F, 0.8F, 0.3F,
        0.6F, 0.4F, 1.0F,
        0.2F, 0.5F, 0.7F,
    };
    const dslt::Volume volume(desc, source);
    for (const auto kernel : {0, 1}) {
        for (const auto include_z : {false, true}) {
            const auto expected = oracle_adaptive_threshold(
                source, desc, 1, kernel == 0, -0.04F, include_z);
            const auto actual = dslt::ops::adaptive_threshold(
                volume, 1, kernel, -0.04F, include_z, {});
            assert(actual == expected);
        }
    }

    const auto z_desc = descriptor(1, 1, 3);
    const std::vector<float> z_source{0.0F, 1.0F, 0.0F};
    const dslt::Volume z_volume(z_desc, z_source);
    const auto two_d = dslt::ops::adaptive_threshold(z_volume, 1, 1, 0.0F, false, {});
    const auto three_d = dslt::ops::adaptive_threshold(z_volume, 1, 1, 0.0F, true, {});
    assert(std::all_of(two_d.begin(), two_d.end(), [](float value) { return value == 0.0F; }));
    assert(three_d[0] == 0.0F && three_d[1] == 0.8F && three_d[2] == 0.0F);

    const auto tie = dslt::ops::adaptive_threshold(volume, 0, 1, 0.0F, true, {});
    assert(std::all_of(tie.begin(), tie.end(), [](float value) { return value == 0.0F; }));

    bool rejected = false;
    try {
        static_cast<void>(dslt::ops::adaptive_threshold(volume, 101, 1, 0.0F, true, {}));
    } catch (const std::invalid_argument&) {
        rejected = true;
    }
    assert(rejected);

    rejected = false;
    try {
        static_cast<void>(dslt::ops::adaptive_threshold(volume, 1, 2, 0.0F, true, {}));
    } catch (const std::invalid_argument&) {
        rejected = true;
    }
    assert(rejected);

    rejected = false;
    try {
        static_cast<void>(dslt::ops::adaptive_threshold(
            volume, 1, 1, std::numeric_limits<float>::quiet_NaN(), true, {}));
    } catch (const std::invalid_argument&) {
        rejected = true;
    }
    assert(rejected);

    bool cancelled = false;
    try {
        static_cast<void>(dslt::ops::adaptive_threshold(
            volume, 1, 1, 0.0F, true,
            [](float progress) { return progress < 0.5F; }));
    } catch (const std::runtime_error& error) {
        cancelled = std::string_view(error.what()) == "cancelled";
    }
    assert(cancelled);
}

void h_minima_fixture() {
    const auto desc = descriptor(3, 1, 1);
    const std::vector<float> pit{1.0F, 0.0F, 1.0F};
    const dslt::Volume volume(desc, pit);
    const auto immediate = dslt::ops::h_minima(volume, 0.5F, 1, {});
    const auto batched = dslt::ops::h_minima(volume, 0.5F, 50, {});
    const std::vector<float> expected{0.8F, 0.0F, 0.8F};
    assert(immediate == expected);
    assert(batched == expected);

    const auto zero = dslt::ops::h_minima(volume, 0.0F, 1, {});
    assert(std::all_of(zero.begin(), zero.end(), [](float value) { return value == 0.8F; }));

    const std::vector<float> shallow{1.0F, 0.8F, 1.0F};
    const dslt::Volume shallow_volume(desc, shallow);
    const auto suppressed = dslt::ops::h_minima(shallow_volume, 0.3F, 1, {});
    assert(std::all_of(suppressed.begin(), suppressed.end(), [](float value) { return value == 0.0F; }));

    const auto cube_desc = descriptor(3, 3, 3);
    std::vector<float> cube(cube_desc.element_count, 1.0F);
    cube[offset(1, 1, 1, cube_desc)] = 0.0F;
    const dslt::Volume cube_volume(cube_desc, cube);
    const auto cube_result = dslt::ops::h_minima(cube_volume, 0.5F, 1, {});
    assert(cube_result[offset(1, 1, 1, cube_desc)] == 0.0F);
    assert(std::count(cube_result.begin(), cube_result.end(), 0.8F) == 26);

    for (const auto invalid_height : {-0.1F, 1.1F, std::numeric_limits<float>::quiet_NaN()}) {
        bool rejected = false;
        try {
            static_cast<void>(dslt::ops::h_minima(volume, invalid_height, 1, {}));
        } catch (const std::invalid_argument&) {
            rejected = true;
        }
        assert(rejected);
    }

    bool rejected = false;
    try {
        static_cast<void>(dslt::ops::h_minima(volume, 0.1F, 0, {}));
    } catch (const std::invalid_argument&) {
        rejected = true;
    }
    assert(rejected);

    auto non_finite = pit;
    non_finite[1] = std::numeric_limits<float>::infinity();
    const dslt::Volume non_finite_volume(desc, non_finite);
    rejected = false;
    try {
        static_cast<void>(dslt::ops::h_minima(non_finite_volume, 0.1F, 1, {}));
    } catch (const std::invalid_argument&) {
        rejected = true;
    }
    assert(rejected);

    bool cancelled = false;
    try {
        static_cast<void>(dslt::ops::h_minima(
            cube_volume, 0.5F, 50,
            [](float progress) { return progress == 0.0F; }));
    } catch (const std::runtime_error& error) {
        cancelled = std::string_view(error.what()) == "cancelled";
    }
    assert(cancelled);
}

void watershed_fixture() {
    const auto desc = descriptor(5, 1, 1);
    const std::vector<float> source(5, 0.0F);
    const dslt::Volume volume(desc, source);
    const std::vector<std::int32_t> seeds{10, -1, -1, -1, 20};
    const std::vector<std::int32_t> selected{10, 20};
    const auto result = dslt::ops::watershed(
        volume, seeds, selected, 0, {}, {});
    assert((result.labels == std::vector<std::int32_t>{10, 10, 10, 20, 20}));
    assert(result.component_count == 2);
    assert(result.passes_completed == 256);

    const std::vector<std::int32_t> left_only{10};
    const auto one_seed = dslt::ops::watershed(
        volume, seeds, left_only, 0, {}, {});
    assert(std::all_of(one_seed.labels.begin(), one_seed.labels.end(),
        [](std::int32_t label) { return label == 10; }));
    assert(one_seed.component_count == 1);

    bool rejected = false;
    try {
        static_cast<void>(dslt::ops::watershed(
            volume, seeds, selected, 2, {}, {}));
    } catch (const std::invalid_argument&) {
        rejected = true;
    }
    assert(rejected);

    bool cancelled = false;
    try {
        static_cast<void>(dslt::ops::watershed(
            volume, seeds, selected, 0, {},
            [](float progress) { return progress == 0.0F; }));
    } catch (const std::runtime_error& error) {
        cancelled = std::string_view(error.what()) == "cancelled";
    }
    assert(cancelled);
}

void filtered_height_map_fixture() {
    const auto ramp_desc = descriptor(1, 1, 5);
    const dslt::Volume ramp_volume(
        ramp_desc, std::vector<float>{0.0F, 0.0F, 0.25F, 0.75F, 1.0F});
    const dslt::ops::HeightMapParameters ramp_parameters{0, 0, 0, 0, 0.5F};
    const auto ramp = dslt::ops::height_map(ramp_volume, ramp_parameters, {});
    assert(ramp.size() == 1 && close(ramp[0], 2.5F));

    const auto smooth_desc = descriptor(3, 1, 3);
    const dslt::Volume smooth_volume(
        smooth_desc,
        std::vector<float>{
            1.0F, 0.0F, 0.0F,
            1.0F, 1.0F, 0.0F,
            1.0F, 1.0F, 1.0F,
        });
    const dslt::ops::HeightMapParameters smooth_parameters{1, 0, 1, 1, 0.5F};
    const auto smoothed = dslt::ops::height_map(smooth_volume, smooth_parameters, {});
    assert(close(smoothed[0], 1.0F / 6.0F));
    assert(close(smoothed[1], 2.0F / 3.0F));
    assert(close(smoothed[2], 7.0F / 6.0F));

    const dslt::Volume empty_volume(ramp_desc, std::vector<float>(5, 0.0F));
    const auto no_crossing = dslt::ops::height_map(empty_volume, ramp_parameters, {});
    assert(no_crossing[0] == 4.0F);

    bool rejected = false;
    try {
        static_cast<void>(dslt::ops::height_map(
            ramp_volume, dslt::ops::HeightMapParameters{0, 65, 0, 0, 0.5F}, {}));
    } catch (const std::invalid_argument&) {
        rejected = true;
    }
    assert(rejected);
}

void height_projection_and_depth_fixture() {
    const auto depth_desc = descriptor(3, 1, 3);
    const dslt::Volume depth_volume(
        depth_desc,
        std::vector<float>{
            1.0F, 0.0F, 1.0F,
            0.0F, 0.0F, 0.0F,
            0.0F, 0.0F, 0.0F,
        });
    const dslt::ops::HeightMapParameters height_parameters{0, 0, 0, 0, 0.5F};
    const auto depth = dslt::ops::depth_map(depth_volume, height_parameters, {});
    assert(depth.size() == depth_volume.voxel_count());
    assert(close(depth[offset(0, 0, 1, depth_desc)], 1.0F));
    assert(close(depth[offset(0, 0, 2, depth_desc)], 1.0F));
    assert(close(depth[offset(2, 0, 2, depth_desc)], 1.0F));
    assert(depth[offset(1, 0, 2, depth_desc)] == 0.0F);

    const auto projection_desc = descriptor(1, 1, 4);
    const dslt::Volume projection_volume(
        projection_desc, std::vector<float>{1.0F, 0.2F, 0.8F, 0.4F});
    const dslt::ops::HeightProjectionParameters z_parameters{1, 2, 0.0F, 1.0F, 0.0F};
    const auto z_projection = dslt::ops::height_projection(
        projection_volume, height_parameters, z_parameters, {});
    assert(z_projection.size() == 1 && close(z_projection[0], 0.8F));

    const dslt::ops::HeightProjectionParameters normal_parameters{0, 2, 0.0F, 1.0F, 0.0F};
    const auto normal_projection = dslt::ops::height_projection(
        projection_volume, height_parameters, normal_parameters, {});
    assert(normal_projection.size() == 1 && close(normal_projection[0], 0.8F));

    const auto binary_projection = dslt::ops::height_projection(
        projection_volume, height_parameters,
        dslt::ops::HeightProjectionParameters{0, 2, 0.0F, 1.0F, 0.5F}, {});
    assert(binary_projection.size() == 1 && binary_projection[0] == 1.0F);
    const auto rejected_projection = dslt::ops::height_projection(
        projection_volume, height_parameters,
        dslt::ops::HeightProjectionParameters{1, 2, 0.0F, 1.0F, 0.9F}, {});
    assert(rejected_projection.size() == 1 && rejected_projection[0] == 0.0F);

    bool rejected = false;
    try {
        static_cast<void>(dslt::ops::height_projection(
            projection_volume, height_parameters,
            dslt::ops::HeightProjectionParameters{2, 0, 0.0F, 0.0F, 0.0F}, {}));
    } catch (const std::invalid_argument&) {
        rejected = true;
    }
    assert(rejected);

    bool cancelled = false;
    try {
        static_cast<void>(dslt::ops::depth_map(
            depth_volume, height_parameters,
            [](float value) { return value == 0.0F; }));
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
    adaptive_threshold_fixture();
    h_minima_fixture();
    watershed_fixture();
    filtered_height_map_fixture();
    height_projection_and_depth_fixture();
    return 0;
}
