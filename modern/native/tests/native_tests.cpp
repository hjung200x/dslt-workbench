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

    dslt_destroy(handle);
    lifecycle_stress_test();
    std::cout << "DSLT native synthetic tests passed\n";
    return 0;
}
