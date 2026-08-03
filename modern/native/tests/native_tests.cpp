#include "dslt/c_api.h"

#include <algorithm>
#include <cassert>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <vector>

namespace {

dslt_volume_descriptor descriptor(std::uint32_t width, std::uint32_t height, std::uint32_t depth) {
    dslt_volume_descriptor value{};
    value.width = width;
    value.height = height;
    value.depth = depth;
    value.channels = 1;
    value.voxel_type = DSLT_VOXEL_FLOAT32;
    value.element_count = static_cast<std::uint64_t>(width) * height * depth;
    value.calibration = {1.0, 1.0, 2.0, 1, {}};
    return value;
}

void require(dslt_status actual, dslt_status expected = DSLT_OK) {
    if (actual != expected) {
        std::cerr << "unexpected DSLT status: " << actual << " expected " << expected << '\n';
        std::abort();
    }
}

void lifecycle_stress_test() {
    constexpr int iterations = 250;
    auto desc = descriptor(16, 12, 8);
    std::vector<float> volume(desc.element_count, 0.0F);
    for (std::size_t index = 0; index < volume.size(); index += 17) volume[index] = 1.0F;

    for (int iteration = 0; iteration < iterations; ++iteration) {
        dslt_handle handle = nullptr;
        require(dslt_create(&handle));
        require(dslt_set_volume_f32(handle, &desc, volume.data(), volume.size()));

        dslt_operation_request request{};
        request.operation = iteration % 2 == 0 ? DSLT_OP_SMOOTH_MEAN : DSLT_OP_CONNECTED_COMPONENTS;
        request.backend = DSLT_BACKEND_CPU;
        request.radius = 1;
        request.threshold = 0.5F;
        request.connectivity = 26;
        request.minimum_component_size = 1;
        dslt_operation_result result{};
        require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));

        if (result.output_kind == DSLT_OUTPUT_LABELS_INT32) {
            std::vector<std::int32_t> labels(result.element_count);
            require(dslt_copy_labels_i32(handle, labels.data(), labels.size()));
        } else {
            std::vector<float> output(result.element_count);
            require(dslt_copy_output_f32(handle, output.data(), output.size()));
        }
        dslt_destroy(handle);
    }
}

} // namespace

