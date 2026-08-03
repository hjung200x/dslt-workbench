#ifndef DSLT_OPERATIONS_HPP
#define DSLT_OPERATIONS_HPP

#include "dslt/core.hpp"

namespace dslt::ops {

std::vector<float> selected_channel(const Volume& volume);
std::vector<float> window_level(const Volume& volume, float minimum, float maximum, const Engine::Progress& progress);
std::vector<float> threshold(const Volume& volume, float value, const Engine::Progress& progress);
std::vector<float> smooth(const Volume& volume, int radius, bool gaussian, const Engine::Progress& progress);
std::vector<float> morphology(const Volume& volume, int radius, bool dilate, bool spherical, const Engine::Progress& progress);
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

} // namespace dslt::ops

#endif

