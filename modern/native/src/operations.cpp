#include "operations.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <deque>
#include <limits>
#include <numbers>
#include <numeric>
#include <sstream>
#include <stdexcept>
#include <unordered_map>
#include <unordered_set>
#include <utility>

namespace dslt::ops {

namespace {

void report(const Engine::Progress& progress, std::size_t done, std::size_t total) {
    if (progress && !progress(total == 0 ? 1.0F : static_cast<float>(done) / static_cast<float>(total))) {
        throw std::runtime_error("cancelled");
    }
}

std::size_t flat(std::size_t x, std::size_t y, std::size_t z, std::size_t width, std::size_t height) {
    return z * width * height + y * width + x;
}

std::uint64_t checked_multiply_u64(std::uint64_t left, std::uint64_t right, const char* name) {
    if (left != 0 && right > std::numeric_limits<std::uint64_t>::max() / left) {
        throw ResourceLimitError(std::string(name) + " estimate overflowed uint64");
    }
    return left * right;
}

std::uint64_t checked_add_u64(std::uint64_t left, std::uint64_t right, const char* name) {
    if (right > std::numeric_limits<std::uint64_t>::max() - left) {
        throw ResourceLimitError(std::string(name) + " estimate overflowed uint64");
    }
    return left + right;
}

float sinc(float value) {
    if (std::abs(value) < 1.0e-7F) return 1.0F;
    const auto p = std::numbers::pi_v<float> * value;
    return std::sin(p) / p;
}

Vec3 midpoint(const Vec3& left, const Vec3& right) {
    return {(left.x + right.x) * 0.5F, (left.y + right.y) * 0.5F, (left.z + right.z) * 0.5F};
}

} // namespace

std::vector<Vec3> geodesic_directions(int level) {
    if (level < 1 || level > 5) throw std::invalid_argument("DSLT direction level must be between 1 and 5");
    std::vector<Vec3> vertices{
        { 0.000000F, -0.000000F,  1.000000F}, { 0.723600F,  0.525720F,  0.447215F},
        {-0.276385F,  0.850640F,  0.447215F}, {-0.894425F, -0.000000F,  0.447215F},
        {-0.276385F, -0.850640F,  0.447215F}, { 0.723600F, -0.525720F,  0.447215F},
        { 0.276385F,  0.850640F, -0.447215F}, {-0.723600F,  0.525720F, -0.447215F},
        {-0.723600F, -0.525720F, -0.447215F}, { 0.276385F, -0.850640F, -0.447215F},
        { 0.894425F,  0.000000F, -0.447215F}, {-0.000000F,  0.000000F, -1.000000F},
    };
    std::vector<std::array<std::size_t, 3>> faces{
        {0, 1, 2}, {1, 0, 5}, {0, 2, 3}, {0, 3, 4}, {0, 4, 5},
        {1, 5, 10}, {2, 1, 6}, {3, 2, 7}, {4, 3, 8}, {5, 4, 9},
        {1, 10, 6}, {2, 6, 7}, {3, 7, 8}, {4, 8, 9}, {5, 9, 10},
        {6, 10, 11}, {7, 6, 11}, {8, 7, 11}, {9, 8, 11}, {9, 10, 11},
    };

    const auto midpoint_index = [&vertices](std::size_t left, std::size_t right) {
        const auto value = midpoint(vertices[left], vertices[right]);
        const auto found = std::find(vertices.begin(), vertices.end(), value);
        if (found != vertices.end()) return static_cast<std::size_t>(found - vertices.begin());
        vertices.push_back(value);
        return vertices.size() - 1;
    };

    for (int subdivision = 0; subdivision < level; ++subdivision) {
        std::vector<std::array<std::size_t, 3>> next;
        next.reserve(faces.size() * 4);
        for (const auto& face : faces) {
            const auto m01 = midpoint_index(face[0], face[1]);
            const auto m12 = midpoint_index(face[1], face[2]);
            const auto m02 = midpoint_index(face[0], face[2]);
            next.push_back({face[0], m01, m02});
            next.push_back({face[1], m12, m01});
            next.push_back({face[2], m02, m12});
            next.push_back({m02, m01, m12});
        }
        faces = std::move(next);
    }

    std::vector<Vec3> directions;
    directions.reserve(vertices.size() / 2 + 1);
    for (const auto& vertex : vertices) {
        const Vec3 opposite{-vertex.x, -vertex.y, -vertex.z};
        if (std::find(directions.begin(), directions.end(), opposite) == directions.end()) {
            directions.push_back(vertex);
        }
    }
    for (auto& direction : directions) {
        const auto length = std::sqrt(
            direction.x * direction.x + direction.y * direction.y + direction.z * direction.z);
        direction.x /= length;
        direction.y /= length;
        direction.z /= length;
    }
    return directions;
}

std::vector<float> line_weights(int radius, bool gaussian) {
    const auto length = radius * 2 + 1;
    std::vector<float> weights(static_cast<std::size_t>(length), 1.0F / static_cast<float>(length));
    if (!gaussian) return weights;
    const auto sigma = 0.3F * static_cast<float>(radius - 1) + 0.8F;
    const auto denominator = 2.0F * sigma * sigma;
    float sum = 0.0F;
    for (int index = 0; index < length; ++index) {
        const auto offset = static_cast<float>(index - radius);
        weights[static_cast<std::size_t>(index)] = std::exp(-(offset * offset) / denominator);
        sum += weights[static_cast<std::size_t>(index)];
    }
    for (auto& weight : weights) weight /= sum;
    return weights;
}

float trilinear_clamp(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    float x,
    float y,
    float z) {
    const auto cx = std::clamp(x, 0.0F, static_cast<float>(descriptor.width - 1));
    const auto cy = std::clamp(y, 0.0F, static_cast<float>(descriptor.height - 1));
    const auto cz = std::clamp(z, 0.0F, static_cast<float>(descriptor.depth - 1));
    const auto x0 = static_cast<std::size_t>(std::floor(cx));
    const auto y0 = static_cast<std::size_t>(std::floor(cy));
    const auto z0 = static_cast<std::size_t>(std::floor(cz));
    const auto x1 = std::min<std::size_t>(x0 + 1, descriptor.width - 1);
    const auto y1 = std::min<std::size_t>(y0 + 1, descriptor.height - 1);
    const auto z1 = std::min<std::size_t>(z0 + 1, descriptor.depth - 1);
    const auto tx = cx - static_cast<float>(x0);
    const auto ty = cy - static_cast<float>(y0);
    const auto tz = cz - static_cast<float>(z0);
    const auto lerp = [](float left, float right, float amount) { return left + (right - left) * amount; };
    const auto c00 = lerp(source[flat(x0, y0, z0, descriptor.width, descriptor.height)],
                          source[flat(x1, y0, z0, descriptor.width, descriptor.height)], tx);
    const auto c10 = lerp(source[flat(x0, y1, z0, descriptor.width, descriptor.height)],
                          source[flat(x1, y1, z0, descriptor.width, descriptor.height)], tx);
    const auto c01 = lerp(source[flat(x0, y0, z1, descriptor.width, descriptor.height)],
                          source[flat(x1, y0, z1, descriptor.width, descriptor.height)], tx);
    const auto c11 = lerp(source[flat(x0, y1, z1, descriptor.width, descriptor.height)],
                          source[flat(x1, y1, z1, descriptor.width, descriptor.height)], tx);
    return lerp(lerp(c00, c10, ty), lerp(c01, c11, ty), tz);
}

std::vector<float> selected_channel(const Volume& volume) {
    std::vector<float> result(volume.voxel_count());
    const auto offset = volume.voxel_count() * volume.descriptor().selected_channel;
    std::copy_n(volume.data().begin() + static_cast<std::ptrdiff_t>(offset), result.size(), result.begin());
    return result;
}

DsltResponse dslt_response(
    const Volume& volume,
    int radius,
    int direction_level,
    int kernel_type,
    const Engine::Progress& progress) {
    if (radius < 1 || radius > 127) throw std::invalid_argument("DSLT radius must be between 1 and 127");
    if (kernel_type != 0 && kernel_type != 1) throw std::invalid_argument("DSLT kernel type must be 0 (Gaussian) or 1 (mean)");

    const auto directions = geodesic_directions(direction_level);
    const auto source = selected_channel(volume);
    const auto& descriptor = volume.descriptor();
    DsltResponse response{
        std::vector<float>(source.size(), std::numeric_limits<float>::max()),
        std::vector<float>(source.size(), 0.0F),
    };
    const auto total_steps = static_cast<std::size_t>(radius) * directions.size();
    std::size_t completed_steps = 0;

    for (int current_radius = radius; current_radius > 0; --current_radius) {
        const auto weights = line_weights(current_radius, kernel_type == 0);
        for (const auto& direction : directions) {
            const auto xy_length = std::sqrt(direction.x * direction.x + direction.y * direction.y);
            const auto latitude = std::acos(std::clamp(xy_length, 0.0F, 1.0F));
            const auto direction_alpha = std::abs(2.0F * latitude / std::numbers::pi_v<float>);
            for (std::size_t z = 0; z < descriptor.depth; ++z) {
                for (std::size_t y = 0; y < descriptor.height; ++y) {
                    for (std::size_t x = 0; x < descriptor.width; ++x) {
                        float sum = 0.0F;
                        for (int offset = -current_radius; offset <= current_radius; ++offset) {
                            const auto weight = weights[static_cast<std::size_t>(offset + current_radius)];
                            sum += trilinear_clamp(
                                source, descriptor,
                                static_cast<float>(x) + direction.x * static_cast<float>(offset),
                                static_cast<float>(y) + direction.y * static_cast<float>(offset),
                                static_cast<float>(z) + direction.z * static_cast<float>(offset)) * weight;
                        }
                        const auto id = flat(x, y, z, descriptor.width, descriptor.height);
                        if (response.minimum[id] > sum) {
                            response.minimum[id] = sum;
                            response.alpha[id] = direction_alpha;
                        }
                    }
                }
            }
            report(progress, ++completed_steps, total_steps);
        }
    }
    return response;
}

std::vector<float> apply_dslt_threshold(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    const DsltResponse& response,
    float constant_c_xy,
    float z_correction_factor,
    const Engine::Progress& progress) {
    const auto expected = static_cast<std::size_t>(descriptor.width) * descriptor.height * descriptor.depth;
    if (source.size() != expected || response.minimum.size() != expected || response.alpha.size() != expected) {
        throw std::invalid_argument("DSLT response dimensions do not match the source volume");
    }
    if (!std::isfinite(constant_c_xy)) throw std::invalid_argument("DSLT C must be finite");
    if (!std::isfinite(z_correction_factor) || z_correction_factor < 0.0F) {
        throw std::invalid_argument("DSLT Z correction factor must be finite and non-negative");
    }

    std::vector<float> result(source.size(), 0.0F);
    const auto constant_c_z = constant_c_xy * z_correction_factor;
    for (std::size_t z = 0; z < descriptor.depth; ++z) {
        for (std::size_t y = 0; y < descriptor.height; ++y) {
            for (std::size_t x = 0; x < descriptor.width; ++x) {
                const auto id = flat(x, y, z, descriptor.width, descriptor.height);
                const auto correction = constant_c_xy * (1.0F - response.alpha[id]) + constant_c_z * response.alpha[id];
                result[id] = source[id] > response.minimum[id] - correction ? 0.8F : 0.0F;
            }
            report(progress, z * descriptor.height + y + 1,
                static_cast<std::size_t>(descriptor.depth) * descriptor.height);
        }
    }
    return result;
}

std::vector<float> dslt_threshold(
    const Volume& volume,
    int radius,
    int direction_level,
    int kernel_type,
    float constant_c_xy,
    float z_correction_factor,
    const Engine::Progress& progress) {
    if (radius < 1 || radius > 127) throw std::invalid_argument("DSLT radius must be between 1 and 127");
    if (kernel_type != 0 && kernel_type != 1) throw std::invalid_argument("DSLT kernel type must be 0 (Gaussian) or 1 (mean)");
    if (!std::isfinite(constant_c_xy)) throw std::invalid_argument("DSLT C must be finite");
    if (!std::isfinite(z_correction_factor) || z_correction_factor < 0.0F) {
        throw std::invalid_argument("DSLT Z correction factor must be finite and non-negative");
    }
    enforce_dslt_work_limits(estimate_dslt_work(
        volume.descriptor(), radius, direction_level, false));
    const auto direction_steps = static_cast<std::size_t>(radius) * geodesic_directions(direction_level).size();
    const auto& descriptor = volume.descriptor();
    const auto threshold_steps = static_cast<std::size_t>(descriptor.depth) * descriptor.height;
    const auto total_steps = direction_steps + threshold_steps;
    const auto response_progress = [&](float value) {
        return !progress || progress(value * static_cast<float>(direction_steps) / static_cast<float>(total_steps));
    };
    const auto threshold_progress = [&](float value) {
        return !progress || progress((static_cast<float>(direction_steps) + value * static_cast<float>(threshold_steps)) /
            static_cast<float>(total_steps));
    };
    const auto response = dslt_response(volume, radius, direction_level, kernel_type, response_progress);
    const auto source = selected_channel(volume);
    return apply_dslt_threshold(
        source, descriptor, response, constant_c_xy, z_correction_factor, threshold_progress);
}

std::vector<float> window_level(const Volume& volume, float minimum, float maximum, const Engine::Progress& progress) {
    if (!(maximum > minimum)) throw std::invalid_argument("window maximum must be greater than minimum");
    auto result = selected_channel(volume);
    const float scale = 1.0F / (maximum - minimum);
    for (std::size_t i = 0; i < result.size(); ++i) {
        result[i] = std::clamp((result[i] - minimum) * scale, 0.0F, 1.0F);
        if ((i & 0xffffU) == 0) report(progress, i, result.size());
    }
    report(progress, result.size(), result.size());
    return result;
}

std::vector<float> threshold(const Volume& volume, float value, const Engine::Progress& progress) {
    auto result = selected_channel(volume);
    for (std::size_t i = 0; i < result.size(); ++i) {
        result[i] = result[i] >= value ? 1.0F : 0.0F;
        if ((i & 0xffffU) == 0) report(progress, i, result.size());
    }
    report(progress, result.size(), result.size());
    return result;
}

std::vector<float> smooth(const Volume& volume, int radius, bool gaussian, const Engine::Progress& progress) {
    if (radius < 0 || radius > 64) throw std::invalid_argument("smoothing radius must be between 0 and 64");
    if (radius == 0) return selected_channel(volume);
    const auto source = selected_channel(volume);
    const auto& d = volume.descriptor();
    std::vector<float> result(source.size(), 0.0F);
    const float sigma = std::max(0.5F, radius / 2.0F);
    const float denom = 2.0F * sigma * sigma;
    for (std::size_t z = 0; z < d.depth; ++z) {
        for (std::size_t y = 0; y < d.height; ++y) {
            for (std::size_t x = 0; x < d.width; ++x) {
                double sum = 0.0;
                double weight_sum = 0.0;
                for (int dz = -radius; dz <= radius; ++dz) {
                    const auto zz = std::clamp<long long>(static_cast<long long>(z) + dz, 0, d.depth - 1);
                    for (int dy = -radius; dy <= radius; ++dy) {
                        const auto yy = std::clamp<long long>(static_cast<long long>(y) + dy, 0, d.height - 1);
                        for (int dx = -radius; dx <= radius; ++dx) {
                            const auto xx = std::clamp<long long>(static_cast<long long>(x) + dx, 0, d.width - 1);
                            const double weight = gaussian
                                ? std::exp(-(dx * dx + dy * dy + dz * dz) / denom)
                                : 1.0;
                            sum += source[flat(xx, yy, zz, d.width, d.height)] * weight;
                            weight_sum += weight;
                        }
                    }
                }
                result[flat(x, y, z, d.width, d.height)] = static_cast<float>(sum / weight_sum);
            }
        }
        report(progress, z + 1, d.depth);
    }
    return result;
}

std::vector<float> morphology_buffer(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    int radius,
    bool dilate,
    bool spherical,
    const Engine::Progress& progress) {
    if (radius < 0 || radius > 64) throw std::invalid_argument("morphology radius must be between 0 and 64");
    const auto expected = static_cast<std::size_t>(descriptor.width) * descriptor.height * descriptor.depth;
    if (source.size() != expected) throw std::invalid_argument("morphology buffer dimensions do not match");
    if (radius == 0) return {source.begin(), source.end()};
    std::vector<float> result(source.size());
    for (std::size_t z = 0; z < descriptor.depth; ++z) {
        for (std::size_t y = 0; y < descriptor.height; ++y) {
            for (std::size_t x = 0; x < descriptor.width; ++x) {
                float chosen = dilate ? -std::numeric_limits<float>::infinity() : std::numeric_limits<float>::infinity();
                for (int dz = -radius; dz <= radius; ++dz) {
                    const auto zz = std::clamp<long long>(static_cast<long long>(z) + dz, 0, descriptor.depth - 1);
                    for (int dy = -radius; dy <= radius; ++dy) {
                        const auto yy = std::clamp<long long>(static_cast<long long>(y) + dy, 0, descriptor.height - 1);
                        for (int dx = -radius; dx <= radius; ++dx) {
                            if (spherical && dx * dx + dy * dy + dz * dz > radius * radius) continue;
                            const auto xx = std::clamp<long long>(static_cast<long long>(x) + dx, 0, descriptor.width - 1);
                            const float value = source[flat(xx, yy, zz, descriptor.width, descriptor.height)];
                            chosen = dilate ? std::max(chosen, value) : std::min(chosen, value);
                        }
                    }
                }
                result[flat(x, y, z, descriptor.width, descriptor.height)] = chosen;
            }
        }
        report(progress, z + 1, descriptor.depth);
    }
    return result;
}

std::vector<float> morphology(const Volume& volume, int radius, bool dilate, bool spherical, const Engine::Progress& progress) {
    const auto source = selected_channel(volume);
    return morphology_buffer(source, volume.descriptor(), radius, dilate, spherical, progress);
}

std::vector<float> spherical_closing(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    int radius,
    const Engine::Progress& progress) {
    if (radius < 0 || radius > 64) throw std::invalid_argument("closing radius must be between 0 and 64");
    if (radius == 0) return {source.begin(), source.end()};
    const auto dilation_progress = [&](float value) {
        return !progress || progress(value * 0.5F);
    };
    const auto erosion_progress = [&](float value) {
        return !progress || progress(0.5F + value * 0.5F);
    };
    auto closed = morphology_buffer(source, descriptor, radius, true, true, dilation_progress);
    return morphology_buffer(closed, descriptor, radius, false, true, erosion_progress);
}

namespace {

Engine::Progress mapped_progress(
    const Engine::Progress& progress,
    float start,
    float length) {
    return [progress, start, length](float value) {
        return !progress || progress(start + std::clamp(value, 0.0F, 1.0F) * length);
    };
}

enum class ConvolutionAxis {
    x,
    y,
    z,
};

std::vector<float> convolve_axis_clamp(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    std::span<const float> weights,
    ConvolutionAxis axis,
    const Engine::Progress& progress) {
    const auto expected = static_cast<std::size_t>(descriptor.width) * descriptor.height * descriptor.depth;
    if (source.size() != expected || weights.empty() || weights.size() % 2 == 0) {
        throw std::invalid_argument("adaptive convolution dimensions or kernel are invalid");
    }
    const auto radius = static_cast<long long>(weights.size() / 2);
    std::vector<float> result(source.size(), 0.0F);
    for (std::size_t z = 0; z < descriptor.depth; ++z) {
        for (std::size_t y = 0; y < descriptor.height; ++y) {
            for (std::size_t x = 0; x < descriptor.width; ++x) {
                double sum = 0.0;
                for (long long offset = -radius; offset <= radius; ++offset) {
                    const auto xx = static_cast<std::size_t>(axis == ConvolutionAxis::x
                        ? std::clamp<long long>(static_cast<long long>(x) + offset, 0, descriptor.width - 1)
                        : static_cast<long long>(x));
                    const auto yy = static_cast<std::size_t>(axis == ConvolutionAxis::y
                        ? std::clamp<long long>(static_cast<long long>(y) + offset, 0, descriptor.height - 1)
                        : static_cast<long long>(y));
                    const auto zz = static_cast<std::size_t>(axis == ConvolutionAxis::z
                        ? std::clamp<long long>(static_cast<long long>(z) + offset, 0, descriptor.depth - 1)
                        : static_cast<long long>(z));
                    sum += static_cast<double>(source[flat(xx, yy, zz, descriptor.width, descriptor.height)]) *
                        weights[static_cast<std::size_t>(offset + radius)];
                }
                result[flat(x, y, z, descriptor.width, descriptor.height)] = static_cast<float>(sum);
            }
        }
        report(progress, z + 1, descriptor.depth);
    }
    return result;
}

std::vector<float> minimum_axis_radius_one(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    ConvolutionAxis axis,
    const Engine::Progress& progress) {
    const auto expected = static_cast<std::size_t>(descriptor.width) * descriptor.height * descriptor.depth;
    if (source.size() != expected) {
        throw std::invalid_argument("minimum-filter dimensions do not match the volume");
    }
    std::vector<float> result(source.size());
    for (std::size_t z = 0; z < descriptor.depth; ++z) {
        for (std::size_t y = 0; y < descriptor.height; ++y) {
            for (std::size_t x = 0; x < descriptor.width; ++x) {
                auto minimum = source[flat(x, y, z, descriptor.width, descriptor.height)];
                for (int delta : {-1, 1}) {
                    const auto xx = static_cast<long long>(x) + (axis == ConvolutionAxis::x ? delta : 0);
                    const auto yy = static_cast<long long>(y) + (axis == ConvolutionAxis::y ? delta : 0);
                    const auto zz = static_cast<long long>(z) + (axis == ConvolutionAxis::z ? delta : 0);
                    if (xx < 0 || yy < 0 || zz < 0 ||
                        xx >= descriptor.width || yy >= descriptor.height || zz >= descriptor.depth) {
                        continue;
                    }
                    minimum = std::min(minimum, source[flat(
                        static_cast<std::size_t>(xx),
                        static_cast<std::size_t>(yy),
                        static_cast<std::size_t>(zz),
                        descriptor.width,
                        descriptor.height)]);
                }
                result[flat(x, y, z, descriptor.width, descriptor.height)] = minimum;
            }
        }
        report(progress, z + 1, descriptor.depth);
    }
    return result;
}

std::vector<float> chunked_spherical_morphology(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    int radius,
    bool opening,
    const Engine::Progress& progress) {
    if (radius < 0) throw std::invalid_argument("chunked morphology radius must be non-negative");
    if (radius == 0) return {source.begin(), source.end()};
    const auto chunks = static_cast<std::size_t>((radius + 3) / 4);
    const auto total_steps = chunks * 2;
    std::size_t step = 0;
    auto result = std::vector<float>(source.begin(), source.end());
    const auto run = [&](bool dilate) {
        int remaining = radius;
        while (remaining > 0) {
            const auto current = std::min(remaining, 4);
            const auto operation_progress = mapped_progress(
                progress,
                static_cast<float>(step) / static_cast<float>(total_steps),
                1.0F / static_cast<float>(total_steps));
            result = morphology_buffer(result, descriptor, current, dilate, true, operation_progress);
            remaining -= current;
            ++step;
        }
    };
    run(!opening);
    run(opening);
    return result;
}

int estimate_wall_thickness(
    std::span<const float> mask,
    const dslt_volume_descriptor& descriptor,
    int starting_radius,
    const Engine::Progress& progress) {
    const auto maximum_dimension = std::max({descriptor.width, descriptor.height, descriptor.depth});
    if (maximum_dimension > static_cast<std::uint32_t>(std::numeric_limits<int>::max())) {
        throw std::invalid_argument("volume dimensions exceed wall-thickness limits");
    }
    const auto maximum_radius = static_cast<int>(maximum_dimension);
    auto radius = starting_radius > 1 ? starting_radius : 1;
    radius = std::min(radius, maximum_radius);
    const auto source_sum = std::accumulate(mask.begin(), mask.end(), 0.0,
        [](double sum, float value) { return sum + std::abs(static_cast<double>(value)); });
    const auto attempts = static_cast<std::size_t>(maximum_radius - radius + 1);
    for (std::size_t attempt = 0; attempt < attempts; ++attempt, ++radius) {
        const auto attempt_progress = mapped_progress(
            progress,
            static_cast<float>(attempt) / static_cast<float>(attempts),
            1.0F / static_cast<float>(attempts));
        const auto opened = chunked_spherical_morphology(mask, descriptor, radius, true, attempt_progress);
        const auto opened_sum = std::accumulate(opened.begin(), opened.end(), 0.0,
            [](double sum, float value) { return sum + std::abs(static_cast<double>(value)); });
        if (opened_sum <= source_sum * 0.5) return radius * 2;
    }
    return maximum_radius * 2;
}

std::size_t invalid_structure_voxels(
    std::span<const std::int32_t> candidate_labels,
    std::span<const std::int32_t> existing_labels,
    std::int32_t component,
    const dslt_volume_descriptor& descriptor,
    int wall_radius,
    const Engine::Progress& progress) {
    std::vector<float> component_mask(candidate_labels.size(), 0.0F);
    for (std::size_t index = 0; index < candidate_labels.size(); ++index) {
        if (candidate_labels[index] == component) component_mask[index] = 1.0F;
    }
    const auto closed = chunked_spherical_morphology(
        component_mask, descriptor, wall_radius, false, progress);
    std::size_t count = 0;
    for (std::size_t index = 0; index < closed.size(); ++index) {
        if (closed[index] > 0.0F && candidate_labels[index] < 0 && existing_labels[index] < 0) ++count;
    }
    return count;
}

std::vector<float> c_schedule(float minimum, float maximum, float interval) {
    if (!std::isfinite(minimum) || !std::isfinite(maximum) || !std::isfinite(interval) ||
        minimum > maximum || interval <= 0.0F) {
        throw std::invalid_argument("DSLT C sweep requires finite ordered bounds and a positive interval");
    }
    std::vector<float> values{minimum};
    constexpr std::size_t maximum_passes = 100000;
    for (std::size_t iteration = 1; values.back() < maximum; ++iteration) {
        if (iteration >= maximum_passes) throw std::invalid_argument("DSLT C sweep exceeds the pass limit");
        const auto candidate = static_cast<double>(minimum) + static_cast<double>(interval) * iteration;
        if (!std::isfinite(candidate) || candidate >= maximum) {
            if (values.back() != maximum) values.push_back(maximum);
            break;
        }
        values.push_back(static_cast<float>(candidate));
    }
    return values;
}

std::vector<float> descending_threshold_schedule(float minimum, float maximum, float interval) {
    if (!std::isfinite(minimum) || !std::isfinite(maximum) || !std::isfinite(interval) ||
        minimum > maximum || interval <= 0.0F) {
        throw std::invalid_argument(
            "threshold sweep requires finite ordered bounds and a positive interval");
    }
    std::vector<float> values{maximum};
    constexpr std::size_t maximum_passes = 100000;
    for (std::size_t iteration = 1; values.back() > minimum; ++iteration) {
        if (iteration >= maximum_passes) {
            throw std::invalid_argument("threshold sweep exceeds the pass limit");
        }
        const auto candidate = static_cast<double>(maximum) - static_cast<double>(interval) * iteration;
        if (!std::isfinite(candidate) || candidate <= minimum) {
            if (values.back() != minimum) values.push_back(minimum);
            break;
        }
        values.push_back(static_cast<float>(candidate));
    }
    return values;
}

void validate_crop(const dslt_volume_descriptor& descriptor, const CropParameters& crop) {
    if (!crop.enabled) return;
    if (crop.upper > crop.lower || crop.border_xy < 0) {
        throw std::invalid_argument("crop bounds are invalid");
    }
    const auto expected_height_map = static_cast<std::size_t>(descriptor.width) * descriptor.height;
    if (crop.use_height_map && crop.height_map.size() != expected_height_map) {
        throw std::invalid_argument("crop height map dimensions do not match");
    }
    if (crop.use_height_map &&
        !std::all_of(crop.height_map.begin(), crop.height_map.end(),
            [](float value) { return std::isfinite(value); })) {
        throw std::invalid_argument("crop height map must contain only finite values");
    }
}

void apply_crop(
    std::span<float> mask,
    const dslt_volume_descriptor& descriptor,
    const CropParameters& crop,
    float outside_value) {
    if (!crop.enabled) return;
    const auto border = static_cast<std::size_t>(crop.border_xy);
    for (std::size_t z = 0; z < descriptor.depth; ++z) {
        for (std::size_t y = 0; y < descriptor.height; ++y) {
            for (std::size_t x = 0; x < descriptor.width; ++x) {
                const auto outside_border =
                    border >= descriptor.width || border >= descriptor.height ||
                    x < border || x >= descriptor.width - border ||
                    y < border || y >= descriptor.height - border;
                auto outside_depth = false;
                if (crop.use_height_map) {
                    const auto surface = crop.height_map[y * descriptor.width + x];
                    outside_depth = static_cast<float>(z) < surface + static_cast<float>(crop.upper) ||
                        static_cast<float>(z) > surface + static_cast<float>(crop.lower);
                } else {
                    outside_depth = static_cast<long long>(z) < crop.upper ||
                        static_cast<long long>(z) > crop.lower;
                }
                if (outside_border || outside_depth) {
                    mask[flat(x, y, z, descriptor.width, descriptor.height)] = outside_value;
                }
            }
        }
    }
}

void squared_distance_transform_1d(
    std::span<const float> input,
    std::span<float> output) {
    if (input.size() != output.size() || input.empty()) {
        throw std::invalid_argument("distance-transform line dimensions are invalid");
    }
    const auto count = input.size();
    std::vector<std::size_t> sites(count);
    std::vector<float> boundaries(count + 1);
    std::size_t envelope = 0;
    sites[0] = 0;
    boundaries[0] = -std::numeric_limits<float>::infinity();
    boundaries[1] = std::numeric_limits<float>::infinity();
    for (std::size_t q = 1; q < count; ++q) {
        auto intersection = 0.0F;
        while (true) {
            const auto site = sites[envelope];
            const auto qf = static_cast<float>(q);
            const auto sitef = static_cast<float>(site);
            const auto numerator =
                (input[q] + qf * qf) -
                (input[site] + sitef * sitef);
            intersection = numerator /
                (2.0F * static_cast<float>(q - site));
            if (intersection > boundaries[envelope] || envelope == 0) break;
            --envelope;
        }
        ++envelope;
        sites[envelope] = q;
        boundaries[envelope] = intersection;
        boundaries[envelope + 1] = std::numeric_limits<float>::infinity();
    }
    envelope = 0;
    for (std::size_t q = 0; q < count; ++q) {
        while (boundaries[envelope + 1] < static_cast<float>(q)) ++envelope;
        const auto delta = static_cast<float>(q) - static_cast<float>(sites[envelope]);
        output[q] = delta * delta + input[sites[envelope]];
    }
}

std::vector<Vec3> surface_normals(
    std::span<const float> height,
    const dslt_volume_descriptor& descriptor) {
    const auto expected = static_cast<std::size_t>(descriptor.width) * descriptor.height;
    if (height.size() != expected) throw std::invalid_argument("height surface dimensions do not match");
    std::vector<Vec3> normals(expected, Vec3{0.0F, 0.0F, 0.0F});
    const auto add = [](Vec3& target, float x, float y) {
        target.x += x;
        target.y += y;
        target.z += 1.0F;
    };
    for (std::size_t y = 0; y < descriptor.height; ++y) {
        for (std::size_t x = 0; x < descriptor.width; ++x) {
            const auto index = y * descriptor.width + x;
            const auto center = height[index];
            if (x + 1 < descriptor.width && y + 1 < descriptor.height) {
                add(normals[index], -(height[index + 1] - center),
                    -(height[index + descriptor.width] - center));
            }
            if (x > 0 && y + 1 < descriptor.height) {
                add(normals[index], height[index - 1] - center,
                    -(height[index + descriptor.width] - center));
            }
            if (x > 0 && y > 0) {
                add(normals[index], height[index - 1] - center,
                    height[index - descriptor.width] - center);
            }
            if (x + 1 < descriptor.width && y > 0) {
                add(normals[index], height[index + 1] - center,
                    height[index - descriptor.width] - center);
            }
            const auto length = std::sqrt(
                normals[index].x * normals[index].x +
                normals[index].y * normals[index].y +
                normals[index].z * normals[index].z);
            if (length > 0.0F) {
                normals[index].x /= length;
                normals[index].y /= length;
                normals[index].z /= length;
            } else {
                normals[index] = {0.0F, 0.0F, 1.0F};
            }
        }
    }
    return normals;
}

void validate_height_surface(
    std::span<const float> height,
    const dslt_volume_descriptor& descriptor) {
    const auto expected = static_cast<std::size_t>(descriptor.width) * descriptor.height;
    if (height.size() != expected) {
        throw std::invalid_argument("provided height surface dimensions do not match the volume");
    }
    if (!std::all_of(height.begin(), height.end(), [](float value) {
            return std::isfinite(value);
        })) {
        throw std::invalid_argument("provided height surface must contain only finite values");
    }
}

} // namespace

std::vector<float> threshold_sweep_schedule(float minimum, float maximum, float interval) {
    return descending_threshold_schedule(minimum, maximum, interval);
}

std::vector<float> dslt_c_schedule(float minimum, float maximum, float interval) {
    return c_schedule(minimum, maximum, interval);
}

std::vector<float> adaptive_threshold(
    const Volume& volume,
    int radius,
    int kernel_type,
    float constant_c,
    bool include_z,
    const Engine::Progress& progress) {
    if (radius < 0 || radius > 100) {
        throw std::invalid_argument("adaptive threshold radius must be between 0 and 100");
    }
    if (kernel_type != 0 && kernel_type != 1) {
        throw std::invalid_argument("adaptive threshold kernel must be 0 (Gaussian) or 1 (mean)");
    }
    if (!std::isfinite(constant_c)) {
        throw std::invalid_argument("adaptive threshold C must be finite");
    }
    const auto weights = line_weights(radius, kernel_type == 0);
    const auto& descriptor = volume.descriptor();
    const auto axis_count = include_z ? 3.0F : 2.0F;
    const auto source = selected_channel(volume);
    auto local = convolve_axis_clamp(
        source, descriptor, weights, ConvolutionAxis::x,
        mapped_progress(progress, 0.0F, 0.8F / axis_count));
    local = convolve_axis_clamp(
        local, descriptor, weights, ConvolutionAxis::y,
        mapped_progress(progress, 0.8F / axis_count, 0.8F / axis_count));
    if (include_z) {
        local = convolve_axis_clamp(
            local, descriptor, weights, ConvolutionAxis::z,
            mapped_progress(progress, 1.6F / axis_count, 0.8F / axis_count));
    }
    for (std::size_t index = 0; index < local.size(); ++index) {
        local[index] = source[index] > local[index] - constant_c ? 0.8F : 0.0F;
        if ((index & 0xffffU) == 0 && progress &&
            !progress(0.8F + 0.2F * static_cast<float>(index) /
                static_cast<float>(std::max(local.size(), std::size_t{1})))) {
            throw std::runtime_error("cancelled");
        }
    }
    report(progress, 1, 1);
    return local;
}

std::vector<float> h_minima(
    const Volume& volume,
    float height,
    int check_interval,
    const Engine::Progress& progress) {
    if (!std::isfinite(height) || height < 0.0F || height > 1.0F) {
        throw std::invalid_argument("h-minima height must be finite and between 0 and 1");
    }
    if (check_interval < 1 || check_interval > 10000) {
        throw std::invalid_argument("h-minima check interval must be between 1 and 10000");
    }
    const auto source = selected_channel(volume);
    if (!std::all_of(source.begin(), source.end(), [](float value) { return std::isfinite(value); })) {
        throw std::invalid_argument("h-minima source must contain only finite values");
    }
    auto current = source;
    for (auto& value : current) {
        value += height;
        if (!std::isfinite(value)) {
            throw std::invalid_argument("h-minima marker overflowed float range");
        }
    }
    auto checkpoint = current;
    const auto& descriptor = volume.descriptor();
    const auto checkpoint_margin = static_cast<std::size_t>(check_interval) * 2;
    if (source.size() > std::numeric_limits<std::size_t>::max() - checkpoint_margin) {
        throw ResourceLimitError("h-minima iteration limit overflowed size_t");
    }
    const auto maximum_iterations = source.size() + checkpoint_margin;
    auto since_check = 0;
    auto converged = false;
    for (std::size_t iteration = 0; iteration < maximum_iterations; ++iteration) {
        const auto iteration_start = 0.95F * static_cast<float>(iteration) /
            static_cast<float>(std::max(maximum_iterations, std::size_t{1}));
        const auto iteration_length = 0.95F /
            static_cast<float>(std::max(maximum_iterations, std::size_t{1}));
        auto previous_iteration = std::move(current);
        current = minimum_axis_radius_one(
            previous_iteration, descriptor, ConvolutionAxis::x,
            mapped_progress(progress, iteration_start, iteration_length * 0.25F));
        current = minimum_axis_radius_one(
            current, descriptor, ConvolutionAxis::y,
            mapped_progress(progress, iteration_start + iteration_length * 0.25F, iteration_length * 0.25F));
        current = minimum_axis_radius_one(
            current, descriptor, ConvolutionAxis::z,
            mapped_progress(progress, iteration_start + iteration_length * 0.50F, iteration_length * 0.25F));
        for (std::size_t index = 0; index < current.size(); ++index) {
            current[index] = std::max(current[index], source[index]);
        }
        if (progress && !progress(iteration_start + iteration_length)) {
            throw std::runtime_error("cancelled");
        }
        if (current == previous_iteration) {
            converged = true;
            break;
        }
        if (++since_check >= check_interval) {
            if (current == checkpoint) {
                converged = true;
                break;
            }
            checkpoint = current;
            since_check = 0;
        }
    }
    if (!converged) {
        throw ResourceLimitError("h-minima reconstruction did not converge within the checked iteration limit");
    }
    std::vector<float> result(source.size());
    constexpr float residual_threshold = 0.00001F;
    for (std::size_t index = 0; index < result.size(); ++index) {
        result[index] = current[index] - source[index] < residual_threshold ? 0.8F : 0.0F;
        if ((index & 0xffffU) == 0 && progress &&
            !progress(0.95F + 0.05F * static_cast<float>(index) /
                static_cast<float>(std::max(result.size(), std::size_t{1})))) {
            throw std::runtime_error("cancelled");
        }
    }
    report(progress, 1, 1);
    return result;
}

DsltWorkEstimate estimate_dslt_work(
    const dslt_volume_descriptor& descriptor,
    int radius,
    int direction_level,
    bool segmentation,
    float minimum_c,
    float maximum_c,
    float c_interval) {
    if (descriptor.width == 0 || descriptor.height == 0 || descriptor.depth == 0) {
        throw std::invalid_argument("DSLT work estimate requires non-zero volume dimensions");
    }
    if (radius < 1 || radius > 127) throw std::invalid_argument("DSLT radius must be between 1 and 127");
    if (direction_level < 1 || direction_level > 5) {
        throw std::invalid_argument("DSLT direction level must be between 1 and 5");
    }

    std::uint64_t direction_scale = 1;
    for (int level = 0; level < direction_level; ++level) {
        direction_scale = checked_multiply_u64(direction_scale, 4, "direction count");
    }
    const auto direction_count = checked_add_u64(
        checked_multiply_u64(5, direction_scale, "direction count"), 1, "direction count");
    const auto radius_u64 = static_cast<std::uint64_t>(radius);
    const auto line_samples = checked_multiply_u64(radius_u64, radius_u64 + 2, "line sample count");
    auto voxel_count = checked_multiply_u64(descriptor.width, descriptor.height, "voxel count");
    voxel_count = checked_multiply_u64(voxel_count, descriptor.depth, "voxel count");
    const auto directional_work = checked_multiply_u64(
        checked_multiply_u64(voxel_count, direction_count, "directional work"),
        line_samples, "directional work");
    const auto bytes_per_voxel = segmentation ? 48ULL : 16ULL;
    auto host_bytes = checked_multiply_u64(voxel_count, bytes_per_voxel, "host memory");
    host_bytes = checked_add_u64(
        host_bytes,
        checked_multiply_u64(direction_count, sizeof(Vec3), "direction memory"),
        "host memory");
    const auto sweep_passes = segmentation
        ? static_cast<std::uint64_t>(c_schedule(minimum_c, maximum_c, c_interval).size())
        : 1ULL;
    constexpr std::uint64_t work_item_limit = 10'000'000'000'000ULL;
    constexpr std::uint64_t host_memory_limit = 16ULL * 1024ULL * 1024ULL * 1024ULL;
    return {
        voxel_count,
        direction_count,
        line_samples,
        directional_work,
        host_bytes,
        sweep_passes,
        work_item_limit,
        host_memory_limit,
        directional_work <= work_item_limit && host_bytes <= host_memory_limit,
    };
}

void enforce_dslt_work_limits(const DsltWorkEstimate& estimate) {
    if (estimate.within_limits) return;
    std::ostringstream message;
    message << "DSLT resource limit exceeded: work=" << estimate.directional_work_items
            << "/" << estimate.work_item_limit
            << ", host_bytes=" << estimate.estimated_host_bytes
            << "/" << estimate.host_memory_limit_bytes
            << ", directions=" << estimate.direction_count
            << ", line_samples_per_voxel=" << estimate.line_samples_per_voxel
            << ", sweep_passes=" << estimate.sweep_passes;
    throw ResourceLimitError(message.str());
}

std::vector<std::int32_t> connected_components(
    const Volume& volume,
    float threshold_value,
    int connectivity,
    int minimum_size,
    std::uint32_t& component_count,
    const Engine::Progress& progress) {
    if (connectivity != 6 && connectivity != 18 && connectivity != 26) {
        throw std::invalid_argument("connectivity must be 6, 18, or 26");
    }
    if (minimum_size < 1) throw std::invalid_argument("minimum component size must be positive");
    const auto source = selected_channel(volume);
    const auto& d = volume.descriptor();
    std::vector<std::int32_t> labels(source.size(), -1);
    std::vector<std::size_t> queue;
    std::vector<std::size_t> component;
    component_count = 0;

    for (std::size_t z = 0; z < d.depth; ++z) {
        for (std::size_t y = 0; y < d.height; ++y) {
            for (std::size_t x = 0; x < d.width; ++x) {
                const auto seed = flat(x, y, z, d.width, d.height);
                if (source[seed] < threshold_value || labels[seed] != -1) continue;
                queue.clear();
                component.clear();
                queue.push_back(seed);
                labels[seed] = -2;
                for (std::size_t head = 0; head < queue.size(); ++head) {
                    const auto id = queue[head];
                    component.push_back(id);
                    const auto cz = id / (d.width * d.height);
                    const auto rem = id % (d.width * d.height);
                    const auto cy = rem / d.width;
                    const auto cx = rem % d.width;
                    for (int dz = -1; dz <= 1; ++dz) {
                        for (int dy = -1; dy <= 1; ++dy) {
                            for (int dx = -1; dx <= 1; ++dx) {
                                if (dx == 0 && dy == 0 && dz == 0) continue;
                                const int manhattan = std::abs(dx) + std::abs(dy) + std::abs(dz);
                                if (connectivity == 6 && manhattan != 1) continue;
                                if (connectivity == 18 && manhattan == 3) continue;
                                const auto nx = static_cast<long long>(cx) + dx;
                                const auto ny = static_cast<long long>(cy) + dy;
                                const auto nz = static_cast<long long>(cz) + dz;
                                if (nx < 0 || ny < 0 || nz < 0 || nx >= d.width || ny >= d.height || nz >= d.depth) continue;
                                const auto neighbor = flat(nx, ny, nz, d.width, d.height);
                                if (source[neighbor] >= threshold_value && labels[neighbor] == -1) {
                                    labels[neighbor] = -2;
                                    queue.push_back(neighbor);
                                }
                            }
                        }
                    }
                }
                if (component.size() >= static_cast<std::size_t>(minimum_size)) {
                    const auto label = static_cast<std::int32_t>(component_count++);
                    for (const auto id : component) labels[id] = label;
                } else {
                    for (const auto id : component) labels[id] = -1;
                }
            }
        }
        report(progress, z + 1, d.depth);
    }
    return labels;
}

ComponentLabels connected_components_low_6(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    float maximum_value,
    int minimum_size_exclusive,
    std::span<const std::int32_t> excluded_labels,
    const Engine::Progress& progress) {
    const auto expected = static_cast<std::size_t>(descriptor.width) * descriptor.height * descriptor.depth;
    if (source.size() != expected) throw std::invalid_argument("component buffer dimensions do not match");
    if (!excluded_labels.empty() && excluded_labels.size() != expected) {
        throw std::invalid_argument("excluded label dimensions do not match");
    }
    if (!std::isfinite(maximum_value)) throw std::invalid_argument("component maximum value must be finite");
    if (minimum_size_exclusive < 0) throw std::invalid_argument("exclusive minimum component size must be non-negative");

    ComponentLabels result{std::vector<std::int32_t>(source.size(), -1), 0};
    std::vector<std::uint8_t> visited(source.size(), 0);
    std::vector<std::size_t> queue;
    std::vector<std::size_t> component;
    constexpr std::array<std::array<int, 3>, 6> neighbors{{
        {{-1, 0, 0}}, {{1, 0, 0}}, {{0, -1, 0}},
        {{0, 1, 0}}, {{0, 0, -1}}, {{0, 0, 1}},
    }};

    for (std::size_t z = 0; z < descriptor.depth; ++z) {
        for (std::size_t y = 0; y < descriptor.height; ++y) {
            for (std::size_t x = 0; x < descriptor.width; ++x) {
                const auto seed = flat(x, y, z, descriptor.width, descriptor.height);
                if (visited[seed] != 0 || source[seed] > maximum_value ||
                    (!excluded_labels.empty() && excluded_labels[seed] >= 0)) continue;
                queue.clear();
                component.clear();
                queue.push_back(seed);
                visited[seed] = 1;
                for (std::size_t head = 0; head < queue.size(); ++head) {
                    const auto id = queue[head];
                    component.push_back(id);
                    const auto cz = id / (descriptor.width * descriptor.height);
                    const auto remainder = id % (descriptor.width * descriptor.height);
                    const auto cy = remainder / descriptor.width;
                    const auto cx = remainder % descriptor.width;
                    for (const auto& offset : neighbors) {
                        const auto nx = static_cast<long long>(cx) + offset[0];
                        const auto ny = static_cast<long long>(cy) + offset[1];
                        const auto nz = static_cast<long long>(cz) + offset[2];
                        if (nx < 0 || ny < 0 || nz < 0 ||
                            nx >= descriptor.width || ny >= descriptor.height || nz >= descriptor.depth) continue;
                        const auto neighbor = flat(nx, ny, nz, descriptor.width, descriptor.height);
                        if (visited[neighbor] == 0 && source[neighbor] <= maximum_value &&
                            (excluded_labels.empty() || excluded_labels[neighbor] < 0)) {
                            visited[neighbor] = 1;
                            queue.push_back(neighbor);
                        }
                    }
                }
                if (component.size() > static_cast<std::size_t>(minimum_size_exclusive)) {
                    if (result.component_count >= static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max())) {
                        throw std::overflow_error("component label count exceeds int32 capacity");
                    }
                    const auto label = static_cast<std::int32_t>(result.component_count++);
                    for (const auto id : component) result.labels[id] = label;
                }
            }
        }
        report(progress, z + 1, descriptor.depth);
    }
    return result;
}

