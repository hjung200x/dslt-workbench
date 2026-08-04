#include "cuda_backend.hpp"

namespace dslt {

BackendState query_cuda_backend() noexcept {
    return BackendState{false, false, 0, "CUDA backend not compiled"};
}

bool cuda_supports_operation(dslt_operation operation) noexcept {
    return operation == DSLT_OP_COPY ||
        operation == DSLT_OP_WINDOW_LEVEL ||
        operation == DSLT_OP_THRESHOLD_2D ||
        operation == DSLT_OP_THRESHOLD_3D ||
        operation == DSLT_OP_ADAPTIVE_THRESHOLD_2D ||
        operation == DSLT_OP_ADAPTIVE_THRESHOLD_3D ||
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
        operation == DSLT_OP_HEIGHT_PROJECTION ||
        operation == DSLT_OP_Z_GRADIENT ||
        operation == DSLT_OP_CONNECTED_COMPONENTS ||
        operation == DSLT_OP_H_MINIMA ||
        operation == DSLT_OP_WATERSHED ||
        operation == DSLT_OP_DSLT_THRESHOLD ||
        operation == DSLT_OP_DSLT_SEGMENTATION ||
        operation == DSLT_OP_THRESHOLD_SWEEP;
}

CudaRunResult run_cuda_operation(
    std::span<const float>,
    const dslt_volume_descriptor&,
    const dslt_operation_request&,
    const CudaOperationState&,
    const Engine::Progress&) noexcept {
    return {CudaRunStatus::unavailable, {}, "CUDA backend is not compiled"};
}

} // namespace dslt

