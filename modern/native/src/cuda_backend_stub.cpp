#include "cuda_backend.hpp"

namespace dslt {

BackendState query_cuda_backend() noexcept {
    return BackendState{false, false, 0, "CUDA backend not compiled"};
}

bool cuda_supports_operation(dslt_operation operation) noexcept {
    return operation == DSLT_OP_COPY ||
        operation == DSLT_OP_WINDOW_LEVEL ||
        operation == DSLT_OP_THRESHOLD_2D ||
        operation == DSLT_OP_THRESHOLD_3D;
}

CudaRunResult run_cuda_operation(
    std::span<const float>,
    const dslt_operation_request&,
    const Engine::Progress&) noexcept {
    return {CudaRunStatus::unavailable, {}, "CUDA backend is not compiled"};
}

} // namespace dslt