ComponentLabels dslt_segmentation_from_response(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    const DsltResponse& response,
    const DsltSegmentationParameters& parameters,
    const Engine::Progress& progress) {
    if (parameters.radius < 1 || parameters.radius > 127) {
        throw std::invalid_argument("DSLT radius must be between 1 and 127");
    }
    if (parameters.direction_level < 1 || parameters.direction_level > 5) {
        throw std::invalid_argument("DSLT direction level must be between 1 and 5");
    }
    if (parameters.kernel_type != 0 && parameters.kernel_type != 1) {
        throw std::invalid_argument("DSLT kernel type must be 0 (Gaussian) or 1 (mean)");
    }
    if (!std::isfinite(parameters.z_correction_factor) || parameters.z_correction_factor < 0.0F) {
        throw std::invalid_argument("DSLT Z correction factor must be finite and non-negative");
    }
    if (parameters.closing_radius < 0 || parameters.closing_radius > 64) {
        throw std::invalid_argument("DSLT closing radius must be between 0 and 64");
    }
    if (parameters.minimum_component_size < 0 || parameters.minimum_invalid_structure_area < 0) {
        throw std::invalid_argument("DSLT component and invalid-structure limits must be non-negative");
    }
    validate_crop(descriptor, parameters.crop);

    const auto values = c_schedule(parameters.minimum_c, parameters.maximum_c, parameters.c_interval);
    enforce_dslt_work_limits(estimate_dslt_work(
        descriptor, parameters.radius, parameters.direction_level, true,
        parameters.minimum_c, parameters.maximum_c, parameters.c_interval));
    const auto expected = static_cast<std::size_t>(descriptor.width) * descriptor.height * descriptor.depth;
    if (source.size() != expected || response.minimum.size() != expected || response.alpha.size() != expected) {
        throw std::invalid_argument("DSLT sweep response dimensions do not match the source volume");
    }
    ComponentLabels result{std::vector<std::int32_t>(source.size(), -1), 0};
    auto previous_thickness = 0;
    const auto pass_length = 1.0F / static_cast<float>(values.size());

    for (std::size_t pass = 0; pass < values.size(); ++pass) {
        const auto pass_start = static_cast<float>(pass) * pass_length;
        auto mask = apply_dslt_threshold(
            source, descriptor, response, values[pass], parameters.z_correction_factor,
            mapped_progress(progress, pass_start, pass_length * 0.15F));
        mask = spherical_closing(
            mask, descriptor, parameters.closing_radius,
            mapped_progress(progress, pass_start + pass_length * 0.15F, pass_length * 0.20F));
        for (std::size_t index = 0; index < mask.size(); ++index) {
            if (result.labels[index] >= 0) mask[index] = 0.0F;
        }
        apply_crop(mask, descriptor, parameters.crop, 0.0F);

        const auto thickness = estimate_wall_thickness(
            mask, descriptor, previous_thickness / 2,
            mapped_progress(progress, pass_start + pass_length * 0.35F, pass_length * 0.25F));
        previous_thickness = thickness;
        apply_crop(mask, descriptor, parameters.crop, 0.8F);
        const auto candidates = connected_components_low_6(
            mask, descriptor, 0.1F, parameters.minimum_component_size, result.labels,
            mapped_progress(progress, pass_start + pass_length * 0.60F, pass_length * 0.15F));

        const auto final_pass = pass + 1 == values.size();
        const auto invalid_limit = static_cast<std::uint64_t>(parameters.minimum_invalid_structure_area) *
            static_cast<std::uint64_t>(thickness);
        std::uint32_t invalid_components = 0;
        for (std::uint32_t component = 0; component < candidates.component_count; ++component) {
            const auto component_progress = mapped_progress(
                progress,
                pass_start + pass_length * (0.75F + 0.25F * static_cast<float>(component) /
                    static_cast<float>(std::max(candidates.component_count, 1U))),
                pass_length * 0.25F / static_cast<float>(std::max(candidates.component_count, 1U)));
            const auto invalid_voxels = final_pass
                ? 0U
                : invalid_structure_voxels(
                    candidates.labels, result.labels, static_cast<std::int32_t>(component), descriptor,
                    thickness / 2, component_progress);
            if (!final_pass && invalid_voxels > invalid_limit) {
                ++invalid_components;
                continue;
            }
            if (result.component_count >= static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max())) {
                throw std::overflow_error("component label count exceeds int32 capacity");
            }
            const auto label = static_cast<std::int32_t>(result.component_count++);
            for (std::size_t index = 0; index < candidates.labels.size(); ++index) {
                if (candidates.labels[index] == static_cast<std::int32_t>(component)) result.labels[index] = label;
            }
        }
        if (progress && !progress(pass_start + pass_length)) throw std::runtime_error("cancelled");
        result.passes_completed = static_cast<std::uint32_t>(pass + 1);
        if (invalid_components == 0) break;
    }
    report(progress, 1, 1);
    return result;
}

