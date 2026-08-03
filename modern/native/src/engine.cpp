#include "dslt/core.hpp"
#include "cuda_backend.hpp"
#include "operations.hpp"

#include <algorithm>
#include <cmath>
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
    crop_ = {};
    result_ = {};
    last_error_.clear();
}

void Engine::set_crop(const dslt_crop_options& options, std::span<const float> height_map) {
    if (options.enabled > 1 || options.use_height_map > 1) {
        throw std::invalid_argument("crop flags must be 0 or 1");
    }
    if (options.enabled == 0) {
        crop_ = {};
        return;
    }
    if (source_.empty()) throw std::invalid_argument("a volume must be loaded before crop configuration");
    if (options.upper > options.lower) throw std::invalid_argument("crop upper bound must not exceed lower bound");
    if (options.border_xy < 0) throw std::invalid_argument("crop XY border must be non-negative");
    const auto expected = static_cast<std::size_t>(source_.descriptor().width) * source_.descriptor().height;
    if (options.use_height_map != 0) {
        if (height_map.size() != expected) throw std::invalid_argument("crop height map dimensions do not match the volume");
        if (!std::all_of(height_map.begin(), height_map.end(), [](float value) { return std::isfinite(value); })) {
            throw std::invalid_argument("crop height map must contain only finite values");
        }
    }
    crop_.options = options;
    crop_.height_map = options.use_height_map != 0
        ? std::vector<float>(height_map.begin(), height_map.end())
        : std::vector<float>{};
}

void Engine::set_label_state(
    std::span<const std::int32_t> labels,
    std::span<const std::int32_t> selected_labels) {
    if (source_.empty()) throw std::invalid_argument("a volume must be loaded before label state");
    if (labels.size() != source_.voxel_count()) {
        throw std::invalid_argument("label state dimensions do not match the volume");
    }
    if (!std::all_of(labels.begin(), labels.end(), [](std::int32_t value) { return value >= -1; })) {
        throw std::invalid_argument("label state values must be -1 or non-negative");
    }
    std::unordered_set<std::int32_t> available;
    for (const auto label : labels) {
        if (label >= 0) available.insert(label);
    }
    std::unordered_set<std::int32_t> replacement_selection;
    for (const auto label : selected_labels) {
        if (label < 0 || !available.contains(label)) {
            throw std::invalid_argument("selected seed label does not exist in the label state");
        }
        replacement_selection.insert(label);
    }
    labels_.assign(labels.begin(), labels.end());
    selection_ = std::move(replacement_selection);
}

