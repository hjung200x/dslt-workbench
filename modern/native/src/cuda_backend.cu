#include "cuda_backend.hpp"

#include <cuda_runtime.h>

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <utility>

namespace dslt {
namespace {

class CudaError final : public std::runtime_error {
public:
    CudaError(cudaError_t code, std::string message)
        : std::runtime_error(std::move(message)), code_(code) {}

    [[nodiscard]] cudaError_t code() const noexcept { return code_; }

private:
    cudaError_t code_;
};

[[nodiscard]] std::string cuda_message(const char* context, cudaError_t code) {
    std::ostringstream message;
    message << context << ": " << cudaGetErrorString(code);
    return message.str();
}

void check_cuda(cudaError_t code, const char* context) {
    if (code != cudaSuccess) throw CudaError(code, cuda_message(context, code));
}

[[nodiscard]] bool is_unavailable(cudaError_t code) noexcept {
    return code == cudaErrorNoDevice ||
        code == cudaErrorInsufficientDriver ||
        code == cudaErrorInitializationError ||
        code == cudaErrorSystemDriverMismatch;
}

class CudaStream final {
public:
    CudaStream() { check_cuda(cudaStreamCreateWithFlags(&stream_, cudaStreamNonBlocking), "creating CUDA stream"); }
    ~CudaStream() { if (stream_ != nullptr) cudaStreamDestroy(stream_); }

    CudaStream(const CudaStream&) = delete;
    CudaStream& operator=(const CudaStream&) = delete;

    [[nodiscard]] cudaStream_t get() const noexcept { return stream_; }

private:
    cudaStream_t stream_{};
};

template <typename T>
class DeviceBuffer final {
public:
    explicit DeviceBuffer(std::size_t count) {
        if (count > std::numeric_limits<std::size_t>::max() / sizeof(T)) {
            throw std::overflow_error("CUDA buffer size overflows addressable memory");
        }
        check_cuda(cudaMalloc(reinterpret_cast<void**>(&data_), count * sizeof(T)), "allocating CUDA buffer");
    }

    ~DeviceBuffer() { if (data_ != nullptr) cudaFree(data_); }

    DeviceBuffer(const DeviceBuffer&) = delete;
    DeviceBuffer& operator=(const DeviceBuffer&) = delete;

    [[nodiscard]] T* get() noexcept { return data_; }
    [[nodiscard]] const T* get() const noexcept { return data_; }

private:
    T* data_{};
};

[[nodiscard]] CudaRunResult cancelled() {
    return {CudaRunStatus::cancelled, {}, "operation cancelled"};
}

[[nodiscard]] bool report(const Engine::Progress& progress, float value) {
    return !progress || progress(value);
}

__global__ void window_level_kernel(
    const float* source,
    float* output,
    std::size_t count,
    float minimum,
    float scale) {
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         index < count;
         index += stride) {
        auto value = (source[index] - minimum) * scale;
        if (value < 0.0F) value = 0.0F;
        else if (value > 1.0F) value = 1.0F;
        output[index] = value;
    }
}

__global__ void threshold_kernel(
    const float* source,
    float* output,
    std::size_t count,
    float threshold) {
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         index < count;
         index += stride) {
        output[index] = source[index] >= threshold ? 1.0F : 0.0F;
    }
}

__device__ std::size_t flat_index(
    std::size_t x,
    std::size_t y,
    std::size_t z,
    std::size_t width,
    std::size_t height) {
    return z * width * height + y * width + x;
}

__device__ std::size_t clamp_coordinate(long long value, std::size_t length) {
    if (value < 0) return 0;
    const auto converted = static_cast<std::size_t>(value);
    return converted >= length ? length - 1 : converted;
}

__global__ void smooth_slice_kernel(
    const float* source,
    float* output,
    std::size_t width,
    std::size_t height,
    std::size_t depth,
    std::size_t z,
    int radius,
    bool gaussian) {
    const auto plane = width * height;
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto plane_index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         plane_index < plane;
         plane_index += stride) {
        const auto x = plane_index % width;
        const auto y = plane_index / width;
        const float sigma = fmaxf(0.5F, static_cast<float>(radius) / 2.0F);
        const float denominator = 2.0F * sigma * sigma;
        double sum = 0.0;
        double weight_sum = 0.0;
        for (int dz = -radius; dz <= radius; ++dz) {
            const auto zz = clamp_coordinate(static_cast<long long>(z) + dz, depth);
            for (int dy = -radius; dy <= radius; ++dy) {
                const auto yy = clamp_coordinate(static_cast<long long>(y) + dy, height);
                for (int dx = -radius; dx <= radius; ++dx) {
                    const auto xx = clamp_coordinate(static_cast<long long>(x) + dx, width);
                    const auto squared_distance = dx * dx + dy * dy + dz * dz;
                    const double weight = gaussian
                        ? static_cast<double>(expf(-static_cast<float>(squared_distance) / denominator))
                        : 1.0;
                    sum += static_cast<double>(source[flat_index(xx, yy, zz, width, height)]) * weight;
                    weight_sum += weight;
                }
            }
        }
        output[flat_index(x, y, z, width, height)] = static_cast<float>(sum / weight_sum);
    }
}