ComponentLabels threshold_sweep(
    const Volume& volume,
    const ThresholdSweepParameters& parameters,
    const Engine::Progress& progress) {
    if (parameters.closing_radius < 0 || parameters.closing_radius > 64) {
        throw std::invalid_argument("threshold sweep closing radius must be between 0 and 64");
    }
    if (parameters.minimum_component_size < 0 || parameters.minimum_invalid_structure_area < 0) {
        throw std::invalid_argument(
            "threshold sweep component and invalid-structure limits must be non-negative");
    }
    const auto values = threshold_sweep_schedule(
        parameters.minimum_threshold, parameters.maximum_threshold, parameters.interval);
    const auto& descriptor = volume.descriptor();
    validate_crop(descriptor, parameters.crop);
    const auto source = selected_channel(volume);
    ComponentLabels result{std::vector<std::int32_t>(source.size(), -1), 0};
    auto previous_thickness = 0;
    const auto pass_length = 1.0F / static_cast<float>(values.size());

    for (std::size_t pass = 0; pass < values.size(); ++pass) {
        const auto pass_start = static_cast<float>(pass) * pass_length;
        std::vector<float> mask(source.size(), 0.0F);
        for (std::size_t index = 0; index < source.size(); ++index) {
            mask[index] = source[index] >= values[pass] ? 0.8F : 0.0F;
            if ((index & 0xffffU) == 0 && progress &&
                !progress(pass_start + pass_length * 0.10F *
                    static_cast<float>(index) / static_cast<float>(std::max(source.size(), std::size_t{1})))) {
                throw std::runtime_error("cancelled");
            }
        }
        mask = spherical_closing(
            mask, descriptor, parameters.closing_radius,
            mapped_progress(progress, pass_start + pass_length * 0.10F, pass_length * 0.25F));
        for (std::size_t index = 0; index < mask.size(); ++index) {
            if (result.labels[index] >= 0) mask[index] = 0.0F;
        }
        apply_crop(mask, descriptor, parameters.crop, 0.0F);

        const auto thickness = estimate_wall_thickness(
            mask, descriptor, previous_thickness / 2,
            mapped_progress(progress, pass_start + pass_length * 0.35F, pass_length * 0.25F));
        previous_thickness = thickness;
        apply_crop(mask, descriptor, parameters.crop, 0.8F);
        const auto candidates = connected_components_low_6(
            mask, descriptor, 0.1F, parameters.minimum_component_size, result.labels,
            mapped_progress(progress, pass_start + pass_length * 0.60F, pass_length * 0.15F));

        const auto final_pass = pass + 1 == values.size();
        const auto invalid_limit = static_cast<std::uint64_t>(parameters.minimum_invalid_structure_area) *
            static_cast<std::uint64_t>(thickness);
        std::uint32_t invalid_components = 0;
        for (std::uint32_t component = 0; component < candidates.component_count; ++component) {
            const auto component_count = std::max(candidates.component_count, 1U);
            const auto component_progress = mapped_progress(
                progress,
                pass_start + pass_length * (0.75F + 0.25F * static_cast<float>(component) /
                    static_cast<float>(component_count)),
                pass_length * 0.25F / static_cast<float>(component_count));
            const auto invalid_voxels = final_pass
                ? 0U
                : invalid_structure_voxels(
                    candidates.labels, result.labels, static_cast<std::int32_t>(component), descriptor,
                    thickness / 2, component_progress);
            if (!final_pass && invalid_voxels > invalid_limit) {
                ++invalid_components;
                continue;
            }
            if (result.component_count >= static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max())) {
                throw std::overflow_error("component label count exceeds int32 capacity");
            }
            const auto label = static_cast<std::int32_t>(result.component_count++);
            for (std::size_t index = 0; index < candidates.labels.size(); ++index) {
                if (candidates.labels[index] == static_cast<std::int32_t>(component)) {
                    result.labels[index] = label;
                }
            }
        }
        if (progress && !progress(pass_start + pass_length)) throw std::runtime_error("cancelled");
        result.passes_completed = static_cast<std::uint32_t>(pass + 1);
        if (invalid_components == 0) break;
    }
    report(progress, 1, 1);
    return result;
}