int main() {
    assert(dslt_get_abi_version() == 1);
    dslt_handle handle = nullptr;
    require(dslt_create(&handle));
    dslt_backend_info backend{};
    require(dslt_get_backend_info(handle, &backend));
    assert(backend.cpu_available == 1);

    auto desc = descriptor(5, 5, 3);
    std::vector<float> impulse(desc.element_count, 0.0F);
    impulse[1 * 25 + 2 * 5 + 2] = 1.0F;
    require(dslt_set_volume_f32(handle, &desc, impulse.data(), impulse.size()));

    dslt_operation_request request{};
    request.operation = DSLT_OP_DILATE_SPHERE;
    request.backend = DSLT_BACKEND_CPU;
    request.radius = 1;
    dslt_operation_result result{};
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.element_count == impulse.size());
    std::vector<float> dilated(result.element_count);
    require(dslt_copy_output_f32(handle, dilated.data(), dilated.size()));
    assert(std::count(dilated.begin(), dilated.end(), 1.0F) == 7);

    std::vector<float> objects(desc.element_count, 0.0F);
    objects[0] = objects[1] = 1.0F;
    objects.back() = 1.0F;
    require(dslt_set_volume_f32(handle, &desc, objects.data(), objects.size()));
    request = {};
    request.operation = DSLT_OP_CONNECTED_COMPONENTS;
    request.backend = DSLT_BACKEND_AUTO;
    request.threshold = 0.5F;
    request.connectivity = 6;
    request.minimum_component_size = 1;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.component_count == 2);
    std::vector<std::int32_t> labels(result.element_count);
    require(dslt_copy_labels_i32(handle, labels.data(), labels.size()));
    assert(labels[0] == labels[1]);
    assert(labels[0] != labels.back());

    request = {};
    request.operation = DSLT_OP_RESAMPLE_Z_AREA;
    request.backend = DSLT_BACKEND_CPU;
    request.target_spacing_z = 1.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.depth == 6);

    request = {};
    request.operation = DSLT_OP_SMOOTH_MEAN;
    request.backend = DSLT_BACKEND_CPU;
    request.radius = 2;
    const auto cancel = [](float progress, void*) -> std::int32_t { return progress > 0.0F ? 1 : 0; };
    require(dslt_run_operation(handle, &request, cancel, nullptr, &result), DSLT_CANCELLED);

    request = {};
    request.operation = DSLT_OP_COPY;
    request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_status = dslt_run_operation(handle, &request, nullptr, nullptr, &result);
    assert(cuda_status == DSLT_BACKEND_UNAVAILABLE || cuda_status == DSLT_NOT_IMPLEMENTED);

    auto invalid = descriptor(2, 2, 2);
    require(dslt_set_volume_f32(handle, &invalid, objects.data(), 3), DSLT_INVALID_ARGUMENT);

    auto dslt_desc = descriptor(3, 3, 3);
    std::vector<float> constant(dslt_desc.element_count, 0.5F);
    require(dslt_set_volume_f32(handle, &dslt_desc, constant.data(), constant.size()));
    request = {};
    request.operation = DSLT_OP_DSLT_THRESHOLD;
    request.backend = DSLT_BACKEND_CPU;
    request.radius = 1;
    request.lanczos_order = 1; // DSLT direction level in the v1 common request.
    request.connectivity = 1;  // DSLT mean kernel in the v1 common request.
    request.constant_c = 0.0F;
    request.target_spacing_z = 0.2F; // DSLT Z correction factor in the v1 common request.
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    std::vector<float> dslt_mask(result.element_count);
    require(dslt_copy_output_f32(handle, dslt_mask.data(), dslt_mask.size()));
    assert(std::count(dslt_mask.begin(), dslt_mask.end(), 0.0F) == static_cast<std::ptrdiff_t>(dslt_mask.size()));

    request.constant_c = 0.1F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    require(dslt_copy_output_f32(handle, dslt_mask.data(), dslt_mask.size()));
    assert(std::count(dslt_mask.begin(), dslt_mask.end(), 0.8F) == static_cast<std::ptrdiff_t>(dslt_mask.size()));

    request.lanczos_order = 0;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = {};
    request.operation = DSLT_OP_DSLT_SEGMENTATION;
    request.backend = DSLT_BACKEND_CPU;
    request.radius = 1;
    request.lanczos_order = 1;
    request.connectivity = 1;
    request.minimum_component_size = 0;
    request.slice_index = 0;
    request.threshold = 0.0F;
    request.constant_c = 0.0F;
    request.window_min = 0.0F;
    request.window_max = 0.1F;
    request.target_spacing_z = 0.2F;
    dslt_work_estimate work{};
    require(dslt_estimate_operation(handle, &request, &work));
    assert(work.voxel_count == 27);
    assert(work.direction_count == 21);
    assert(work.line_samples_per_voxel == 3);
    assert(work.directional_work_items == 1701);
    assert(work.estimated_host_bytes == 1548);
    assert(work.sweep_passes == 1);
    assert(work.within_limits == 1);
    dslt_crop_options crop{};
    crop.enabled = 1;
    crop.use_height_map = 1;
    crop.upper = 0;
    crop.lower = 0;
    crop.border_xy = 1;
    std::vector<float> height_map(9, 1.0F);
    require(dslt_set_crop(handle, &crop, height_map.data(), height_map.size() - 1), DSLT_INVALID_ARGUMENT);
    require(dslt_set_crop(handle, &crop, height_map.data(), height_map.size()));
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.output_kind == DSLT_OUTPUT_LABELS_INT32);
    assert(result.component_count == 1);
    assert(result.reserved == 1);
    labels.assign(result.element_count, -1);
    require(dslt_copy_labels_i32(handle, labels.data(), labels.size()));
    assert(std::count(labels.begin(), labels.end(), 0) == 1);
    assert(labels[1 * 9 + 1 * 3 + 1] == 0);

    request.window_max = 0.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    auto sweep_desc = descriptor(9, 5, 1);
    std::vector<float> sweep_source(sweep_desc.element_count, 1.0F);
    const auto sweep_index = [&sweep_desc](std::size_t x, std::size_t y) {
        return y * sweep_desc.width + x;
    };
    sweep_source[sweep_index(0, 2)] = 0.0F;
    for (std::size_t y = 1; y <= 3; ++y) {
        for (std::size_t x = 4; x <= 6; ++x) {
            if (x != 5 || y != 2) sweep_source[sweep_index(x, y)] = 0.0F;
        }
    }
    require(dslt_set_volume_f32(handle, &sweep_desc, sweep_source.data(), sweep_source.size()));
    crop = {};
    require(dslt_set_crop(handle, &crop, nullptr, 0));
    request = {};
    request.operation = DSLT_OP_THRESHOLD_SWEEP;
    request.backend = DSLT_BACKEND_CPU;
    request.minimum_component_size = 0;
    request.slice_index = 0;  // Spherical closing radius in the v1 common request.
    request.threshold = 0.0F; // Minimum invalid-structure area in the v1 common request.
    request.constant_c = 0.4F; // Minimum threshold in the v1 common request.
    request.window_min = 0.8F; // Maximum threshold in the v1 common request.
    request.window_max = 0.4F; // Threshold interval in the v1 common request.
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.output_kind == DSLT_OUTPUT_LABELS_INT32);
    assert(result.component_count == 2);
    assert(result.reserved == 2);
    labels.assign(result.element_count, -1);
    require(dslt_copy_labels_i32(handle, labels.data(), labels.size()));
    assert(labels[sweep_index(0, 2)] == 0);
    assert(labels[sweep_index(4, 1)] == 1);
    assert(labels[sweep_index(5, 2)] == -1);

    request.window_max = 0.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    dslt_destroy(handle);
    lifecycle_stress_test();
    std::cout << "DSLT native synthetic tests passed\n";
    return 0;
}
