#ifndef DSLT_CUDA_BACKEND_HPP
#define DSLT_CUDA_BACKEND_HPP

#include "dslt/core.hpp"

#include <span>
#include <string>
#include <vector>

namespace dslt {

enum class CudaRunStatus {
    success,
    unavailable,
    unsupported,
    invalid_argument,
    out_of_memory,
    cancelled,
    internal_error,
};

struct CudaRunResult final {
    CudaRunStatus status{CudaRunStatus::internal_error};
    std::vector<float> output;
    std::string error;
};

[[nodiscard]] bool cuda_supports_operation(dslt_operation operation) noexcept;

[[nodiscard]] CudaRunResult run_cuda_operation(
    std::span<const float> source,
    const dslt_operation_request& request,
    const Engine::Progress& progress) noexcept;

} // namespace dslt

#endif