ComponentLabels dslt_segmentation(
    const Volume& volume,
    const DsltSegmentationParameters& parameters,
    const Engine::Progress& progress) {
    if (parameters.radius < 1 || parameters.radius > 127 ||
        parameters.direction_level < 1 || parameters.direction_level > 5 ||
        (parameters.kernel_type != 0 && parameters.kernel_type != 1)) {
        throw std::invalid_argument("DSLT segmentation response parameters are outside the supported range");
    }
    if (!std::isfinite(parameters.z_correction_factor) || parameters.z_correction_factor < 0.0F ||
        parameters.closing_radius < 0 || parameters.closing_radius > 64 ||
        parameters.minimum_component_size < 0 || parameters.minimum_invalid_structure_area < 0) {
        throw std::invalid_argument("DSLT segmentation limits are outside the supported range");
    }
    static_cast<void>(c_schedule(parameters.minimum_c, parameters.maximum_c, parameters.c_interval));
    enforce_dslt_work_limits(estimate_dslt_work(
        volume.descriptor(), parameters.radius, parameters.direction_level, true,
        parameters.minimum_c, parameters.maximum_c, parameters.c_interval));
    const auto response = dslt_response(
        volume, parameters.radius, parameters.direction_level, parameters.kernel_type,
        mapped_progress(progress, 0.0F, 0.45F));
    const auto source = selected_channel(volume);
    return dslt_segmentation_from_response(
        source, volume.descriptor(), response, parameters,
        mapped_progress(progress, 0.45F, 0.55F));
}