dslt_status Engine::run(const dslt_operation_request& request, const Progress& progress) {
    if (source_.empty()) {
        set_error("no volume is loaded");
        return DSLT_INVALID_STATE;
    }

    const auto cuda = cuda_state();
    const auto explicit_cuda = request.backend == DSLT_BACKEND_CUDA;
    const auto supports_cuda = cuda_supports_operation(request.operation);
    if (explicit_cuda && !cuda.available) {
        set_error("CUDA backend is unavailable; choose Auto or CPU");
        return DSLT_BACKEND_UNAVAILABLE;
    }
    if (explicit_cuda && !supports_cuda) {
        set_error("CUDA execution is not implemented for this operation");
        return DSLT_NOT_IMPLEMENTED;
    }

    if (request.backend != DSLT_BACKEND_CPU && cuda.available && supports_cuda) {
        const auto channel_offset = source_.voxel_count() * source_.descriptor().selected_channel;
        const auto channel = source_.data().subspan(channel_offset, source_.voxel_count());
        auto cuda_result = run_cuda_operation(channel, source_.descriptor(), request, progress);
        if (cuda_result.status == CudaRunStatus::success) {
            result_ = {};
            result_.used_backend = DSLT_BACKEND_CUDA;
            result_.output_kind = cuda_result.output_kind;
            result_.width = cuda_result.width;
            result_.height = cuda_result.height;
            result_.depth = cuda_result.depth;
            result_.component_count = cuda_result.component_count;
            result_.reserved = cuda_result.passes_completed;
            if (cuda_result.output_kind == DSLT_OUTPUT_LABELS_INT32) {
                labels_ = std::move(cuda_result.labels);
                output_.clear();
                result_.element_count = labels_.size();
            } else {
                output_ = std::move(cuda_result.output);
                labels_.clear();
                result_.element_count = output_.size();
            }
            selection_.clear();
            last_error_.clear();
            return DSLT_OK;
        }

        // Auto falls back only when CUDA becomes unavailable during initialization.
        // Invalid parameters, cancellation, memory exhaustion, and kernel failures are
        // surfaced so a failed GPU operation cannot silently produce a different path.
        if (cuda_result.status != CudaRunStatus::unavailable || explicit_cuda) {
            set_error(cuda_result.error);
            switch (cuda_result.status) {
            case CudaRunStatus::unsupported: return DSLT_NOT_IMPLEMENTED;
            case CudaRunStatus::invalid_argument: return DSLT_INVALID_ARGUMENT;
            case CudaRunStatus::out_of_memory: return DSLT_OUT_OF_MEMORY;
            case CudaRunStatus::resource_limit: return DSLT_RESOURCE_LIMIT;
            case CudaRunStatus::cancelled: return DSLT_CANCELLED;
            case CudaRunStatus::unavailable: return DSLT_BACKEND_UNAVAILABLE;
            case CudaRunStatus::internal_error: return DSLT_INTERNAL_ERROR;
            case CudaRunStatus::success: break;
            }
        }
    }

    try {
        auto seed_labels = request.operation == DSLT_OP_WATERSHED
            ? labels_
            : std::vector<std::int32_t>{};
        auto selected_seed_labels = request.operation == DSLT_OP_WATERSHED
            ? std::vector<std::int32_t>(selection_.begin(), selection_.end())
            : std::vector<std::int32_t>{};

        result_ = {};
        result_.used_backend = DSLT_BACKEND_CPU;
        result_.width = source_.descriptor().width;
        result_.height = source_.descriptor().height;
        result_.depth = source_.descriptor().depth;
        labels_.clear();
        selection_.clear();

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
        case DSLT_OP_ADAPTIVE_THRESHOLD_2D:
        case DSLT_OP_ADAPTIVE_THRESHOLD_3D:
            output_ = ops::adaptive_threshold(
                source_, request.radius, request.connectivity, request.constant_c,
                request.operation == DSLT_OP_ADAPTIVE_THRESHOLD_3D, progress);
            break;
        case DSLT_OP_H_MINIMA:
            output_ = ops::h_minima(source_, request.threshold, request.radius, progress);
            break;
        case DSLT_OP_WATERSHED: {
            const ops::CropParameters crop{
                crop_.options.enabled != 0,
                crop_.options.use_height_map != 0,
                crop_.options.upper,
                crop_.options.lower,
                crop_.options.border_xy,
                crop_.height_map,
            };
            auto segmentation = ops::watershed(
                source_, seed_labels, selected_seed_labels,
                request.minimum_component_size, crop, progress);
            labels_ = std::move(segmentation.labels);
            output_.clear();
            result_.component_count = segmentation.component_count;
            result_.reserved = segmentation.passes_completed;
            result_.output_kind = DSLT_OUTPUT_LABELS_INT32;
            result_.element_count = labels_.size();
            last_error_.clear();
            return DSLT_OK;
        }
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
            output_ = ops::height_map(
                source_,
                ops::HeightMapParameters{
                    request.radius,
                    request.lanczos_order,
                    request.connectivity,
                    request.slice_index,
                    request.threshold,
                },
                progress);
            result_.width = source_.descriptor().width;
            result_.height = source_.descriptor().height;
            result_.depth = 1;
            result_.output_kind = DSLT_OUTPUT_IMAGE_FLOAT32;
            break;
        case DSLT_OP_DEPTH_MAP:
            output_ = ops::depth_map(
                source_,
                ops::HeightMapParameters{
                    request.radius,
                    request.lanczos_order,
                    request.connectivity,
                    request.slice_index,
                    request.threshold,
                },
                progress);
            break;
        case DSLT_OP_HEIGHT_PROJECTION:
            output_ = ops::height_projection(
                source_,
                ops::HeightMapParameters{
                    request.radius,
                    request.lanczos_order,
                    request.connectivity,
                    request.slice_index,
                    request.threshold,
                },
                ops::HeightProjectionParameters{
                    static_cast<int>(request.target_spacing_z),
                    request.minimum_component_size,
                    request.constant_c,
                    request.window_min,
                    request.window_max,
                },
                progress);
            result_.width = source_.descriptor().width;
            result_.height = source_.descriptor().height;
            result_.depth = 1;
            result_.output_kind = DSLT_OUTPUT_IMAGE_FLOAT32;
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
        case DSLT_OP_THRESHOLD_SWEEP: {
            if (!std::isfinite(request.threshold) || request.threshold < 0.0F ||
                std::floor(request.threshold) != request.threshold ||
                request.threshold > static_cast<float>(std::numeric_limits<int>::max())) {
                throw std::invalid_argument("minimum invalid-structure area must be a non-negative integer");
            }
            const ops::ThresholdSweepParameters parameters{
                request.constant_c,
                request.window_min,
                request.window_max,
                request.slice_index,
                request.minimum_component_size,
                static_cast<int>(request.threshold),
                ops::CropParameters{
                    crop_.options.enabled != 0,
                    crop_.options.use_height_map != 0,
                    crop_.options.upper,
                    crop_.options.lower,
                    crop_.options.border_xy,
                    crop_.height_map,
                },
            };
            auto segmentation = ops::threshold_sweep(source_, parameters, progress);
            labels_ = std::move(segmentation.labels);
            output_.clear();
            result_.component_count = segmentation.component_count;
            result_.reserved = segmentation.passes_completed;
            result_.output_kind = DSLT_OUTPUT_LABELS_INT32;
            result_.element_count = labels_.size();
            last_error_.clear();
            return DSLT_OK;
        }
        case DSLT_OP_DSLT_THRESHOLD:
            output_ = ops::dslt_threshold(
                source_, request.radius, request.lanczos_order, request.connectivity,
                request.constant_c, request.target_spacing_z, progress);
            break;
        case DSLT_OP_DSLT_SEGMENTATION: {
            if (!std::isfinite(request.threshold) || request.threshold < 0.0F ||
                std::floor(request.threshold) != request.threshold ||
                request.threshold > static_cast<float>(std::numeric_limits<int>::max())) {
                throw std::invalid_argument("minimum invalid-structure area must be a non-negative integer");
            }
            const ops::DsltSegmentationParameters parameters{
                request.radius,
                request.lanczos_order,
                request.connectivity,
                request.constant_c,
                request.window_min,
                request.window_max,
                request.target_spacing_z,
                request.slice_index,
                request.minimum_component_size,
                static_cast<int>(request.threshold),
                ops::CropParameters{
                    crop_.options.enabled != 0,
                    crop_.options.use_height_map != 0,
                    crop_.options.upper,
                    crop_.options.lower,
                    crop_.options.border_xy,
                    crop_.height_map,
                },
            };
            auto segmentation = ops::dslt_segmentation(source_, parameters, progress);
            labels_ = std::move(segmentation.labels);
            output_.clear();
            result_.component_count = segmentation.component_count;
            result_.reserved = segmentation.passes_completed;
            result_.output_kind = DSLT_OUTPUT_LABELS_INT32;
            result_.element_count = labels_.size();
            last_error_.clear();
            return DSLT_OK;
        }
        default:
            set_error("unknown operation identifier");
            return DSLT_INVALID_ARGUMENT;
        }
    } catch (const ResourceLimitError& error) {
        set_error(error.what());
        return DSLT_RESOURCE_LIMIT;
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
