#include "dslt/core.hpp"

namespace dslt {

BackendState query_cuda_backend() noexcept {
    return BackendState{false, false, 0, "CUDA backend not compiled"};
}

bool cuda_supports_operation(std::int32_t) noexcept {
    return false;
}

CudaRunResult run_cuda_operation(
    const Volume&,
    const dslt_operation_request&,
    const std::function<bool(float)>&) {
    return {DSLT_BACKEND_UNAVAILABLE, {}, "CUDA backend not compiled"};
}

} // namespace dslt