ComponentLabels watershed(
    const Volume& volume,
    std::span<const std::int32_t> seed_labels,
    std::span<const std::int32_t> selected_labels,
    int minimum_seed_size,
    const CropParameters& crop,
    const Engine::Progress& progress) {
    if (minimum_seed_size < 0) {
        throw std::invalid_argument("watershed minimum seed size must be non-negative");
    }
    if (seed_labels.size() != volume.voxel_count()) {
        throw std::invalid_argument("watershed seed dimensions do not match the volume");
    }
    if (selected_labels.empty()) {
        throw std::invalid_argument("watershed requires at least one selected seed label");
    }

    const auto& descriptor = volume.descriptor();
    validate_crop(descriptor, crop);
    auto intensity = selected_channel(volume);
    apply_crop(intensity, descriptor, crop, std::numeric_limits<float>::max());

    std::unordered_set<std::int32_t> selected;
    selected.reserve(selected_labels.size());
    for (const auto label : selected_labels) {
        if (label < 0) throw std::invalid_argument("watershed selected seed labels must be non-negative");
        selected.insert(label);
    }
    std::unordered_map<std::int32_t, std::size_t> seed_sizes;
    for (const auto label : seed_labels) {
        if (label < -1) throw std::invalid_argument("watershed seed labels must be -1 or non-negative");
        if (label >= 0) ++seed_sizes[label];
    }
    for (const auto label : selected) {
        if (!seed_sizes.contains(label)) {
            throw std::invalid_argument("watershed selected seed label does not exist");
        }
    }

    std::vector<std::int32_t> labels(seed_labels.size(), -1);
    for (std::size_t index = 0; index < seed_labels.size(); ++index) {
        const auto label = seed_labels[index];
        if (label >= 0 && selected.contains(label) &&
            seed_sizes[label] >= static_cast<std::size_t>(minimum_seed_size)) {
            labels[index] = label;
        }
    }
    if (std::none_of(labels.begin(), labels.end(), [](std::int32_t label) { return label >= 0; })) {
        throw std::invalid_argument("watershed has no selected seeds at the requested minimum size");
    }

    constexpr std::array<std::array<int, 3>, 6> neighbor_priority{{
        {{0, 0, -1}}, {{-1, 0, 0}}, {{0, -1, 0}},
        {{1, 0, 0}}, {{0, 1, 0}}, {{0, 0, 1}},
    }};
    const auto neighbor = [&](std::size_t x, std::size_t y, std::size_t z,
                              const std::array<int, 3>& delta) -> std::size_t {
        const auto nx = static_cast<long long>(x) + delta[0];
        const auto ny = static_cast<long long>(y) + delta[1];
        const auto nz = static_cast<long long>(z) + delta[2];
        if (nx < 0 || ny < 0 || nz < 0 ||
            nx >= descriptor.width || ny >= descriptor.height || nz >= descriptor.depth) {
            return labels.size();
        }
        return flat(static_cast<std::size_t>(nx), static_cast<std::size_t>(ny),
                    static_cast<std::size_t>(nz), descriptor.width, descriptor.height);
    };

    auto next = labels;
    for (std::size_t level = 1; level <= 256; ++level) {
        const auto threshold_value = static_cast<float>(level) / 256.0F;
        bool changed;
        do {
            changed = false;
            next = labels;
            for (std::size_t z = 0; z < descriptor.depth; ++z) {
                for (std::size_t y = 0; y < descriptor.height; ++y) {
                    for (std::size_t x = 0; x < descriptor.width; ++x) {
                        const auto index = flat(x, y, z, descriptor.width, descriptor.height);
                        if (labels[index] >= 0 || intensity[index] > threshold_value) continue;
                        for (const auto& delta : neighbor_priority) {
                            const auto candidate = neighbor(x, y, z, delta);
                            if (candidate != labels.size() && labels[candidate] >= 0) {
                                next[index] = labels[candidate];
                                changed = true;
                                break;
                            }
                        }
                    }
                }
            }
            labels.swap(next);
            if (progress && !progress(static_cast<float>(level - 1) / 256.0F)) {
                throw std::runtime_error("cancelled");
            }
        } while (changed);

        // The legacy path applies a synchronous radius-one label opening after
        // every flood level. Image borders are ignored, matching the CUDA guards.
        next = labels;
        for (std::size_t z = 0; z < descriptor.depth; ++z) {
            for (std::size_t y = 0; y < descriptor.height; ++y) {
                for (std::size_t x = 0; x < descriptor.width; ++x) {
                    const auto index = flat(x, y, z, descriptor.width, descriptor.height);
                    if (labels[index] < 0) continue;
                    for (const auto& delta : neighbor_priority) {
                        const auto candidate = neighbor(x, y, z, delta);
                        if (candidate != labels.size() && labels[candidate] != labels[index]) {
                            next[index] = -1;
                            break;
                        }
                    }
                }
            }
        }
        labels.swap(next);
        next = labels;
        for (std::size_t z = 0; z < descriptor.depth; ++z) {
            for (std::size_t y = 0; y < descriptor.height; ++y) {
                for (std::size_t x = 0; x < descriptor.width; ++x) {
                    const auto index = flat(x, y, z, descriptor.width, descriptor.height);
                    if (labels[index] >= 0) continue;
                    for (const auto& delta : neighbor_priority) {
                        const auto candidate = neighbor(x, y, z, delta);
                        if (candidate != labels.size() && labels[candidate] >= 0) {
                            next[index] = labels[candidate];
                            break;
                        }
                    }
                }
            }
        }
        labels.swap(next);
        if (progress && !progress(static_cast<float>(level) / 256.0F)) {
            throw std::runtime_error("cancelled");
        }
    }

    std::unordered_set<std::int32_t> components;
    for (const auto label : labels) {
        if (label >= 0) components.insert(label);
    }
    report(progress, 1, 1);
    return {std::move(labels), static_cast<std::uint32_t>(components.size()), 256};
}