__global__ void morphology_slice_kernel(
    const float* source,
    float* output,
    std::size_t width,
    std::size_t height,
    std::size_t depth,
    std::size_t z,
    int radius,
    bool dilate,
    bool spherical) {
    const auto plane = width * height;
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto plane_index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         plane_index < plane;
         plane_index += stride) {
        const auto x = plane_index % width;
        const auto y = plane_index / width;
        const auto infinity = __int_as_float(0x7f800000);
        auto chosen = dilate ? -infinity : infinity;
        for (int dz = -radius; dz <= radius; ++dz) {
            const auto zz = clamp_coordinate(static_cast<long long>(z) + dz, depth);
            for (int dy = -radius; dy <= radius; ++dy) {
                const auto yy = clamp_coordinate(static_cast<long long>(y) + dy, height);
                for (int dx = -radius; dx <= radius; ++dx) {
                    if (spherical && dx * dx + dy * dy + dz * dz > radius * radius) continue;
                    const auto xx = clamp_coordinate(static_cast<long long>(x) + dx, width);
                    const auto value = source[flat_index(xx, yy, zz, width, height)];
                    if (dilate ? value > chosen : value < chosen) chosen = value;
                }
            }
        }
        output[flat_index(x, y, z, width, height)] = chosen;
    }
}

__device__ float sinc_device(float value) {
    if (fabsf(value) < 1.0e-7F) return 1.0F;
    constexpr float pi = 3.14159265358979323846F;
    const auto angle = pi * value;
    return sinf(angle) / angle;
}

__device__ int clamp_index(int value, int length) {
    if (value < 0) return 0;
    return value >= length ? length - 1 : value;
}

__global__ void resample_area_slice_kernel(
    const float* source,
    float* output,
    std::size_t width,
    std::size_t height,
    int input_depth,
    std::size_t output_z,
    double scale) {
    const auto plane = width * height;
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    const auto begin = static_cast<double>(output_z) * scale;
    const auto end = static_cast<double>(output_z + 1) * scale;
    const auto first = static_cast<int>(floor(begin));
    const auto last = static_cast<int>(ceil(end));
    for (auto plane_index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         plane_index < plane;
         plane_index += stride) {
        const auto x = plane_index % width;
        const auto y = plane_index / width;
        double sum = 0.0;
        double weight_sum = 0.0;
        for (int input_z = first; input_z < last; ++input_z) {
            const auto clamped_z = clamp_index(input_z, input_depth);
            const auto overlap = fmax(
                0.0,
                fmin(end, static_cast<double>(input_z) + 1.0) -
                    fmax(begin, static_cast<double>(input_z)));
            sum += static_cast<double>(source[flat_index(
                x, y, static_cast<std::size_t>(clamped_z), width, height)]) * overlap;
            weight_sum += overlap;
        }
        output[flat_index(x, y, output_z, width, height)] =
            weight_sum == 0.0 ? 0.0F : static_cast<float>(sum / weight_sum);
    }
}

__global__ void resample_lanczos_slice_kernel(
    const float* source,
    float* output,
    std::size_t width,
    std::size_t height,
    int input_depth,
    std::size_t output_z,
    double scale,
    int order) {
    const auto plane = width * height;
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    const auto center = (static_cast<double>(output_z) + 0.5) * scale - 0.5;
    const auto center_floor = static_cast<int>(floor(center));
    const auto first = center_floor - order + 1;
    const auto last = center_floor + order;
    for (auto plane_index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         plane_index < plane;
         plane_index += stride) {
        const auto x = plane_index % width;
        const auto y = plane_index / width;
        double sum = 0.0;
        double weight_sum = 0.0;
        for (int input_z = first; input_z <= last; ++input_z) {
            const auto clamped_z = clamp_index(input_z, input_depth);
            const auto distance = static_cast<float>(center - input_z);
            const double weight = fabsf(distance) < static_cast<float>(order)
                ? static_cast<double>(sinc_device(distance) * sinc_device(distance / order))
                : 0.0;
            sum += static_cast<double>(source[flat_index(
                x, y, static_cast<std::size_t>(clamped_z), width, height)]) * weight;
            weight_sum += weight;
        }
        output[flat_index(x, y, output_z, width, height)] =
            weight_sum == 0.0 ? 0.0F : static_cast<float>(sum / weight_sum);
    }
}

__global__ void extract_plane_kernel(
    const float* source,
    float* output,
    std::size_t output_count,
    std::size_t width,
    std::size_t height,
    std::size_t depth,
    int operation,
    std::size_t slice) {
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto output_index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         output_index < output_count;
         output_index += stride) {
        if (operation == DSLT_OP_EXTRACT_XY) {
            output[output_index] = source[slice * width * height + output_index];
        } else if (operation == DSLT_OP_EXTRACT_YZ) {
            const auto y = output_index / depth;
            const auto z = output_index % depth;
            output[output_index] = source[flat_index(slice, y, z, width, height)];
        } else {
            const auto z = output_index / width;
            const auto x = output_index % width;
            output[output_index] = source[flat_index(x, slice, z, width, height)];
        }
    }
}

__device__ float line_weight(int offset, int radius, bool gaussian, float weight_sum) {
    if (!gaussian) return 1.0F / static_cast<float>(radius * 2 + 1);
    const auto sigma = 0.3F * static_cast<float>(radius - 1) + 0.8F;
    const auto denominator = 2.0F * sigma * sigma;
    const auto value = static_cast<float>(offset);
    return expf(-(value * value) / denominator) / weight_sum;
}

