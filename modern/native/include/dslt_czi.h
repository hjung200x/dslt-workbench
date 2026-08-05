#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(DSLT_CZI_EXPORTS)
#    define DSLT_CZI_API __declspec(dllexport)
#  else
#    define DSLT_CZI_API __declspec(dllimport)
#  endif
#else
#  define DSLT_CZI_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define DSLT_CZI_ABI_VERSION_V1 0x00010000u

typedef struct dslt_czi_document dslt_czi_document;
typedef struct dslt_czi_volume dslt_czi_volume;

typedef enum dslt_czi_status {
    DSLT_CZI_OK = 0,
    DSLT_CZI_INVALID_ARGUMENT = 1,
    DSLT_CZI_UNSUPPORTED = 2,
    DSLT_CZI_IO_ERROR = 3,
    DSLT_CZI_INVALID_DATA = 4,
    DSLT_CZI_ALLOCATION_LIMIT = 5,
    DSLT_CZI_CANCELLED = 6,
    DSLT_CZI_INTERNAL_ERROR = 7,
    DSLT_CZI_BUFFER_TOO_SMALL = 8
} dslt_czi_status;

typedef enum dslt_czi_voxel_type {
    DSLT_CZI_VOXEL_UINT8 = 1,
    DSLT_CZI_VOXEL_UINT16 = 2,
    DSLT_CZI_VOXEL_FLOAT32 = 6
} dslt_czi_voxel_type;

typedef enum dslt_czi_document_flags {
    DSLT_CZI_DOCUMENT_HAS_PYRAMID = 1u << 0,
    DSLT_CZI_DOCUMENT_HAS_MULTIPLE_TILES = 1u << 1,
    DSLT_CZI_DOCUMENT_METADATA_VALID = 1u << 2
} dslt_czi_document_flags;

typedef enum dslt_czi_spacing_flags {
    DSLT_CZI_SPACING_X_VALID = 1u << 0,
    DSLT_CZI_SPACING_Y_VALID = 1u << 1,
    DSLT_CZI_SPACING_Z_VALID = 1u << 2
} dslt_czi_spacing_flags;

typedef struct dslt_czi_document_info_v1 {
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t scene_count;
    int32_t time_start;
    int32_t time_count;
    int32_t channel_start;
    int32_t channel_count;
    int32_t z_start;
    int32_t z_count;
    uint32_t document_flags;
    uint32_t unsupported_dimensions_mask;
    uint32_t spacing_flags;
    uint32_t reserved0;
    double spacing_x_um;
    double spacing_y_um;
    double spacing_z_um;
    uint64_t subblock_count;
} dslt_czi_document_info_v1;

typedef struct dslt_czi_scene_info_v1 {
    uint32_t struct_size;
    int32_t ordinal;
    int32_t scene_index;
    int32_t x;
    int32_t y;
    int32_t width;
    int32_t height;
    uint32_t reserved0;
} dslt_czi_scene_info_v1;

typedef struct dslt_czi_channel_info_v1 {
    uint32_t struct_size;
    int32_t channel_index;
    uint32_t voxel_type;
    uint8_t red;
    uint8_t green;
    uint8_t blue;
    uint8_t alpha;
    uint32_t flags;
    uint32_t reserved0;
} dslt_czi_channel_info_v1;

typedef struct dslt_czi_selection_v1 {
    uint32_t struct_size;
    int32_t scene_index;
    int32_t time_index;
    const int32_t* channel_indices;
    size_t channel_count;
    uint64_t maximum_output_bytes;
} dslt_czi_selection_v1;

typedef struct dslt_czi_volume_info_v1 {
    uint32_t struct_size;
    uint32_t voxel_type;
    int32_t width;
    int32_t height;
    int32_t depth;
    int32_t channels;
    uint32_t bytes_per_sample;
    uint32_t spacing_flags;
    double spacing_x_um;
    double spacing_y_um;
    double spacing_z_um;
    uint64_t raw_sample_bytes;
} dslt_czi_volume_info_v1;

typedef int32_t (*dslt_czi_progress_callback)(float progress, void* user_data);

DSLT_CZI_API uint32_t dslt_czi_get_abi_version(void);
DSLT_CZI_API int32_t dslt_czi_open_utf8(const char* path_utf8, dslt_czi_document** out_document);
DSLT_CZI_API int32_t dslt_czi_close(dslt_czi_document* document);
DSLT_CZI_API int32_t dslt_czi_get_document_info(
    const dslt_czi_document* document,
    dslt_czi_document_info_v1* out_info);
DSLT_CZI_API int32_t dslt_czi_get_scene_info(
    const dslt_czi_document* document,
    int32_t ordinal,
    dslt_czi_scene_info_v1* out_info);
DSLT_CZI_API int32_t dslt_czi_get_channel_info(
    const dslt_czi_document* document,
    int32_t channel_index,
    dslt_czi_channel_info_v1* out_info);
DSLT_CZI_API int32_t dslt_czi_copy_channel_name_utf8(
    const dslt_czi_document* document,
    int32_t channel_index,
    char* destination,
    size_t destination_size,
    size_t* required_size);
DSLT_CZI_API int32_t dslt_czi_read_volume(
    dslt_czi_document* document,
    const dslt_czi_selection_v1* selection,
    dslt_czi_progress_callback progress,
    void* user_data,
    dslt_czi_volume** out_volume);
DSLT_CZI_API int32_t dslt_czi_get_volume_info(
    const dslt_czi_volume* volume,
    dslt_czi_volume_info_v1* out_info);
DSLT_CZI_API int32_t dslt_czi_copy_raw_samples(
    const dslt_czi_volume* volume,
    void* destination,
    size_t destination_size);
DSLT_CZI_API int32_t dslt_czi_release_volume(dslt_czi_volume* volume);
DSLT_CZI_API int32_t dslt_czi_copy_last_error_utf8(
    char* destination,
    size_t destination_size,
    size_t* required_size);

#ifdef __cplusplus
}
#endif
