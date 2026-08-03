#include "dslt/core.hpp"

namespace dslt {

BackendState query_cuda_backend() noexcept {
    return BackendState{false, false, 0, "CUDA backend not compiled"};
}

} // namespace dslt

