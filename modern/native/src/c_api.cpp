#include "dslt/core.hpp"
#include "operations.hpp"

#include <algorithm>
#include <cstring>
#include <exception>
#include <limits>
#include <new>
#include <span>
#include <string>

namespace {

dslt::Engine* engine(dslt_handle handle) noexcept {
    return static_cast<dslt::Engine*>(handle);
}

dslt_status fail(dslt::Engine* instance, dslt_status status, const char* message) noexcept {
    if (instance != nullptr) instance->set_error(message);
    return status;
}

} // namespace

extern "C" {

uint32_t DSLT_CALL dslt_get_abi_version(void) {
    return DSLT_ABI_VERSION;
}

dslt_status DSLT_CALL dslt_create(dslt_handle* out_handle) {
    if (out_handle == nullptr) return DSLT_INVALID_ARGUMENT;
    *out_handle = nullptr;
    try {
        *out_handle = new dslt::Engine();
        return DSLT_OK;
    } catch (const std::bad_alloc&) {
        return DSLT_OUT_OF_MEMORY;
    } catch (...) {
        return DSLT_INTERNAL_ERROR;
    }
}

void DSLT_CALL dslt_destroy(dslt_handle handle) {
    delete engine(handle);
}

dslt_status DSLT_CALL dslt_get_backend_info(dslt_handle handle, dslt_backend_info* out_info) {
    auto* instance = engine(handle);
    if (instance == nullptr || out_info == nullptr) return DSLT_INVALID_ARGUMENT;
    const auto cuda = instance->cuda_state();
    *out_info = {};
    out_info->cpu_available = 1;
    out_info->cuda_compiled = cuda.compiled ? 1 : 0;
    out_info->cuda_available = cuda.available ? 1 : 0;
    out_info->device_memory_bytes = cuda.device_memory_bytes;
    const auto count = std::min(cuda.device_name.size(), sizeof(out_info->device_name) - 1);
    std::memcpy(out_info->device_name, cuda.device_name.data(), count);
    out_info->device_name[count] = '\0';
    return DSLT_OK;
}

dslt_status DSLT_CALL dslt_set_volume_f32(
    dslt_handle handle,
    const dslt_volume_descriptor* descriptor,
    const float* data,
    uint64_t element_count) {
    auto* instance = engine(handle);
    if (instance == nullptr || descriptor == nullptr || data == nullptr) return DSLT_INVALID_ARGUMENT;
    if (element_count > static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max())) {
        return fail(instance, DSLT_INVALID_ARGUMENT, "element count exceeds addressable memory");
    }
    try {
        instance->set_volume(*descriptor, std::span<const float>(data, static_cast<std::size_t>(element_count)));
        return DSLT_OK;
    } catch (const std::bad_alloc&) {
        return fail(instance, DSLT_OUT_OF_MEMORY, "not enough memory to load volume");
    } catch (const std::exception& error) {
        instance->set_error(error.what());
        return DSLT_INVALID_ARGUMENT;
    } catch (...) {
        return fail(instance, DSLT_INTERNAL_ERROR, "unexpected native error while loading volume");
    }
}

dslt_status DSLT_CALL dslt_set_crop(
    dslt_handle handle,
    const dslt_crop_options* options,
    const float* height_map,
    uint64_t height_map_element_count) {
    auto* instance = engine(handle);
    if (instance == nullptr || options == nullptr ||
        (height_map == nullptr && height_map_element_count != 0)) return DSLT_INVALID_ARGUMENT;
    if (height_map_element_count > static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max())) {
        return fail(instance, DSLT_INVALID_ARGUMENT, "height map count exceeds addressable memory");
    }
    try {
        const auto values = height_map == nullptr
            ? std::span<const float>{}
            : std::span<const float>(height_map, static_cast<std::size_t>(height_map_element_count));
        instance->set_crop(*options, values);
        return DSLT_OK;
    } catch (const std::bad_alloc&) {
        return fail(instance, DSLT_OUT_OF_MEMORY, "not enough memory to configure crop state");
    } catch (const std::exception& error) {
        instance->set_error(error.what());
        return DSLT_INVALID_ARGUMENT;
    } catch (...) {
        return fail(instance, DSLT_INTERNAL_ERROR, "unexpected native error while configuring crop state");
    }
}

dslt_status DSLT_CALL dslt_set_label_state_i32(
    dslt_handle handle,
    const int32_t* labels,
    uint64_t label_element_count,
    const int32_t* selected_labels,
    uint64_t selected_label_count) {
    auto* instance = engine(handle);
    if (instance == nullptr || labels == nullptr ||
        (selected_labels == nullptr && selected_label_count != 0)) return DSLT_INVALID_ARGUMENT;
    if (label_element_count > static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max()) ||
        selected_label_count > static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max())) {
        return fail(instance, DSLT_INVALID_ARGUMENT, "label state count exceeds addressable memory");
    }
    try {
        const auto selection = selected_labels == nullptr
            ? std::span<const std::int32_t>{}
            : std::span<const std::int32_t>(selected_labels, static_cast<std::size_t>(selected_label_count));
        instance->set_label_state(
            std::span<const std::int32_t>(labels, static_cast<std::size_t>(label_element_count)),
            selection);
        return DSLT_OK;
    } catch (const std::bad_alloc&) {
        return fail(instance, DSLT_OUT_OF_MEMORY, "not enough memory to configure label state");
    } catch (const std::exception& error) {
        instance->set_error(error.what());
        return DSLT_INVALID_ARGUMENT;
    } catch (...) {
        return fail(instance, DSLT_INTERNAL_ERROR, "unexpected native error while configuring label state");
    }
}

