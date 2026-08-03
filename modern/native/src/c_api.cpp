#include "dslt/core.hpp"

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