__global__ void line_convolve_kernel(
    const float* source,
    float* output,
    std::size_t count,
    std::size_t width,
    std::size_t height,
    std::size_t depth,
    int radius,
    bool gaussian,
    int axis) {
    float weight_sum = 1.0F;
    if (gaussian) {
        weight_sum = 0.0F;
        const auto sigma = 0.3F * static_cast<float>(radius - 1) + 0.8F;
        const auto denominator = 2.0F * sigma * sigma;
        for (int offset = -radius; offset <= radius; ++offset) {
            const auto value = static_cast<float>(offset);
            weight_sum += expf(-(value * value) / denominator);
        }
    }
    const auto plane = width * height;
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         index < count;
         index += stride) {
        const auto z = index / plane;
        const auto remainder = index % plane;
        const auto y = remainder / width;
        const auto x = remainder % width;
        double sum = 0.0;
        for (int offset = -radius; offset <= radius; ++offset) {
            const auto xx = axis == 0
                ? clamp_coordinate(static_cast<long long>(x) + offset, width)
                : x;
            const auto yy = axis == 1
                ? clamp_coordinate(static_cast<long long>(y) + offset, height)
                : y;
            const auto zz = axis == 2
                ? clamp_coordinate(static_cast<long long>(z) + offset, depth)
                : z;
            const auto weight = line_weight(offset, radius, gaussian, weight_sum);
            sum += static_cast<double>(source[flat_index(xx, yy, zz, width, height)]) * weight;
        }
        output[index] = static_cast<float>(sum);
    }
}

__global__ void height_surface_kernel(
    const float* filtered,
    float* surface,
    std::size_t width,
    std::size_t height,
    std::size_t depth,
    float threshold) {
    const auto plane = width * height;
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         index < plane;
         index += stride) {
        auto selected = static_cast<float>(depth - 1);
        for (std::size_t z = 0; z < depth; ++z) {
            const auto value = filtered[z * plane + index];
            if (value <= threshold) continue;
            if (z == 0) {
                selected = 0.0F;
            } else {
                const auto previous = filtered[(z - 1) * plane + index];
                const auto denominator = value - previous;
                selected = denominator == 0.0F
                    ? static_cast<float>(z - 1)
                    : static_cast<float>(z - 1) + (threshold - previous) / denominator;
            }
            break;
        }
        surface[index] = selected;
    }
}

__device__ float trilinear_clamp_device(
    const float* source,
    std::size_t width,
    std::size_t height,
    std::size_t depth,
    float x,
    float y,
    float z) {
    const auto cx = fminf(fmaxf(x, 0.0F), static_cast<float>(width - 1));
    const auto cy = fminf(fmaxf(y, 0.0F), static_cast<float>(height - 1));
    const auto cz = fminf(fmaxf(z, 0.0F), static_cast<float>(depth - 1));
    const auto x0 = static_cast<std::size_t>(floorf(cx));
    const auto y0 = static_cast<std::size_t>(floorf(cy));
    const auto z0 = static_cast<std::size_t>(floorf(cz));
    const auto x1 = x0 + 1 < width ? x0 + 1 : width - 1;
    const auto y1 = y0 + 1 < height ? y0 + 1 : height - 1;
    const auto z1 = z0 + 1 < depth ? z0 + 1 : depth - 1;
    const auto tx = cx - static_cast<float>(x0);
    const auto ty = cy - static_cast<float>(y0);
    const auto tz = cz - static_cast<float>(z0);
    const auto c000 = source[flat_index(x0, y0, z0, width, height)];
    const auto c100 = source[flat_index(x1, y0, z0, width, height)];
    const auto c010 = source[flat_index(x0, y1, z0, width, height)];
    const auto c110 = source[flat_index(x1, y1, z0, width, height)];
    const auto c001 = source[flat_index(x0, y0, z1, width, height)];
    const auto c101 = source[flat_index(x1, y0, z1, width, height)];
    const auto c011 = source[flat_index(x0, y1, z1, width, height)];
    const auto c111 = source[flat_index(x1, y1, z1, width, height)];
    const auto c00 = c000 + (c100 - c000) * tx;
    const auto c10 = c010 + (c110 - c010) * tx;
    const auto c01 = c001 + (c101 - c001) * tx;
    const auto c11 = c011 + (c111 - c011) * tx;
    const auto lower = c00 + (c10 - c00) * ty;
    const auto upper = c01 + (c11 - c01) * ty;
    return lower + (upper - lower) * tz;
}