std::vector<float> height_map(
    const Volume& volume,
    const HeightMapParameters& parameters,
    const Engine::Progress& progress) {
    if (parameters.xy_radius < 0 || parameters.xy_radius > 64 ||
        parameters.z_radius < 0 || parameters.z_radius > 64) {
        throw std::invalid_argument("height-map XY and Z radii must be between 0 and 64");
    }
    if (parameters.kernel_type != 0 && parameters.kernel_type != 1) {
        throw std::invalid_argument("height-map kernel must be 0 (Gaussian) or 1 (mean)");
    }
    if (parameters.smooth_level < 0 || parameters.smooth_level > 10) {
        throw std::invalid_argument("height-map smooth level must be between 0 and 10");
    }
    if (!std::isfinite(parameters.threshold)) {
        throw std::invalid_argument("height-map threshold must be finite");
    }

    const auto& descriptor = volume.descriptor();
    const auto source = selected_channel(volume);
    const auto z_weights = line_weights(parameters.z_radius, parameters.kernel_type == 0);
    const auto filtered = convolve_axis_clamp(
        source, descriptor, z_weights, ConvolutionAxis::z,
        mapped_progress(progress, 0.0F, 0.35F));

    std::vector<float> result(
        static_cast<std::size_t>(descriptor.width) * descriptor.height, 0.0F);
    for (std::size_t y = 0; y < descriptor.height; ++y) {
        for (std::size_t x = 0; x < descriptor.width; ++x) {
            auto surface = static_cast<float>(descriptor.depth - 1);
            for (std::size_t z = 0; z < descriptor.depth; ++z) {
                const auto value = filtered[flat(x, y, z, descriptor.width, descriptor.height)];
                if (value <= parameters.threshold) continue;
                if (z == 0) {
                    surface = 0.0F;
                } else {
                    const auto previous = filtered[flat(
                        x, y, z - 1, descriptor.width, descriptor.height)];
                    const auto denominator = value - previous;
                    surface = denominator == 0.0F
                        ? static_cast<float>(z - 1)
                        : static_cast<float>(z - 1) +
                            (parameters.threshold - previous) / denominator;
                }
                break;
            }
            result[y * descriptor.width + x] = surface;
        }
        if (progress && !progress(0.35F + 0.30F * static_cast<float>(y + 1) /
            static_cast<float>(descriptor.height))) {
            throw std::runtime_error("cancelled");
        }
    }

    if (parameters.smooth_level > 0) {
        auto map_descriptor = descriptor;
        map_descriptor.depth = 1;
        map_descriptor.channels = 1;
        map_descriptor.selected_channel = 0;
        map_descriptor.element_count = result.size();
        const auto xy_weights = line_weights(parameters.xy_radius, parameters.kernel_type == 0);
        const auto pass_length = 0.35F / static_cast<float>(parameters.smooth_level);
        for (int pass = 0; pass < parameters.smooth_level; ++pass) {
            const auto pass_start = 0.65F + static_cast<float>(pass) * pass_length;
            result = convolve_axis_clamp(
                result, map_descriptor, xy_weights, ConvolutionAxis::x,
                mapped_progress(progress, pass_start, pass_length * 0.5F));
            result = convolve_axis_clamp(
                result, map_descriptor, xy_weights, ConvolutionAxis::y,
                mapped_progress(progress, pass_start + pass_length * 0.5F, pass_length * 0.5F));
        }
    }
    report(progress, 1, 1);
    return result;
}

