#include "operations.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <deque>
#include <limits>
#include <numbers>
#include <stdexcept>

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

float sinc(float value) {
    if (std::abs(value) < 1.0e-7F) return 1.0F;
    const auto p = std::numbers::pi_v<float> * value;
    return std::sin(p) / p;
}

struct Vec3 final {
    float x;
    float y;
    float z;

    bool operator==(const Vec3&) const = default;
};

Vec3 midpoint(const Vec3& left, const Vec3& right) {
    return {(left.x + right.x) * 0.5F, (left.y + right.y) * 0.5F, (left.z + right.z) * 0.5F};
}

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

} // namespace

std::vector<float> selected_channel(const Volume& volume) {
    std::vector<float> result(volume.voxel_count());
    const auto offset = volume.voxel_count() * volume.descriptor().selected_channel;
    std::copy_n(volume.data().begin() + static_cast<std::ptrdiff_t>(offset), result.size(), result.begin());
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
    if (!std::isfinite(z_correction_factor) || z_correction_factor < 0.0F)
        throw std::invalid_argument("DSLT Z correction factor must be finite and non-negative");

    const auto directions = geodesic_directions(direction_level);
    const auto source = selected_channel(volume);
    const auto& descriptor = volume.descriptor();
    std::vector<float> result(source.size(), 0.0F);
    std::vector<float> minimum(source.size(), std::numeric_limits<float>::max());
    std::vector<float> alpha(source.size(), 0.0F);
    const auto total_steps = static_cast<std::size_t>(radius) * directions.size() +
        static_cast<std::size_t>(descriptor.depth) * descriptor.height;
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
                        if (minimum[id] > sum) {
                            minimum[id] = sum;
                            alpha[id] = direction_alpha;
                        }
                    }
                }
            }
            report(progress, ++completed_steps, total_steps);
        }
    }

    const auto constant_c_z = constant_c_xy * z_correction_factor;
    for (std::size_t z = 0; z < descriptor.depth; ++z) {
        for (std::size_t y = 0; y < descriptor.height; ++y) {
            for (std::size_t x = 0; x < descriptor.width; ++x) {
                const auto id = flat(x, y, z, descriptor.width, descriptor.height);
                const auto correction = constant_c_xy * (1.0F - alpha[id]) + constant_c_z * alpha[id];
                result[id] = source[id] > minimum[id] - correction ? 0.8F : 0.0F;
            }
            report(progress, ++completed_steps, total_steps);
        }
    }
    return result;
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

std::vector<float> morphology(const Volume& volume, int radius, bool dilate, bool spherical, const Engine::Progress& progress) {
    if (radius < 0 || radius > 64) throw std::invalid_argument("morphology radius must be between 0 and 64");
    if (radius == 0) return selected_channel(volume);
    const auto source = selected_channel(volume);
    const auto& d = volume.descriptor();
    std::vector<float> result(source.size());
    for (std::size_t z = 0; z < d.depth; ++z) {
        for (std::size_t y = 0; y < d.height; ++y) {
            for (std::size_t x = 0; x < d.width; ++x) {
                float chosen = dilate ? -std::numeric_limits<float>::infinity() : std::numeric_limits<float>::infinity();
                for (int dz = -radius; dz <= radius; ++dz) {
                    const auto zz = std::clamp<long long>(static_cast<long long>(z) + dz, 0, d.depth - 1);
                    for (int dy = -radius; dy <= radius; ++dy) {
                        const auto yy = std::clamp<long long>(static_cast<long long>(y) + dy, 0, d.height - 1);
                        for (int dx = -radius; dx <= radius; ++dx) {
                            if (spherical && dx * dx + dy * dy + dz * dz > radius * radius) continue;
                            const auto xx = std::clamp<long long>(static_cast<long long>(x) + dx, 0, d.width - 1);
                            const float value = source[flat(xx, yy, zz, d.width, d.height)];
                            chosen = dilate ? std::max(chosen, value) : std::min(chosen, value);
                        }
                    }
                }
                result[flat(x, y, z, d.width, d.height)] = chosen;
            }
        }
        report(progress, z + 1, d.depth);
    }
    return result;
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

std::vector<float> height_map(const Volume& volume, float threshold_value, const Engine::Progress& progress) {
    const auto source = selected_channel(volume);
    const auto& d = volume.descriptor();
    std::vector<float> result(static_cast<std::size_t>(d.width) * d.height, -1.0F);
    for (std::size_t y = 0; y < d.height; ++y) {
        for (std::size_t x = 0; x < d.width; ++x) {
            for (std::size_t z = 0; z < d.depth; ++z) {
                if (source[flat(x, y, z, d.width, d.height)] >= threshold_value) {
                    result[y * d.width + x] = static_cast<float>(z);
                    break;
                }
            }
        }
        report(progress, y + 1, d.height);
    }
    return result;
}

std::vector<float> depth_map(const Volume& volume, float threshold_value, const Engine::Progress& progress) {
    const auto source = selected_channel(volume);
    const auto& d = volume.descriptor();
    std::vector<float> result(source.size(), 0.0F);
    for (std::size_t y = 0; y < d.height; ++y) {
        for (std::size_t x = 0; x < d.width; ++x) {
            int surface = -1;
            for (std::size_t z = 0; z < d.depth; ++z) {
                if (source[flat(x, y, z, d.width, d.height)] >= threshold_value) {
                    surface = static_cast<int>(z);
                    break;
                }
            }
            if (surface >= 0) {
                for (std::size_t z = surface; z < d.depth; ++z) {
                    result[flat(x, y, z, d.width, d.height)] = static_cast<float>(static_cast<int>(z) - surface) * static_cast<float>(d.calibration.spacing_z);
                }
            }
        }
        report(progress, y + 1, d.height);
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
    if (!(target_spacing > 0.0F)) throw std::invalid_argument("target Z spacing must be positive");
    if (lanczos && lanczos_order != 2 && lanczos_order != 3) throw std::invalid_argument("Lanczos order must be 2 or 3");
    const auto source = selected_channel(volume);
    const auto& d = volume.descriptor();
    const double physical_depth = d.depth * d.calibration.spacing_z;
    output_depth = std::max<std::uint32_t>(1, static_cast<std::uint32_t>(std::lround(physical_depth / target_spacing)));
    std::vector<float> result(static_cast<std::size_t>(d.width) * d.height * output_depth, 0.0F);
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