__global__ void height_projection_kernel(
    const float* source,
    const float* surface,
    float* output,
    std::size_t width,
    std::size_t height,
    std::size_t depth,
    int mode,
    int range,
    float offset,
    float start_depth,
    float projection_threshold) {
    const auto plane = width * height;
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         index < plane;
         index += stride) {
        const auto x = index % width;
        const auto y = index / width;
        auto nx = 0.0F;
        auto ny = 0.0F;
        auto nz = 0.0F;
        const auto center = surface[index];
        if (mode == 0) {
            if (x + 1 < width && y + 1 < height) {
                nx -= surface[index + 1] - center;
                ny -= surface[index + width] - center;
                nz += 1.0F;
            }
            if (x > 0 && y + 1 < height) {
                nx += surface[index - 1] - center;
                ny -= surface[index + width] - center;
                nz += 1.0F;
            }
            if (x > 0 && y > 0) {
                nx += surface[index - 1] - center;
                ny += surface[index - width] - center;
                nz += 1.0F;
            }
            if (x + 1 < width && y > 0) {
                nx += surface[index + 1] - center;
                ny += surface[index - width] - center;
                nz += 1.0F;
            }
            const auto length = sqrtf(nx * nx + ny * ny + nz * nz);
            if (length > 0.0F) {
                nx /= length;
                ny /= length;
                nz /= length;
            } else {
                nx = 0.0F;
                ny = 0.0F;
                nz = 1.0F;
            }
        }

        auto maximum = -1.0F;
        for (int step = 0; step <= range; ++step) {
            const auto distance = start_depth + static_cast<float>(step);
            const auto px = mode == 0 ? static_cast<float>(x) + nx * distance : static_cast<float>(x);
            const auto py = mode == 0 ? static_cast<float>(y) + ny * distance : static_cast<float>(y);
            const auto pz = center + (mode == 0 ? nz * distance : distance) + offset;
            const auto outside = px < 0.0F || py < 0.0F || pz < 0.0F ||
                px > static_cast<float>(width - 1) ||
                py > static_cast<float>(height - 1) ||
                pz > static_cast<float>(depth - 1);
            if (outside) {
                if (mode == 1 && pz < 0.0F) continue;
                if (maximum < 0.0F) maximum = 0.0F;
                break;
            }
            const auto value = trilinear_clamp_device(source, width, height, depth, px, py, pz);
            if (mode == 0 && projection_threshold > 0.0F) {
                maximum = value > projection_threshold ? 1.0F : 0.0F;
                if (maximum == 1.0F) break;
            } else if (value > maximum) {
                maximum = value;
            }
        }
        if (maximum < 0.0F || (mode == 1 && maximum < projection_threshold)) maximum = 0.0F;
        output[index] = maximum;
    }
}

__global__ void depth_cost_kernel(
    const float* surface,
    float* costs,
    std::size_t plane,
    std::size_t z) {
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         index < plane;
         index += stride) {
        const auto delta = static_cast<float>(z) - surface[index];
        costs[index] = delta * delta;
    }
}

__global__ void depth_row_kernel(
    const float* costs,
    float* rows,
    std::size_t width,
    std::size_t height) {
    const auto plane = width * height;
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         index < plane;
         index += stride) {
        const auto x = index % width;
        const auto y = index / width;
        auto minimum = __int_as_float(0x7f800000);
        for (std::size_t source_x = 0; source_x < width; ++source_x) {
            const auto delta = static_cast<float>(x) - static_cast<float>(source_x);
            minimum = fminf(minimum, delta * delta + costs[y * width + source_x]);
        }
        rows[index] = minimum;
    }
}

__global__ void depth_column_kernel(
    const float* rows,
    const float* surface,
    float* output,
    std::size_t width,
    std::size_t height,
    std::size_t z) {
    const auto plane = width * height;
    const auto stride = static_cast<std::size_t>(blockDim.x) * gridDim.x;
    for (auto index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
         index < plane;
         index += stride) {
        if (static_cast<float>(z) <= surface[index]) {
            output[z * plane + index] = 0.0F;
            continue;
        }
        const auto x = index % width;
        const auto y = index / width;
        auto minimum = __int_as_float(0x7f800000);
        for (std::size_t source_y = 0; source_y < height; ++source_y) {
            const auto delta = static_cast<float>(y) - static_cast<float>(source_y);
            minimum = fminf(minimum, delta * delta + rows[source_y * width + x]);
        }
        output[z * plane + index] = sqrtf(fmaxf(minimum, 0.0F));
    }
}

[[nodiscard]] unsigned int block_count(std::size_t count) noexcept {
    constexpr std::size_t threads = 256;
    constexpr std::size_t maximum_blocks = 65535;
    return static_cast<unsigned int>(std::min((count + threads - 1) / threads, maximum_blocks));
}

} // namespace

BackendState query_cuda_backend() noexcept {
    BackendState state{true, false, 0, "CUDA unavailable"};
    int count = 0;
    if (cudaGetDeviceCount(&count) != cudaSuccess || count <= 0) return state;
    int device = 0;
    if (cudaGetDevice(&device) != cudaSuccess) return state;
    cudaDeviceProp properties{};
    if (cudaGetDeviceProperties(&properties, device) != cudaSuccess) return state;
    std::size_t free_memory = 0;
    std::size_t total_memory = 0;
    if (cudaMemGetInfo(&free_memory, &total_memory) != cudaSuccess) return state;
    state.available = true;
    state.device_memory_bytes = free_memory;
    state.device_name = properties.name;
    return state;
}

bool cuda_supports_operation(dslt_operation operation) noexcept {
    return operation == DSLT_OP_COPY ||
        operation == DSLT_OP_WINDOW_LEVEL ||
        operation == DSLT_OP_THRESHOLD_2D ||
        operation == DSLT_OP_THRESHOLD_3D ||
        operation == DSLT_OP_SMOOTH_MEAN ||
        operation == DSLT_OP_SMOOTH_GAUSSIAN ||
        operation == DSLT_OP_DILATE_CUBE ||
        operation == DSLT_OP_ERODE_CUBE ||
        operation == DSLT_OP_DILATE_SPHERE ||
        operation == DSLT_OP_ERODE_SPHERE ||
        operation == DSLT_OP_RESAMPLE_Z_AREA ||
        operation == DSLT_OP_RESAMPLE_Z_LANCZOS ||
        operation == DSLT_OP_EXTRACT_XY ||
        operation == DSLT_OP_EXTRACT_YZ ||
        operation == DSLT_OP_EXTRACT_ZX ||
        operation == DSLT_OP_HEIGHT_MAP ||
        operation == DSLT_OP_DEPTH_MAP ||
        operation == DSLT_OP_HEIGHT_PROJECTION;
}

