#include "operations.hpp"

#include <algorithm>
#include <cassert>
#include <cmath>
#include <cstdint>
#include <limits>
#include <numeric>
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

void closing_and_component_fixture() {
    const auto closing_desc = descriptor(5, 5, 5);
    std::vector<float> solid(closing_desc.element_count, 0.8F);
    solid[2 * 25 + 2 * 5 + 2] = 0.0F;
    const auto closed = dslt::ops::spherical_closing(solid, closing_desc, 1, {});
    assert(std::all_of(closed.begin(), closed.end(), [](float value) { return value == 0.8F; }));

    const auto component_desc = descriptor(5, 1, 1);
    const std::vector<float> mask{0.0F, 0.0F, 0.8F, 0.0F, 0.8F};
    const auto components = dslt::ops::connected_components_low_6(mask, component_desc, 0.1F, 1, {});
    assert(components.component_count == 1);
    assert(components.labels[0] == 0 && components.labels[1] == 0);
    assert(components.labels[3] == -1);
}

} // namespace

int main() {
    direction_fixture();
    weight_fixture();
    interpolation_fixture();
    response_and_boundary_fixture();
    closing_and_component_fixture();
    return 0;
}
