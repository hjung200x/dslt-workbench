#include "dslt/core.hpp"

#include <cuda_runtime.h>

namespace dslt {

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

} // namespace dslt

