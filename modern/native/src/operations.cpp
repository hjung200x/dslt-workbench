#include "operations.hpp"

#include <algorithm>
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

} // namespace

std::vector<float> selected_channel(const Volume& volume) {
    std::vector<float> result(volume.voxel_count());
    const auto offset = volume.voxel_count() * volume.descriptor().selected_channel;
    std::copy_n(volume.data().begin() + static_cast<std::ptrdiff_t>(offset), result.size(), result.begin());
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

