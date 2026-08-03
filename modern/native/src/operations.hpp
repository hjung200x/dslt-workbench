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
};

std::vector<float> selected_channel(const Volume& volume);
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
    const Engine::Progress& progress);
std::vector<std::int32_t> connected_components(
    const Volume& volume,
    float threshold,
    int connectivity,
    int minimum_size,
    std::uint32_t& component_count,
    const Engine::Progress& progress);
std::vector<float> height_map(const Volume& volume, float threshold, const Engine::Progress& progress);
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
