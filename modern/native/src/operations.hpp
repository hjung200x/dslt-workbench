#ifndef DSLT_OPERATIONS_HPP
#define DSLT_OPERATIONS_HPP

#include "dslt/core.hpp"

namespace dslt::ops {

struct Vec3 final {
    float x;
    float y;
    float z;

    bool operator==(const Vec3&) const = default;
};

struct DsltResponse final {
    std::vector<float> minimum;
    std::vector<float> alpha;
};

struct ComponentLabels final {
    std::vector<std::int32_t> labels;
    std::uint32_t component_count;
    std::uint32_t passes_completed{};
};

struct CropParameters final {
    bool enabled{};
    bool use_height_map{};
    int upper{};
    int lower{};
    int border_xy{};
    std::span<const float> height_map{};
};

struct DsltSegmentationParameters final {
    int radius;
    int direction_level;
    int kernel_type;
    float minimum_c;
    float maximum_c;
    float c_interval;
    float z_correction_factor;
    int closing_radius;
    int minimum_component_size;
    int minimum_invalid_structure_area;
    CropParameters crop{};
};

struct ThresholdSweepParameters final {
    float minimum_threshold;
    float maximum_threshold;
    float interval;
    int closing_radius;
    int minimum_component_size;
    int minimum_invalid_structure_area;
    CropParameters crop{};
};

struct HeightMapParameters final {
    int xy_radius;
    int z_radius;
    int kernel_type;
    int smooth_level;
    float threshold;
};

struct DsltWorkEstimate final {
    std::uint64_t voxel_count;
    std::uint64_t direction_count;
    std::uint64_t line_samples_per_voxel;
    std::uint64_t directional_work_items;
    std::uint64_t estimated_host_bytes;
    std::uint64_t sweep_passes;
    std::uint64_t work_item_limit;
    std::uint64_t host_memory_limit_bytes;
    bool within_limits;
};

std::vector<float> selected_channel(const Volume& volume);
DsltWorkEstimate estimate_dslt_work(
    const dslt_volume_descriptor& descriptor,
    int radius,
    int direction_level,
    bool segmentation,
    float minimum_c = 0.0F,
    float maximum_c = 0.0F,
    float c_interval = 1.0F);
void enforce_dslt_work_limits(const DsltWorkEstimate& estimate);
std::vector<Vec3> geodesic_directions(int level);
std::vector<float> line_weights(int radius, bool gaussian);
float trilinear_clamp(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    float x,
    float y,
    float z);
DsltResponse dslt_response(
    const Volume& volume,
    int radius,
    int direction_level,
    int kernel_type,
    const Engine::Progress& progress);
std::vector<float> apply_dslt_threshold(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    const DsltResponse& response,
    float constant_c_xy,
    float z_correction_factor,
    const Engine::Progress& progress);
std::vector<float> window_level(const Volume& volume, float minimum, float maximum, const Engine::Progress& progress);
std::vector<float> threshold(const Volume& volume, float value, const Engine::Progress& progress);
std::vector<float> adaptive_threshold(
    const Volume& volume,
    int radius,
    int kernel_type,
    float constant_c,
    bool include_z,
    const Engine::Progress& progress);
std::vector<float> h_minima(
    const Volume& volume,
    float height,
    int check_interval,
    const Engine::Progress& progress);
std::vector<float> smooth(const Volume& volume, int radius, bool gaussian, const Engine::Progress& progress);
std::vector<float> morphology(const Volume& volume, int radius, bool dilate, bool spherical, const Engine::Progress& progress);
std::vector<float> morphology_buffer(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    int radius,
    bool dilate,
    bool spherical,
    const Engine::Progress& progress);
std::vector<float> spherical_closing(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    int radius,
    const Engine::Progress& progress);
ComponentLabels connected_components_low_6(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    float maximum_value,
    int minimum_size_exclusive,
    std::span<const std::int32_t> excluded_labels,
    const Engine::Progress& progress);
ComponentLabels dslt_segmentation(
    const Volume& volume,
    const DsltSegmentationParameters& parameters,
    const Engine::Progress& progress);
ComponentLabels dslt_segmentation_from_response(
    std::span<const float> source,
    const dslt_volume_descriptor& descriptor,
    const DsltResponse& response,
    const DsltSegmentationParameters& parameters,
    const Engine::Progress& progress);
ComponentLabels threshold_sweep(
    const Volume& volume,
    const ThresholdSweepParameters& parameters,
    const Engine::Progress& progress);
ComponentLabels watershed(
    const Volume& volume,
    std::span<const std::int32_t> seed_labels,
    std::span<const std::int32_t> selected_labels,
    int minimum_seed_size,
    const CropParameters& crop,
    const Engine::Progress& progress);
std::vector<std::int32_t> connected_components(
    const Volume& volume,
    float threshold,
    int connectivity,
    int minimum_size,
    std::uint32_t& component_count,
    const Engine::Progress& progress);
std::vector<float> height_map(
    const Volume& volume,
    const HeightMapParameters& parameters,
    const Engine::Progress& progress);
std::vector<float> depth_map(const Volume& volume, float threshold, const Engine::Progress& progress);
std::vector<float> resample_z(const Volume& volume, float target_spacing, int lanczos_order, bool lanczos, std::uint32_t& output_depth, const Engine::Progress& progress);
std::vector<float> extract_plane(const Volume& volume, dslt_operation operation, int slice, std::uint32_t& width, std::uint32_t& height);
std::vector<float> dslt_threshold(
    const Volume& volume,
    int radius,
    int direction_level,
    int kernel_type,
    float constant_c_xy,
    float z_correction_factor,
    const Engine::Progress& progress);

} // namespace dslt::ops

#endif