CudaRunResult run_cuda_operation(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    const dslt_operation_request& request,
    const Engine::Progress& progress) noexcept {
    if (!cuda_supports_operation(request.operation)) {
        return {CudaRunStatus::unsupported, {}, "CUDA does not implement the requested operation"};
    }
    if (request.operation == DSLT_OP_WINDOW_LEVEL && !(request.window_max > request.window_min)) {
        return {CudaRunStatus::invalid_argument, {}, "window maximum must be greater than minimum"};
    }
    const auto smoothing = request.operation == DSLT_OP_SMOOTH_MEAN ||
        request.operation == DSLT_OP_SMOOTH_GAUSSIAN;
    const auto morphology = request.operation == DSLT_OP_DILATE_CUBE ||
        request.operation == DSLT_OP_ERODE_CUBE ||
        request.operation == DSLT_OP_DILATE_SPHERE ||
        request.operation == DSLT_OP_ERODE_SPHERE;
    const auto resampling = request.operation == DSLT_OP_RESAMPLE_Z_AREA ||
        request.operation == DSLT_OP_RESAMPLE_Z_LANCZOS;
    const auto height_processing = request.operation == DSLT_OP_HEIGHT_MAP ||
        request.operation == DSLT_OP_DEPTH_MAP ||
        request.operation == DSLT_OP_HEIGHT_PROJECTION;
    if (smoothing && (request.radius < 0 || request.radius > 64)) {
        return {CudaRunStatus::invalid_argument, {}, "smoothing radius must be between 0 and 64"};
    }
    if (morphology && (request.radius < 0 || request.radius > 64)) {
        return {CudaRunStatus::invalid_argument, {}, "morphology radius must be between 0 and 64"};
    }
    if (resampling && !(request.target_spacing_z > 0.0F)) {
        return {CudaRunStatus::invalid_argument, {}, "target Z spacing must be positive"};
    }
    if (request.operation == DSLT_OP_RESAMPLE_Z_LANCZOS &&
        request.lanczos_order != 2 && request.lanczos_order != 3) {
        return {CudaRunStatus::invalid_argument, {}, "Lanczos order must be 2 or 3"};
    }
    if (height_processing &&
        (request.radius < 0 || request.radius > 64 ||
         request.lanczos_order < 0 || request.lanczos_order > 64)) {
        return {CudaRunStatus::invalid_argument, {},
            "height-map XY and Z radii must be between 0 and 64"};
    }
    if (height_processing && request.connectivity != 0 && request.connectivity != 1) {
        return {CudaRunStatus::invalid_argument, {},
            "height-map kernel must be 0 (Gaussian) or 1 (mean)"};
    }
    if (height_processing && (request.slice_index < 0 || request.slice_index > 10)) {
        return {CudaRunStatus::invalid_argument, {},
            "height-map smooth level must be between 0 and 10"};
    }
    if (height_processing && !std::isfinite(request.threshold)) {
        return {CudaRunStatus::invalid_argument, {}, "height-map threshold must be finite"};
    }
    const auto projection_mode = request.operation == DSLT_OP_HEIGHT_PROJECTION
        ? static_cast<int>(request.target_spacing_z)
        : 0;
    if (request.operation == DSLT_OP_HEIGHT_PROJECTION &&
        projection_mode != 0 && projection_mode != 1) {
        return {CudaRunStatus::invalid_argument, {},
            "height projection mode must be 0 (normal) or 1 (Z)"};
    }
    if (request.operation == DSLT_OP_HEIGHT_PROJECTION && request.minimum_component_size < 0) {
        return {CudaRunStatus::invalid_argument, {},
            "height projection range must be non-negative"};
    }
    if (request.operation == DSLT_OP_HEIGHT_PROJECTION &&
        (!std::isfinite(request.constant_c) || !std::isfinite(request.window_min) ||
         !std::isfinite(request.window_max))) {
        return {CudaRunStatus::invalid_argument, {},
            "height projection parameters must be finite"};
    }
    const auto width = static_cast<std::size_t>(descriptor.width);
    const auto height = static_cast<std::size_t>(descriptor.height);
    const auto depth = static_cast<std::size_t>(descriptor.depth);
    if (width == 0 || height == 0 || depth == 0 ||
        width > std::numeric_limits<std::size_t>::max() / height ||
        width * height > std::numeric_limits<std::size_t>::max() / depth ||
        source.size() != width * height * depth) {
        return {CudaRunStatus::invalid_argument, {}, "CUDA source dimensions do not match the selected channel"};
    }

    auto output_width = descriptor.width;
    auto output_height = descriptor.height;
    auto output_depth = descriptor.depth;
    auto output_kind = DSLT_OUTPUT_VOLUME_FLOAT32;
    if (request.operation == DSLT_OP_HEIGHT_MAP ||
        request.operation == DSLT_OP_HEIGHT_PROJECTION) {
        output_depth = 1;
        output_kind = DSLT_OUTPUT_IMAGE_FLOAT32;
    } else if (resampling) {
        const auto physical_depth = static_cast<double>(descriptor.depth) * descriptor.calibration.spacing_z;
        output_depth = std::max<std::uint32_t>(
            1,
            static_cast<std::uint32_t>(std::lround(physical_depth / request.target_spacing_z)));
    } else if (request.operation == DSLT_OP_EXTRACT_XY) {
        if (request.slice_index < 0 || request.slice_index >= static_cast<int>(descriptor.depth)) {
            return {CudaRunStatus::invalid_argument, {}, "XY slice is outside the volume"};
        }
        output_depth = 1;
        output_kind = DSLT_OUTPUT_IMAGE_FLOAT32;
    } else if (request.operation == DSLT_OP_EXTRACT_YZ) {
        if (request.slice_index < 0 || request.slice_index >= static_cast<int>(descriptor.width)) {
            return {CudaRunStatus::invalid_argument, {}, "YZ slice is outside the volume"};
        }
        output_width = descriptor.depth;
        output_height = descriptor.height;
        output_depth = 1;
        output_kind = DSLT_OUTPUT_IMAGE_FLOAT32;
    } else if (request.operation == DSLT_OP_EXTRACT_ZX) {
        if (request.slice_index < 0 || request.slice_index >= static_cast<int>(descriptor.height)) {
            return {CudaRunStatus::invalid_argument, {}, "ZX slice is outside the volume"};
        }
        output_width = descriptor.width;
        output_height = descriptor.depth;
        output_depth = 1;
        output_kind = DSLT_OUTPUT_IMAGE_FLOAT32;
    }
    const auto output_width_size = static_cast<std::size_t>(output_width);
    const auto output_height_size = static_cast<std::size_t>(output_height);
    const auto output_depth_size = static_cast<std::size_t>(output_depth);
    if (output_width_size > std::numeric_limits<std::size_t>::max() / output_height_size ||
        output_width_size * output_height_size >
            std::numeric_limits<std::size_t>::max() / output_depth_size) {
        return {CudaRunStatus::out_of_memory, {}, "CUDA output dimensions overflow addressable memory"};
    }
    const auto output_count = output_width_size * output_height_size * output_depth_size;

    std::size_t required_bytes = 0;
    std::size_t available_bytes = 0;
    try {
        if (!report(progress, 0.0F)) return cancelled();
        if (source.size() > std::numeric_limits<std::size_t>::max() / sizeof(float) ||
            output_count > std::numeric_limits<std::size_t>::max() / sizeof(float)) {
            return {CudaRunStatus::out_of_memory, {}, "CUDA input and output size overflow addressable memory"};
        }
        const auto input_bytes = source.size() * sizeof(float);
        const auto output_bytes = output_count * sizeof(float);
        const auto plane_count = width * height;
        if (request.operation == DSLT_OP_DEPTH_MAP &&
            plane_count > std::numeric_limits<std::size_t>::max() / 2) {
            return {CudaRunStatus::out_of_memory, {},
                "CUDA depth-map workspace overflows addressable memory"};
        }
        const auto scratch_count = height_processing
            ? std::max(
                source.size(),
                request.operation == DSLT_OP_DEPTH_MAP ? plane_count * 2 : source.size())
            : 0;
        const auto auxiliary_count = request.operation == DSLT_OP_DEPTH_MAP ||
            request.operation == DSLT_OP_HEIGHT_PROJECTION
            ? plane_count
            : 0;
        const auto add_required_buffer = [&](std::size_t count) {
            if (count > std::numeric_limits<std::size_t>::max() / sizeof(float)) return false;
            const auto bytes = count * sizeof(float);
            if (required_bytes > std::numeric_limits<std::size_t>::max() - bytes) return false;
            required_bytes += bytes;
            return true;
        };
        if (!add_required_buffer(source.size()) || !add_required_buffer(output_count) ||
            !add_required_buffer(scratch_count) || !add_required_buffer(auxiliary_count)) {
            return {CudaRunStatus::out_of_memory, {},
                "CUDA input and output size overflow addressable memory"};
        }
        std::size_t total_bytes = 0;
        check_cuda(cudaMemGetInfo(&available_bytes, &total_bytes), "querying CUDA memory");
        if (required_bytes > available_bytes) {
            std::ostringstream message;
            message << "CUDA memory preflight failed: requires " << required_bytes
                    << " bytes, available " << available_bytes << " bytes";
            return {CudaRunStatus::out_of_memory, {}, message.str()};
        }

        CudaStream stream;
        DeviceBuffer<float> device_source(source.size());
        DeviceBuffer<float> device_output(output_count);
        std::unique_ptr<DeviceBuffer<float>> device_scratch;
        std::unique_ptr<DeviceBuffer<float>> device_auxiliary;
        if (scratch_count != 0) {
            device_scratch = std::make_unique<DeviceBuffer<float>>(scratch_count);
        }
        if (auxiliary_count != 0) {
            device_auxiliary = std::make_unique<DeviceBuffer<float>>(auxiliary_count);
        }
        std::vector<float> output(output_count);

        check_cuda(cudaMemcpyAsync(
            device_source.get(), source.data(), input_bytes,
            cudaMemcpyHostToDevice, stream.get()), "copying input to CUDA device");
        if (!report(progress, 0.25F)) {
            check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing cancelled CUDA input copy");
            return cancelled();
        }

        constexpr unsigned int threads = 256;
        const auto blocks = block_count(source.size());
        const auto zero_radius_filter = (smoothing || morphology) && request.radius == 0;
        const auto slice_based_filter = (smoothing || morphology) && !zero_radius_filter;
        const auto run_height_map = [&](float* surface, float final_progress) {
            line_convolve_kernel<<<blocks, threads, 0, stream.get()>>>(
                device_source.get(), device_scratch->get(), source.size(), width, height, depth,
                request.lanczos_order, request.connectivity == 0, 2);
            check_cuda(cudaGetLastError(), "launching CUDA height-map Z convolution");
            check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing CUDA height-map Z convolution");
            if (!report(progress, 0.35F)) return false;

            height_surface_kernel<<<block_count(plane_count), threads, 0, stream.get()>>>(
                device_scratch->get(), surface, width, height, depth, request.threshold);
            check_cuda(cudaGetLastError(), "launching CUDA height-map surface kernel");
            check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing CUDA height-map surface kernel");
            if (!report(progress, 0.45F)) return false;

            for (int pass = 0; pass < request.slice_index; ++pass) {
                line_convolve_kernel<<<block_count(plane_count), threads, 0, stream.get()>>>(
                    surface, device_scratch->get(), plane_count, width, height, 1,
                    request.radius, request.connectivity == 0, 0);
                check_cuda(cudaGetLastError(), "launching CUDA height-map X convolution");
                line_convolve_kernel<<<block_count(plane_count), threads, 0, stream.get()>>>(
                    device_scratch->get(), surface, plane_count, width, height, 1,
                    request.radius, request.connectivity == 0, 1);
                check_cuda(cudaGetLastError(), "launching CUDA height-map Y convolution");
                check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing CUDA height-map smoothing pass");
                const auto pass_progress = 0.45F + (final_progress - 0.45F) *
                    static_cast<float>(pass + 1) / static_cast<float>(request.slice_index);
                if (!report(progress, pass_progress)) return false;
            }
            return request.slice_index != 0 || report(progress, final_progress);
        };
        switch (request.operation) {
        case DSLT_OP_COPY:
            check_cuda(cudaMemcpyAsync(
                device_output.get(), device_source.get(), input_bytes,
                cudaMemcpyDeviceToDevice, stream.get()), "copying CUDA volume");
            break;
        case DSLT_OP_WINDOW_LEVEL:
            window_level_kernel<<<blocks, threads, 0, stream.get()>>>(
                device_source.get(), device_output.get(), source.size(),
                request.window_min, 1.0F / (request.window_max - request.window_min));
            check_cuda(cudaGetLastError(), "launching CUDA window-level kernel");
            break;
        case DSLT_OP_THRESHOLD_2D:
        case DSLT_OP_THRESHOLD_3D:
            threshold_kernel<<<blocks, threads, 0, stream.get()>>>(
                device_source.get(), device_output.get(), source.size(), request.threshold);
            check_cuda(cudaGetLastError(), "launching CUDA threshold kernel");
            break;
        case DSLT_OP_SMOOTH_MEAN:
        case DSLT_OP_SMOOTH_GAUSSIAN:
        case DSLT_OP_DILATE_CUBE:
        case DSLT_OP_ERODE_CUBE:
        case DSLT_OP_DILATE_SPHERE:
        case DSLT_OP_ERODE_SPHERE:
            if (zero_radius_filter) {
                check_cuda(cudaMemcpyAsync(
                    device_output.get(), device_source.get(), input_bytes,
                    cudaMemcpyDeviceToDevice, stream.get()), "copying zero-radius CUDA filter output");
                break;
            }
            for (std::size_t z = 0; z < depth; ++z) {
                if (smoothing) {
                    smooth_slice_kernel<<<block_count(width * height), threads, 0, stream.get()>>>(
                        device_source.get(), device_output.get(), width, height, depth, z,
                        request.radius, request.operation == DSLT_OP_SMOOTH_GAUSSIAN);
                    check_cuda(cudaGetLastError(), "launching CUDA smoothing kernel");
                } else {
                    const auto dilate = request.operation == DSLT_OP_DILATE_CUBE ||
                        request.operation == DSLT_OP_DILATE_SPHERE;
                    const auto spherical = request.operation == DSLT_OP_DILATE_SPHERE ||
                        request.operation == DSLT_OP_ERODE_SPHERE;
                    morphology_slice_kernel<<<block_count(width * height), threads, 0, stream.get()>>>(
                        device_source.get(), device_output.get(), width, height, depth, z,
                        request.radius, dilate, spherical);
                    check_cuda(cudaGetLastError(), "launching CUDA morphology kernel");
                }
                check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing CUDA filter slice");
                const auto slice_progress = 0.25F + 0.5F *
                    static_cast<float>(z + 1) / static_cast<float>(depth);
                if (!report(progress, slice_progress)) return cancelled();
            }
            break;
        case DSLT_OP_RESAMPLE_Z_AREA:
        case DSLT_OP_RESAMPLE_Z_LANCZOS: {
            const auto scale = static_cast<double>(depth) / output_depth_size;
            for (std::size_t output_z = 0; output_z < output_depth_size; ++output_z) {
                if (request.operation == DSLT_OP_RESAMPLE_Z_AREA) {
                    resample_area_slice_kernel<<<block_count(width * height), threads, 0, stream.get()>>>(
                        device_source.get(), device_output.get(), width, height,
                        static_cast<int>(depth), output_z, scale);
                    check_cuda(cudaGetLastError(), "launching CUDA area-resample kernel");
                } else {
                    resample_lanczos_slice_kernel<<<block_count(width * height), threads, 0, stream.get()>>>(
                        device_source.get(), device_output.get(), width, height,
                        static_cast<int>(depth), output_z, scale, request.lanczos_order);
                    check_cuda(cudaGetLastError(), "launching CUDA Lanczos-resample kernel");
                }
                check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing CUDA resample slice");
                const auto slice_progress = 0.25F + 0.5F *
                    static_cast<float>(output_z + 1) / static_cast<float>(output_depth_size);
                if (!report(progress, slice_progress)) return cancelled();
            }
            break;
        }
        case DSLT_OP_EXTRACT_XY:
        case DSLT_OP_EXTRACT_YZ:
        case DSLT_OP_EXTRACT_ZX:
            extract_plane_kernel<<<block_count(output_count), threads, 0, stream.get()>>>(
                device_source.get(), device_output.get(), output_count, width, height, depth,
                static_cast<int>(request.operation), static_cast<std::size_t>(request.slice_index));
            check_cuda(cudaGetLastError(), "launching CUDA orthogonal-view kernel");
            break;
        case DSLT_OP_HEIGHT_MAP:
            if (!run_height_map(device_output.get(), 0.75F)) return cancelled();
            break;
        case DSLT_OP_DEPTH_MAP:
            if (!run_height_map(device_auxiliary->get(), 0.50F)) return cancelled();
            for (std::size_t z = 0; z < depth; ++z) {
                depth_cost_kernel<<<block_count(plane_count), threads, 0, stream.get()>>>(
                    device_auxiliary->get(), device_scratch->get(), plane_count, z);
                check_cuda(cudaGetLastError(), "launching CUDA depth-map cost kernel");
                depth_row_kernel<<<block_count(plane_count), threads, 0, stream.get()>>>(
                    device_scratch->get(), device_scratch->get() + plane_count,
                    width, height);
                check_cuda(cudaGetLastError(), "launching CUDA depth-map row kernel");
                depth_column_kernel<<<block_count(plane_count), threads, 0, stream.get()>>>(
                    device_scratch->get() + plane_count, device_auxiliary->get(),
                    device_output.get(), width, height, z);
                check_cuda(cudaGetLastError(), "launching CUDA depth-map column kernel");
                check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing CUDA depth-map slice");
                if (!report(progress, 0.50F + 0.25F * static_cast<float>(z + 1) /
                    static_cast<float>(depth))) return cancelled();
            }
            break;
        case DSLT_OP_HEIGHT_PROJECTION:
            if (!run_height_map(device_output.get(), 0.55F)) return cancelled();
            height_projection_kernel<<<block_count(plane_count), threads, 0, stream.get()>>>(
                device_source.get(), device_output.get(), device_auxiliary->get(),
                width, height, depth, projection_mode, request.minimum_component_size,
                request.constant_c, request.window_min, request.window_max);
            check_cuda(cudaGetLastError(), "launching CUDA height projection kernel");
            check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing CUDA height projection kernel");
            if (!report(progress, 0.70F)) return cancelled();
            check_cuda(cudaMemcpyAsync(
                device_output.get(), device_auxiliary->get(), output_bytes,
                cudaMemcpyDeviceToDevice, stream.get()), "copying CUDA height projection output");
            check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing CUDA height projection output");
            if (!report(progress, 0.75F)) return cancelled();
            break;
        default:
            return {CudaRunStatus::unsupported, {}, "CUDA does not implement the requested operation"};
        }

        if (!slice_based_filter && !resampling && !height_processing && !report(progress, 0.75F)) {
            check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing cancelled CUDA operation");
            return cancelled();
        }
        check_cuda(cudaMemcpyAsync(
            output.data(), device_output.get(), output_bytes,
            cudaMemcpyDeviceToHost, stream.get()), "copying CUDA output to host");
        check_cuda(cudaStreamSynchronize(stream.get()), "synchronizing CUDA operation");
        if (!report(progress, 1.0F)) return cancelled();
        return {
            CudaRunStatus::success,
            std::move(output),
            {},
            output_width,
            output_height,
            output_depth,
            output_kind,
        };
    } catch (const CudaError& error) {
        if (error.code() == cudaErrorMemoryAllocation) {
            std::ostringstream message;
            message << "CUDA memory allocation failed: requires " << required_bytes
                    << " bytes, available " << available_bytes << " bytes ("
                    << error.what() << ')';
            return {CudaRunStatus::out_of_memory, {}, message.str()};
        }
        if (is_unavailable(error.code())) {
            return {CudaRunStatus::unavailable, {}, error.what()};
        }
        return {CudaRunStatus::internal_error, {}, error.what()};
    } catch (const std::bad_alloc&) {
        return {CudaRunStatus::out_of_memory, {}, "not enough host memory for CUDA output"};
    } catch (const std::overflow_error& error) {
        return {CudaRunStatus::out_of_memory, {}, error.what()};
    } catch (const std::exception& error) {
        return {CudaRunStatus::internal_error, {}, error.what()};
    } catch (...) {
        return {CudaRunStatus::internal_error, {}, "unknown CUDA execution failure"};
    }
}

} // namespace dslt