std::vector<float> depth_map(
    const Volume& volume,
    const HeightMapParameters& parameters,
    std::span<const float> height_surface,
    const Engine::Progress& progress) {
    const auto& descriptor = volume.descriptor();
    std::vector<float> generated_height;
    auto height = height_surface;
    auto processing_start = 0.0F;
    if (height.empty()) {
        generated_height = height_map(
            volume, parameters, mapped_progress(progress, 0.0F, 0.45F));
        height = std::span<const float>(generated_height);
        processing_start = 0.45F;
    } else {
        validate_height_surface(height, descriptor);
    }
    const auto width = static_cast<std::size_t>(descriptor.width);
    const auto height_count = static_cast<std::size_t>(descriptor.height);
    const auto plane = width * height_count;
    std::vector<float> result(volume.voxel_count(), 0.0F);
    std::vector<float> costs(plane);
    std::vector<float> row_pass(plane);
    std::vector<float> distance_squared(plane);
    std::vector<float> input_line(std::max(width, height_count));
    std::vector<float> output_line(input_line.size());

    for (std::size_t z = 0; z < descriptor.depth; ++z) {
        for (std::size_t index = 0; index < plane; ++index) {
            const auto delta = static_cast<float>(z) - height[index];
            costs[index] = delta * delta;
        }
        for (std::size_t y = 0; y < height_count; ++y) {
            const auto row = std::span<const float>(costs).subspan(y * width, width);
            auto transformed = std::span<float>(row_pass).subspan(y * width, width);
            squared_distance_transform_1d(row, transformed);
        }
        for (std::size_t x = 0; x < width; ++x) {
            for (std::size_t y = 0; y < height_count; ++y) {
                input_line[y] = row_pass[y * width + x];
            }
            squared_distance_transform_1d(
                std::span<const float>(input_line).first(height_count),
                std::span<float>(output_line).first(height_count));
            for (std::size_t y = 0; y < height_count; ++y) {
                distance_squared[y * width + x] = output_line[y];
            }
        }
        for (std::size_t index = 0; index < plane; ++index) {
            if (static_cast<float>(z) > height[index]) {
                result[z * plane + index] = std::sqrt(std::max(distance_squared[index], 0.0F));
            }
        }
        if (progress && !progress(processing_start + (1.0F - processing_start) *
            static_cast<float>(z + 1) / static_cast<float>(descriptor.depth))) {
            throw std::runtime_error("cancelled");
        }
    }
    report(progress, 1, 1);
    return result;
}

