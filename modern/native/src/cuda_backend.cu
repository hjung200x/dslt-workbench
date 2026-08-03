#include "dslt/core.hpp"

#include <cuda_runtime.h>

#include <algorithm>
#include <cstddef>
#include <limits>
#include <new>
#include <string>
#include <utility>

namespace dslt {

namespace {

class CudaStream final {
public:
    CudaStream() {
        const auto status = cudaStreamCreateWithFlags(&value_, cudaStreamNonBlocking);
        if (status != cudaSuccess) throw status;
    }
    ~CudaStream() { if (value_ != nullptr) cudaStreamDestroy(value_); }
    CudaStream(const CudaStream&) = delete;
    CudaStream& operator=(const CudaStream&) = delete;
    [[nodiscard]] cudaStream_t get() const noexcept { return value_; }

private:
    cudaStream_t value_{};
};

class DeviceFloats final {
public:
    explicit DeviceFloats(std::size_t count) : count_(count) {
        if (count_ == 0) return;
        const auto status = cudaMalloc(reinterpret_cast<void**>(&value_), count_ * sizeof(float));
        if (status != cudaSuccess) throw status;
    }
    ~DeviceFloats() { if (value_ != nullptr) cudaFree(value_); }
    DeviceFloats(const DeviceFloats&) = delete;
    DeviceFloats& operator=(const DeviceFloats&) = delete;
    [[nodiscard]] float* get() noexcept { return value_; }
    [[nodiscard]] const float* get() const noexcept { return value_; }

private:
    float* value_{};
    std::size_t count_{};
};

__global__ void pointwise_kernel(
    const float* source,
    float* destination,
    std::size_t count,
    std::int32_t operation,
    float first,
    float second) {
    const auto index = static_cast<std::size_t>(blockIdx.x) * blockDim.x + threadIdx.x;
    if (index >= count) return;
    const auto value = source[index];
    if (operation == DSLT_OP_WINDOW_LEVEL) {
        const auto normalized = (value - first) / (second - first);
        destination[index] = fminf(1.0F, fmaxf(0.0F, normalized));
    } else if (operation == DSLT_OP_THRESHOLD_2D || operation == DSLT_OP_THRESHOLD_3D) {
        destination[index] = value >= first ? 1.0F : 0.0F;
    } else {
        destination[index] = value;
    }
}

CudaRunResult cuda_error(dslt_status status, const char* context, cudaError_t error) {
    return {status, {}, std::string(context) + ": " + cudaGetErrorString(error)};
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

bool cuda_supports_operation(std::int32_t operation) noexcept {
    return operation == DSLT_OP_COPY ||
        operation == DSLT_OP_WINDOW_LEVEL ||
        operation == DSLT_OP_THRESHOLD_2D ||
        operation == DSLT_OP_THRESHOLD_3D;
}

CudaRunResult run_cuda_operation(
    const Volume& volume,
    const dslt_operation_request& request,
    const std::function<bool(float)>& progress) {
    if (!cuda_supports_operation(request.operation))
        return {DSLT_NOT_IMPLEMENTED, {}, "CUDA operation is not implemented"};
    if (request.operation == DSLT_OP_WINDOW_LEVEL && !(request.window_max > request.window_min))
        return {DSLT_INVALID_ARGUMENT, {}, "window maximum must be greater than minimum"};
    if (progress && !progress(0.0F))
        return {DSLT_CANCELLED, {}, "operation cancelled"};

    try {
        const auto voxel_count = volume.voxel_count();
        if (voxel_count > std::numeric_limits<std::size_t>::max() / (2 * sizeof(float)))
            return {DSLT_OUT_OF_MEMORY, {}, "CUDA byte requirement overflowed addressable memory"};
        const auto required_bytes = voxel_count * 2 * sizeof(float);
        std::size_t free_bytes = 0;
        std::size_t total_bytes = 0;
        auto status = cudaMemGetInfo(&free_bytes, &total_bytes);
        if (status != cudaSuccess) return cuda_error(DSLT_BACKEND_UNAVAILABLE, "cudaMemGetInfo failed", status);
        if (required_bytes > free_bytes) {
            return {
                DSLT_OUT_OF_MEMORY,
                {},
                "CUDA operation requires " + std::to_string(required_bytes) +
                    " bytes but only " + std::to_string(free_bytes) + " bytes are available",
            };
        }

        const auto channel_offset = voxel_count * volume.descriptor().selected_channel;
        const auto source = volume.data().subspan(channel_offset, voxel_count);
        std::vector<float> output(voxel_count);
        CudaStream stream;
        DeviceFloats device_source(voxel_count);
        DeviceFloats device_output(voxel_count);
        const auto bytes = voxel_count * sizeof(float);
        status = cudaMemcpyAsync(
            device_source.get(), source.data(), bytes, cudaMemcpyHostToDevice, stream.get());
        if (status != cudaSuccess) return cuda_error(DSLT_INTERNAL_ERROR, "CUDA input copy failed", status);

        constexpr unsigned int threads = 256;
        const auto blocks64 = (voxel_count + threads - 1) / threads;
        if (blocks64 > std::numeric_limits<unsigned int>::max())
            return {DSLT_RESOURCE_LIMIT, {}, "CUDA grid dimension exceeds the supported range"};
        pointwise_kernel<<<static_cast<unsigned int>(blocks64), threads, 0, stream.get()>>>(
            device_source.get(),
            device_output.get(),
            voxel_count,
            request.operation,
            request.operation == DSLT_OP_WINDOW_LEVEL ? request.window_min : request.threshold,
            request.window_max);
        status = cudaGetLastError();
        if (status != cudaSuccess) return cuda_error(DSLT_INTERNAL_ERROR, "CUDA kernel launch failed", status);
        status = cudaMemcpyAsync(
            output.data(), device_output.get(), bytes, cudaMemcpyDeviceToHost, stream.get());
        if (status != cudaSuccess) return cuda_error(DSLT_INTERNAL_ERROR, "CUDA output copy failed", status);
        status = cudaStreamSynchronize(stream.get());
        if (status != cudaSuccess) return cuda_error(DSLT_INTERNAL_ERROR, "CUDA stream synchronization failed", status);
        if (progress && !progress(1.0F))
            return {DSLT_CANCELLED, {}, "operation cancelled"};
        return {DSLT_OK, std::move(output), {}};
    } catch (cudaError_t error) {
        const auto status = error == cudaErrorMemoryAllocation ? DSLT_OUT_OF_MEMORY : DSLT_BACKEND_UNAVAILABLE;
        return cuda_error(status, "CUDA resource initialization failed", error);
    } catch (const std::bad_alloc&) {
        return {DSLT_OUT_OF_MEMORY, {}, "not enough host memory for CUDA output"};
    }
}

} // namespace dslt
