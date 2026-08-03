#include "cuda_backend.hpp"

#include <cuda_runtime.h>

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
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
        operation == DSLT_OP_EXTRACT_ZX;
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
    if (resampling) {
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
        if (input_bytes > std::numeric_limits<std::size_t>::max() - output_bytes) {
            return {CudaRunStatus::out_of_memory, {}, "CUDA input and output size overflow addressable memory"};
        }
        required_bytes = input_bytes + output_bytes;
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
        default:
            return {CudaRunStatus::unsupported, {}, "CUDA does not implement the requested operation"};
        }

        if (!slice_based_filter && !resampling && !report(progress, 0.75F)) {
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