std::vector<float> height_projection(
    const Volume& volume,
    const HeightMapParameters& height_parameters,
    const HeightProjectionParameters& projection_parameters,
    std::span<const float> height_surface,
    const Engine::Progress& progress) {
    if (projection_parameters.mode != 0 && projection_parameters.mode != 1) {
        throw std::invalid_argument("height projection mode must be 0 (normal) or 1 (Z)");
    }
    if (projection_parameters.range < 0) {
        throw std::invalid_argument("height projection range must be non-negative");
    }
    if (!std::isfinite(projection_parameters.offset) ||
        !std::isfinite(projection_parameters.start_depth) ||
        !std::isfinite(projection_parameters.projection_threshold)) {
        throw std::invalid_argument("height projection parameters must be finite");
    }
    const auto& descriptor = volume.descriptor();
    std::vector<float> generated_surface;
    auto surface = height_surface;
    auto processing_start = 0.0F;
    if (surface.empty()) {
        generated_surface = height_map(
            volume, height_parameters, mapped_progress(progress, 0.0F, 0.45F));
        surface = std::span<const float>(generated_surface);
        processing_start = 0.45F;
    } else {
        validate_height_surface(surface, descriptor);
    }
    const auto source = selected_channel(volume);
    const auto normals = projection_parameters.mode == 0
        ? surface_normals(surface, descriptor)
        : std::vector<Vec3>{};
    std::vector<float> result(surface.size(), 0.0F);
    for (std::size_t y = 0; y < descriptor.height; ++y) {
        for (std::size_t x = 0; x < descriptor.width; ++x) {
            const auto surface_index = y * descriptor.width + x;
            auto maximum = -1.0F;
            for (int step = 0; step <= projection_parameters.range; ++step) {
                const auto distance = projection_parameters.start_depth + static_cast<float>(step);
                const auto px = projection_parameters.mode == 0
                    ? static_cast<float>(x) + normals[surface_index].x * distance
                    : static_cast<float>(x);
                const auto py = projection_parameters.mode == 0
                    ? static_cast<float>(y) + normals[surface_index].y * distance
                    : static_cast<float>(y);
                const auto pz = surface[surface_index] +
                    (projection_parameters.mode == 0
                        ? normals[surface_index].z * distance
                        : distance) +
                    projection_parameters.offset;
                const auto outside = px < 0.0F || py < 0.0F || pz < 0.0F ||
                    px > static_cast<float>(descriptor.width - 1) ||
                    py > static_cast<float>(descriptor.height - 1) ||
                    pz > static_cast<float>(descriptor.depth - 1);
                if (outside) {
                    if (projection_parameters.mode == 1 && pz < 0.0F) continue;
                    if (maximum < 0.0F) maximum = 0.0F;
                    break;
                }
                const auto value = trilinear_clamp(source, descriptor, px, py, pz);
                if (projection_parameters.mode == 0 &&
                    projection_parameters.projection_threshold > 0.0F) {
                    maximum = value > projection_parameters.projection_threshold ? 1.0F : 0.0F;
                    if (maximum == 1.0F) break;
                } else if (value > maximum) {
                    maximum = value;
                }
            }
            if (maximum < 0.0F ||
                (projection_parameters.mode == 1 &&
                 maximum < projection_parameters.projection_threshold)) {
                maximum = 0.0F;
            }
            result[surface_index] = maximum;
        }
        if (progress && !progress(processing_start + (1.0F - processing_start) *
            static_cast<float>(y + 1) / static_cast<float>(descriptor.height))) {
            throw std::runtime_error("cancelled");
        }
    }
    report(progress, 1, 1);
    return result;
}

std::uint32_t resample_output_depth(
    const dslt_volume_descriptor& descriptor,
    float target_spacing) {
    if (!(target_spacing > 0.0F) || !std::isfinite(target_spacing)) {
        throw std::invalid_argument("target Z spacing must be finite and positive");
    }
    const auto physical_depth =
        static_cast<double>(descriptor.depth) * descriptor.calibration.spacing_z;
    const auto requested_depth = physical_depth / static_cast<double>(target_spacing);
    if (!std::isfinite(requested_depth) || requested_depth < 0.0) {
        throw std::invalid_argument("calibrated Z depth must be finite and non-negative");
    }
    const auto rounded_depth = std::floor(requested_depth + 0.5);
    if (rounded_depth > static_cast<double>(std::numeric_limits<std::uint32_t>::max())) {
        throw ResourceLimitError("Z resampling output depth exceeds uint32 capacity");
    }
    return std::max<std::uint32_t>(1, static_cast<std::uint32_t>(rounded_depth));
}

std::vector<float> z_gradient(
    const Volume& volume,
    float coefficient,
    float exponent,
    float minimum,
    float maximum,
    std::span<const float> height_map,
    const Engine::Progress& progress) {
    if (!std::isfinite(coefficient) || coefficient < 0.0F) {
        throw std::invalid_argument("Z-gradient coefficient must be finite and non-negative");
    }
    if (!std::isfinite(exponent) || exponent <= 0.0F) {
        throw std::invalid_argument("Z-gradient exponent must be finite and positive");
    }
    if (!std::isfinite(minimum) || !std::isfinite(maximum) || maximum <= minimum) {
        throw std::invalid_argument("Z-gradient intensity maximum must exceed the minimum");
    }

    const auto& descriptor = volume.descriptor();
    const auto plane = static_cast<std::size_t>(descriptor.width) * descriptor.height;
    if (!height_map.empty()) {
        if (height_map.size() != plane) {
            throw std::invalid_argument("Z-gradient height map dimensions do not match the volume");
        }
        if (!std::all_of(height_map.begin(), height_map.end(), [](float value) {
                return std::isfinite(value);
            })) {
            throw std::invalid_argument("Z-gradient height map must contain only finite values");
        }
    }

    const auto source = selected_channel(volume);
    std::vector<float> result(source.size());
    const auto scale = 1.0F / (maximum - minimum);
    const auto depth = static_cast<float>(descriptor.depth);
    for (std::size_t z = 0; z < descriptor.depth; ++z) {
        for (std::size_t index = 0; index < plane; ++index) {
            const auto surface = height_map.empty() ? 0.0F : height_map[index];
            const auto distance = std::max(0.0F, static_cast<float>(z) - surface);
            const auto gain = std::pow(1.0F + coefficient * distance / depth, exponent);
            result[z * plane + index] = std::clamp(
                (source[z * plane + index] * gain - minimum) * scale,
                0.0F,
                1.0F);
        }
        report(progress, z + 1, descriptor.depth);
    }
    return result;
}

std::vector<float> resample_z(
    const Volume& volume,
    float target_spacing,
    int lanczos_order,
    bool lanczos,
    std::uint32_t& output_depth,
    const Engine::Progress& progress) {
    if (lanczos && lanczos_order != 2 && lanczos_order != 3) throw std::invalid_argument("Lanczos order must be 2 or 3");
    const auto source = selected_channel(volume);
    const auto& d = volume.descriptor();
    output_depth = resample_output_depth(d, target_spacing);
    auto output_count = checked_multiply_u64(d.width, d.height, "Z resampling output");
    output_count = checked_multiply_u64(output_count, output_depth, "Z resampling output");
    if (output_count > std::numeric_limits<std::size_t>::max()) {
        throw ResourceLimitError("Z resampling output exceeds addressable memory");
    }
    std::vector<float> result(static_cast<std::size_t>(output_count), 0.0F);
    const double scale = static_cast<double>(d.depth) / output_depth;

    for (std::size_t oz = 0; oz < output_depth; ++oz) {
        const double center = (oz + 0.5) * scale - 0.5;
        for (std::size_t y = 0; y < d.height; ++y) {
            for (std::size_t x = 0; x < d.width; ++x) {
                double sum = 0.0;
                double weight_sum = 0.0;
                if (lanczos) {
                    const int start = static_cast<int>(std::floor(center)) - lanczos_order + 1;
                    const int end = static_cast<int>(std::floor(center)) + lanczos_order;
                    for (int iz = start; iz <= end; ++iz) {
                        const auto clamped = std::clamp(iz, 0, static_cast<int>(d.depth) - 1);
                        const auto distance = static_cast<float>(center - iz);
                        const double weight = std::abs(distance) < lanczos_order
                            ? sinc(distance) * sinc(distance / lanczos_order)
                            : 0.0;
                        sum += source[flat(x, y, clamped, d.width, d.height)] * weight;
                        weight_sum += weight;
                    }
                } else {
                    const double begin = oz * scale;
                    const double end = (oz + 1) * scale;
                    const int first = static_cast<int>(std::floor(begin));
                    const int last = static_cast<int>(std::ceil(end));
                    for (int iz = first; iz < last; ++iz) {
                        const auto clamped = std::clamp(iz, 0, static_cast<int>(d.depth) - 1);
                        const double overlap = std::max(0.0, std::min(end, iz + 1.0) - std::max(begin, static_cast<double>(iz)));
                        sum += source[flat(x, y, clamped, d.width, d.height)] * overlap;
                        weight_sum += overlap;
                    }
                }
                result[flat(x, y, oz, d.width, d.height)] = weight_sum == 0.0 ? 0.0F : static_cast<float>(sum / weight_sum);
            }
        }
        report(progress, oz + 1, output_depth);
    }
    return result;
}

std::vector<float> extract_plane(const Volume& volume, dslt_operation operation, int slice, std::uint32_t& width, std::uint32_t& height) {
    const auto source = selected_channel(volume);
    const auto& d = volume.descriptor();
    std::vector<float> result;
    if (operation == DSLT_OP_EXTRACT_XY) {
        if (slice < 0 || slice >= static_cast<int>(d.depth)) throw std::invalid_argument("XY slice is outside the volume");
        width = d.width;
        height = d.height;
        result.resize(static_cast<std::size_t>(width) * height);
        std::copy_n(source.begin() + static_cast<std::ptrdiff_t>(slice) * width * height, result.size(), result.begin());
    } else if (operation == DSLT_OP_EXTRACT_YZ) {
        if (slice < 0 || slice >= static_cast<int>(d.width)) throw std::invalid_argument("YZ slice is outside the volume");
        width = d.depth;
        height = d.height;
        result.resize(static_cast<std::size_t>(width) * height);
        for (std::size_t y = 0; y < d.height; ++y)
            for (std::size_t z = 0; z < d.depth; ++z)
                result[y * width + z] = source[flat(slice, y, z, d.width, d.height)];
    } else {
        if (slice < 0 || slice >= static_cast<int>(d.height)) throw std::invalid_argument("ZX slice is outside the volume");
        width = d.width;
        height = d.depth;
        result.resize(static_cast<std::size_t>(width) * height);
        for (std::size_t z = 0; z < d.depth; ++z)
            for (std::size_t x = 0; x < d.width; ++x)
                result[z * width + x] = source[flat(x, slice, z, d.width, d.height)];
    }
    return result;
}

} // namespace dslt::ops