dslt_status DSLT_CALL dslt_estimate_operation(
    dslt_handle handle,
    const dslt_operation_request* request,
    dslt_work_estimate* out_estimate) {
    auto* instance = engine(handle);
    if (instance == nullptr || request == nullptr || out_estimate == nullptr) return DSLT_INVALID_ARGUMENT;
    *out_estimate = {};
    if (instance->volume().empty()) return fail(instance, DSLT_INVALID_STATE, "no volume is loaded");
    try {
        const auto segmentation = request->operation == DSLT_OP_DSLT_SEGMENTATION;
        if (!segmentation && request->operation != DSLT_OP_DSLT_THRESHOLD) {
            return fail(instance, DSLT_INVALID_ARGUMENT, "work estimates are available only for DSLT operations");
        }
        const auto estimate = dslt::ops::estimate_dslt_work(
            instance->volume().descriptor(), request->radius, request->lanczos_order,
            segmentation, request->constant_c, request->window_min, request->window_max);
        out_estimate->voxel_count = estimate.voxel_count;
        out_estimate->direction_count = estimate.direction_count;
        out_estimate->line_samples_per_voxel = estimate.line_samples_per_voxel;
        out_estimate->directional_work_items = estimate.directional_work_items;
        out_estimate->estimated_host_bytes = estimate.estimated_host_bytes;
        out_estimate->sweep_passes = estimate.sweep_passes;
        out_estimate->work_item_limit = estimate.work_item_limit;
        out_estimate->host_memory_limit_bytes = estimate.host_memory_limit_bytes;
        out_estimate->within_limits = estimate.within_limits ? 1 : 0;
        return DSLT_OK;
    } catch (const dslt::ResourceLimitError& error) {
        instance->set_error(error.what());
        return DSLT_RESOURCE_LIMIT;
    } catch (const std::exception& error) {
        instance->set_error(error.what());
        return DSLT_INVALID_ARGUMENT;
    } catch (...) {
        return fail(instance, DSLT_INTERNAL_ERROR, "unexpected native error while estimating DSLT work");
    }
}

dslt_status DSLT_CALL dslt_get_volume_descriptor(dslt_handle handle, dslt_volume_descriptor* out_descriptor) {
    auto* instance = engine(handle);
    if (instance == nullptr || out_descriptor == nullptr) return DSLT_INVALID_ARGUMENT;
    if (instance->volume().empty()) return fail(instance, DSLT_INVALID_STATE, "no volume is loaded");
    *out_descriptor = instance->volume().descriptor();
    return DSLT_OK;
}

dslt_status DSLT_CALL dslt_run_operation(
    dslt_handle handle,
    const dslt_operation_request* request,
    dslt_progress_callback progress,
    void* user_data,
    dslt_operation_result* out_result) {
    auto* instance = engine(handle);
    if (instance == nullptr || request == nullptr || out_result == nullptr) return DSLT_INVALID_ARGUMENT;
    const auto callback = [progress, user_data](float value) {
        return progress == nullptr || progress(value, user_data) == 0;
    };
    const auto status = instance->run(*request, callback);
    if (status == DSLT_OK) *out_result = instance->result();
    return status;
}

dslt_status DSLT_CALL dslt_copy_output_f32(dslt_handle handle, float* destination, uint64_t element_count) {
    auto* instance = engine(handle);
    if (instance == nullptr || destination == nullptr) return DSLT_INVALID_ARGUMENT;
    const auto& output = instance->output();
    if (element_count != output.size()) return fail(instance, DSLT_INVALID_ARGUMENT, "output destination has the wrong length");
    std::copy(output.begin(), output.end(), destination);
    return DSLT_OK;
}

dslt_status DSLT_CALL dslt_copy_labels_i32(dslt_handle handle, int32_t* destination, uint64_t element_count) {
    auto* instance = engine(handle);
    if (instance == nullptr || destination == nullptr) return DSLT_INVALID_ARGUMENT;
    const auto& labels = instance->labels();
    if (element_count != labels.size()) return fail(instance, DSLT_INVALID_ARGUMENT, "label destination has the wrong length");
    std::copy(labels.begin(), labels.end(), destination);
    return DSLT_OK;
}

dslt_status DSLT_CALL dslt_get_last_error(
    dslt_handle handle,
    char* destination,
    size_t destination_size,
    size_t* required_size) {
    auto* instance = engine(handle);
    if (instance == nullptr || required_size == nullptr) return DSLT_INVALID_ARGUMENT;
    const auto& message = instance->last_error();
    *required_size = message.size() + 1;
    if (destination == nullptr || destination_size < *required_size) return DSLT_INVALID_ARGUMENT;
    std::memcpy(destination, message.c_str(), *required_size);
    return DSLT_OK;
}

} // extern "C"
