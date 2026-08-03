#include "dslt/core.hpp"
#include "operations.hpp"

#include <algorithm>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <string_view>

namespace dslt {

namespace {

std::size_t checked_multiply(std::size_t left, std::size_t right) {
    if (left != 0 && right > std::numeric_limits<std::size_t>::max() / left) {
        throw std::overflow_error("volume dimensions overflow addressable memory");
    }
    return left * right;
}

} // namespace

std::size_t checked_element_count(const dslt_volume_descriptor& descriptor) {
    if (descriptor.width == 0 || descriptor.height == 0 || descriptor.depth == 0 || descriptor.channels == 0) {
        throw std::invalid_argument("volume dimensions and channels must be non-zero");
    }
    if (descriptor.selected_channel >= descriptor.channels) {
        throw std::invalid_argument("selected channel is outside the volume");
    }
    auto count = checked_multiply(descriptor.width, descriptor.height);
    count = checked_multiply(count, descriptor.depth);
    return checked_multiply(count, descriptor.channels);
}

Volume::Volume(dslt_volume_descriptor descriptor, std::span<const float> samples)
    : descriptor_(descriptor) {
    const auto expected = checked_element_count(descriptor);
    if (samples.size() != expected) {
        throw std::invalid_argument("sample count does not match the volume descriptor");
    }
    if (descriptor.element_count != 0 && descriptor.element_count != expected) {
        throw std::invalid_argument("descriptor element_count is inconsistent");
    }
    descriptor_.element_count = expected;
    if (descriptor_.calibration.spacing_x <= 0.0) descriptor_.calibration.spacing_x = 1.0;
    if (descriptor_.calibration.spacing_y <= 0.0) descriptor_.calibration.spacing_y = 1.0;
    if (descriptor_.calibration.spacing_z <= 0.0) descriptor_.calibration.spacing_z = 1.0;
    descriptor_.voxel_type = DSLT_VOXEL_FLOAT32;
    data_.assign(samples.begin(), samples.end());
}

std::size_t Volume::voxel_count() const noexcept {
    return static_cast<std::size_t>(descriptor_.width) * descriptor_.height * descriptor_.depth;
}

std::size_t Volume::index(std::size_t x, std::size_t y, std::size_t z) const {
    if (x >= descriptor_.width || y >= descriptor_.height || z >= descriptor_.depth) {
        throw std::out_of_range("voxel coordinate is outside the volume");
    }
    const auto channel_offset = voxel_count() * descriptor_.selected_channel;
    return channel_offset + z * descriptor_.width * descriptor_.height + y * descriptor_.width + x;
}

void Engine::set_volume(const dslt_volume_descriptor& descriptor, std::span<const float> samples) {
    Volume replacement(descriptor, samples);
    source_ = std::move(replacement);
    output_.clear();
    labels_.clear();
    selection_.clear();
    result_ = {};
    last_error_.clear();
}

dslt_status Engine::run(const dslt_operation_request& request, const Progress& progress) {
    if (source_.empty()) {
        set_error("no volume is loaded");
        return DSLT_INVALID_STATE;
    }

    const auto cuda = cuda_state();
    if (request.backend == DSLT_BACKEND_CUDA) {
        set_error(cuda.available
            ? "CUDA execution is not implemented for this operation"
            : "CUDA backend is unavailable; choose Auto or CPU");
        return cuda.available ? DSLT_NOT_IMPLEMENTED : DSLT_BACKEND_UNAVAILABLE;
    }

    result_ = {};
    result_.used_backend = DSLT_BACKEND_CPU;
    result_.width = source_.descriptor().width;
    result_.height = source_.descriptor().height;
    result_.depth = source_.descriptor().depth;
    labels_.clear();

    try {
        switch (request.operation) {
        case DSLT_OP_COPY:
            output_ = ops::selected_channel(source_);
            break;
        case DSLT_OP_WINDOW_LEVEL:
            output_ = ops::window_level(source_, request.window_min, request.window_max, progress);
            break;
        case DSLT_OP_THRESHOLD_2D:
        case DSLT_OP_THRESHOLD_3D:
            output_ = ops::threshold(source_, request.threshold, progress);
            break;
        case DSLT_OP_SMOOTH_MEAN:
            output_ = ops::smooth(source_, request.radius, false, progress);
            break;
        case DSLT_OP_SMOOTH_GAUSSIAN:
            output_ = ops::smooth(source_, request.radius, true, progress);
            break;
        case DSLT_OP_DILATE_CUBE:
            output_ = ops::morphology(source_, request.radius, true, false, progress);
            break;
        case DSLT_OP_ERODE_CUBE:
            output_ = ops::morphology(source_, request.radius, false, false, progress);
            break;
        case DSLT_OP_DILATE_SPHERE:
            output_ = ops::morphology(source_, request.radius, true, true, progress);
            break;
        case DSLT_OP_ERODE_SPHERE:
            output_ = ops::morphology(source_, request.radius, false, true, progress);
            break;
        case DSLT_OP_CONNECTED_COMPONENTS:
            labels_ = ops::connected_components(
                source_, request.threshold, request.connectivity,
                request.minimum_component_size, result_.component_count, progress);
            output_.clear();
            result_.output_kind = DSLT_OUTPUT_LABELS_INT32;
            result_.element_count = labels_.size();
            last_error_.clear();
            return DSLT_OK;
        case DSLT_OP_HEIGHT_MAP:
            output_ = ops::height_map(source_, request.threshold, progress);
            result_.width = source_.descriptor().width;
            result_.height = source_.descriptor().height;
            result_.depth = 1;
            result_.output_kind = DSLT_OUTPUT_IMAGE_FLOAT32;
            break;
        case DSLT_OP_DEPTH_MAP:
            output_ = ops::depth_map(source_, request.threshold, progress);
            break;
        case DSLT_OP_RESAMPLE_Z_AREA:
            output_ = ops::resample_z(source_, request.target_spacing_z, 0, false, result_.depth, progress);
            break;
        case DSLT_OP_RESAMPLE_Z_LANCZOS:
            output_ = ops::resample_z(source_, request.target_spacing_z, request.lanczos_order, true, result_.depth, progress);
            break;
        case DSLT_OP_EXTRACT_XY:
        case DSLT_OP_EXTRACT_YZ:
        case DSLT_OP_EXTRACT_ZX:
            output_ = ops::extract_plane(source_, request.operation, request.slice_index, result_.width, result_.height);
            result_.depth = 1;
            result_.output_kind = DSLT_OUTPUT_IMAGE_FLOAT32;
            break;
        case DSLT_OP_THRESHOLD_SWEEP:
            set_error("threshold sweep requires legacy behavior capture before implementation");
            return DSLT_NOT_IMPLEMENTED;
        default:
            set_error("unknown operation identifier");
            return DSLT_INVALID_ARGUMENT;
        }
    } catch (const std::bad_alloc&) {
        set_error("not enough memory for the requested operation");
        return DSLT_OUT_OF_MEMORY;
    } catch (const std::invalid_argument& error) {
        set_error(error.what());
        return DSLT_INVALID_ARGUMENT;
    } catch (const std::runtime_error& error) {
        if (std::string_view(error.what()) == "cancelled") {
            set_error("operation cancelled");
            return DSLT_CANCELLED;
        }
        set_error(error.what());
        return DSLT_INTERNAL_ERROR;
    }

    result_.output_kind = result_.output_kind == 0 ? DSLT_OUTPUT_VOLUME_FLOAT32 : result_.output_kind;
    result_.element_count = output_.size();
    last_error_.clear();
    return DSLT_OK;
}

} // namespace dslt
