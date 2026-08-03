#ifndef DSLT_C_API_H
#define DSLT_C_API_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(DSLT_CORE_EXPORTS)
#    define DSLT_API __declspec(dllexport)
#  else
#    define DSLT_API __declspec(dllimport)
#  endif
#  define DSLT_CALL __cdecl
#else
#  define DSLT_API
#  define DSLT_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define DSLT_ABI_VERSION 1u

typedef void* dslt_handle;

typedef enum dslt_status {
    DSLT_OK = 0,
    DSLT_INVALID_ARGUMENT = 1,
    DSLT_INVALID_STATE = 2,
    DSLT_OUT_OF_MEMORY = 3,
    DSLT_CANCELLED = 4,
    DSLT_BACKEND_UNAVAILABLE = 5,
    DSLT_IO_ERROR = 6,
    DSLT_NOT_IMPLEMENTED = 7,
    DSLT_RESOURCE_LIMIT = 8,
    DSLT_INTERNAL_ERROR = 100
} dslt_status;

typedef enum dslt_backend {
    DSLT_BACKEND_AUTO = 0,
    DSLT_BACKEND_CPU = 1,
    DSLT_BACKEND_CUDA = 2
} dslt_backend;

typedef enum dslt_voxel_type {
    DSLT_VOXEL_UINT8 = 1,
    DSLT_VOXEL_UINT16 = 2,
    DSLT_VOXEL_INT16 = 3,
    DSLT_VOXEL_UINT32 = 4,
    DSLT_VOXEL_FLOAT32 = 5
} dslt_voxel_type;

typedef enum dslt_operation {
    DSLT_OP_COPY = 0,
    DSLT_OP_WINDOW_LEVEL = 1,
    DSLT_OP_THRESHOLD_2D = 2,
    DSLT_OP_THRESHOLD_3D = 3,
    DSLT_OP_SMOOTH_MEAN = 4,
    DSLT_OP_SMOOTH_GAUSSIAN = 5,
    DSLT_OP_DILATE_CUBE = 6,
    DSLT_OP_ERODE_CUBE = 7,
    DSLT_OP_DILATE_SPHERE = 8,
    DSLT_OP_ERODE_SPHERE = 9,
    DSLT_OP_CONNECTED_COMPONENTS = 10,
    DSLT_OP_HEIGHT_MAP = 11,
    DSLT_OP_DEPTH_MAP = 12,
    DSLT_OP_RESAMPLE_Z_AREA = 13,
    DSLT_OP_RESAMPLE_Z_LANCZOS = 14,
    DSLT_OP_EXTRACT_XY = 15,
    DSLT_OP_EXTRACT_YZ = 16,
    DSLT_OP_EXTRACT_ZX = 17,
    DSLT_OP_THRESHOLD_SWEEP = 18,
    DSLT_OP_DSLT_THRESHOLD = 19,
    DSLT_OP_DSLT_SEGMENTATION = 20,
    DSLT_OP_ADAPTIVE_THRESHOLD_2D = 21,
    DSLT_OP_ADAPTIVE_THRESHOLD_3D = 22,
    DSLT_OP_H_MINIMA = 23
} dslt_operation;

typedef struct dslt_calibration {
    double spacing_x;
    double spacing_y;
    double spacing_z;
    uint8_t calibrated;
    uint8_t reserved[7];
} dslt_calibration;

typedef struct dslt_volume_descriptor {
    uint32_t width;
    uint32_t height;
    uint32_t depth;
    uint32_t channels;
    uint32_t selected_channel;
    dslt_voxel_type voxel_type;
    uint64_t element_count;
    dslt_calibration calibration;
} dslt_volume_descriptor;

typedef struct dslt_operation_request {
    dslt_operation operation;
    dslt_backend backend;
    int32_t radius;
    int32_t connectivity;
    int32_t minimum_component_size;
    int32_t slice_index;
    int32_t lanczos_order;
    float threshold;
    float constant_c;
    float window_min;
    float window_max;
    float target_spacing_z;
} dslt_operation_request;

typedef struct dslt_crop_options {
    uint8_t enabled;
    uint8_t use_height_map;
    uint8_t reserved[2];
    int32_t upper;
    int32_t lower;
    int32_t border_xy;
} dslt_crop_options;

typedef struct dslt_work_estimate {
    uint64_t voxel_count;
    uint64_t direction_count;
    uint64_t line_samples_per_voxel;
    uint64_t directional_work_items;
    uint64_t estimated_host_bytes;
    uint64_t sweep_passes;
    uint64_t work_item_limit;
    uint64_t host_memory_limit_bytes;
    uint8_t within_limits;
    uint8_t reserved[7];
} dslt_work_estimate;

typedef enum dslt_output_kind {
    DSLT_OUTPUT_VOLUME_FLOAT32 = 1,
    DSLT_OUTPUT_LABELS_INT32 = 2,
    DSLT_OUTPUT_IMAGE_FLOAT32 = 3
} dslt_output_kind;

typedef struct dslt_operation_result {
    dslt_backend used_backend;
    dslt_output_kind output_kind;
    uint32_t width;
    uint32_t height;
    uint32_t depth;
    uint64_t element_count;
    uint32_t component_count;
    uint32_t reserved;
} dslt_operation_result;

typedef struct dslt_backend_info {
    uint8_t cpu_available;
    uint8_t cuda_compiled;
    uint8_t cuda_available;
    uint8_t reserved;
    uint64_t device_memory_bytes;
    char device_name[128];
} dslt_backend_info;

typedef int32_t (DSLT_CALL *dslt_progress_callback)(float progress, void* user_data);

DSLT_API uint32_t DSLT_CALL dslt_get_abi_version(void);
DSLT_API dslt_status DSLT_CALL dslt_create(dslt_handle* out_handle);
DSLT_API void DSLT_CALL dslt_destroy(dslt_handle handle);
DSLT_API dslt_status DSLT_CALL dslt_get_backend_info(dslt_handle handle, dslt_backend_info* out_info);
DSLT_API dslt_status DSLT_CALL dslt_set_volume_f32(
    dslt_handle handle,
    const dslt_volume_descriptor* descriptor,
    const float* data,
    uint64_t element_count);
DSLT_API dslt_status DSLT_CALL dslt_set_crop(
    dslt_handle handle,
    const dslt_crop_options* options,
    const float* height_map,
    uint64_t height_map_element_count);
DSLT_API dslt_status DSLT_CALL dslt_estimate_operation(
    dslt_handle handle,
    const dslt_operation_request* request,
    dslt_work_estimate* out_estimate);
DSLT_API dslt_status DSLT_CALL dslt_get_volume_descriptor(
    dslt_handle handle,
    dslt_volume_descriptor* out_descriptor);
DSLT_API dslt_status DSLT_CALL dslt_run_operation(
    dslt_handle handle,
    const dslt_operation_request* request,
    dslt_progress_callback progress,
    void* user_data,
    dslt_operation_result* out_result);
DSLT_API dslt_status DSLT_CALL dslt_copy_output_f32(
    dslt_handle handle,
    float* destination,
    uint64_t element_count);
DSLT_API dslt_status DSLT_CALL dslt_copy_labels_i32(
    dslt_handle handle,
    int32_t* destination,
    uint64_t element_count);
DSLT_API dslt_status DSLT_CALL dslt_get_last_error(
    dslt_handle handle,
    char* destination,
    size_t destination_size,
    size_t* required_size);

#ifdef __cplusplus
}
#endif

#endif
