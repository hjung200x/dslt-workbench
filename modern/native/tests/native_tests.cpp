#include "dslt/c_api.h"

#include <algorithm>
#include <cassert>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <limits>
#include <utility>
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

#ifdef DSLT_TEST_CUDA
struct FloatOperationOutput final {
    std::vector<float> values;
    dslt_operation_result result{};
};

FloatOperationOutput run_float_operation_result(
    dslt_handle handle,
    dslt_operation_request request,
    dslt_backend expected_backend,
    dslt_output_kind expected_kind = DSLT_OUTPUT_VOLUME_FLOAT32) {
    FloatOperationOutput output{};
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &output.result));
    assert(output.result.used_backend == expected_backend);
    assert(output.result.output_kind == expected_kind);
    output.values.resize(output.result.element_count);
    require(dslt_copy_output_f32(handle, output.values.data(), output.values.size()));
    return output;
}

std::vector<float> run_float_operation(
    dslt_handle handle,
    dslt_operation_request request,
    dslt_backend expected_backend) {
    return run_float_operation_result(handle, request, expected_backend).values;
}

void cuda_pointwise_parity_test() {
    dslt_handle handle = nullptr;
    require(dslt_create(&handle));
    dslt_backend_info backend{};
    require(dslt_get_backend_info(handle, &backend));
    assert(backend.cuda_compiled == 1);
    assert(backend.cuda_available == 1);

    auto desc = descriptor(8, 2, 1);
    const std::vector<float> source{
        -1.0F, -0.25F, 0.0F, 0.24F, 0.25F, 0.5F, 1.0F, 2.0F,
        2.0F, 1.0F, 0.5F, 0.25F, 0.24F, 0.0F, -0.25F, -1.0F,
    };
    desc.channels = 2;
    desc.selected_channel = 1;
    desc.element_count = source.size() * 2;
    std::vector<float> multichannel(source.size(), 99.0F);
    multichannel.insert(multichannel.end(), source.begin(), source.end());
    require(dslt_set_volume_f32(handle, &desc, multichannel.data(), multichannel.size()));

    for (const auto operation : {
             DSLT_OP_COPY,
             DSLT_OP_WINDOW_LEVEL,
             DSLT_OP_THRESHOLD_2D,
             DSLT_OP_THRESHOLD_3D}) {
        dslt_operation_request request{};
        request.operation = operation;
        request.window_min = -0.25F;
        request.window_max = 1.0F;
        request.threshold = 0.25F;
        request.backend = DSLT_BACKEND_CPU;
        const auto cpu = run_float_operation(handle, request, DSLT_BACKEND_CPU);
        request.backend = DSLT_BACKEND_CUDA;
        const auto cuda = run_float_operation(handle, request, DSLT_BACKEND_CUDA);
        assert(cpu.size() == cuda.size());
        for (std::size_t index = 0; index < cpu.size(); ++index) {
            assert(std::abs(cpu[index] - cuda[index]) <= 1.0e-6F);
        }
    }

    dslt_operation_request request{};
    request.operation = DSLT_OP_COPY;
    request.backend = DSLT_BACKEND_AUTO;
    const auto automatic = run_float_operation(handle, request, DSLT_BACKEND_CUDA);
    assert(automatic == source);

    auto filter_desc = descriptor(5, 4, 3);
    std::vector<float> filter_source(filter_desc.element_count);
    for (std::size_t index = 0; index < filter_source.size(); ++index) {
        const auto pattern = static_cast<int>((index * 37U) % 101U) - 50;
        filter_source[index] = static_cast<float>(pattern) / 25.0F;
    }
    filter_source[0] = 8.0F;
    filter_source[filter_source.size() / 2] = -7.0F;
    require(dslt_set_volume_f32(
        handle, &filter_desc, filter_source.data(), filter_source.size()));

    for (const auto operation : {
             DSLT_OP_SMOOTH_MEAN,
             DSLT_OP_SMOOTH_GAUSSIAN,
             DSLT_OP_DILATE_CUBE,
             DSLT_OP_ERODE_CUBE,
             DSLT_OP_DILATE_SPHERE,
             DSLT_OP_ERODE_SPHERE}) {
        dslt_operation_request filter_request{};
        filter_request.operation = operation;
        filter_request.radius = 1;
        filter_request.backend = DSLT_BACKEND_CPU;
        const auto cpu = run_float_operation(handle, filter_request, DSLT_BACKEND_CPU);
        filter_request.backend = DSLT_BACKEND_CUDA;
        const auto cuda = run_float_operation(handle, filter_request, DSLT_BACKEND_CUDA);
        assert(cpu.size() == cuda.size());
        const auto smooth_operation = operation == DSLT_OP_SMOOTH_MEAN ||
            operation == DSLT_OP_SMOOTH_GAUSSIAN;
        for (std::size_t index = 0; index < cpu.size(); ++index) {
            const auto difference = std::abs(cpu[index] - cuda[index]);
            if (smooth_operation) {
                assert(difference <= 1.0e-5F ||
                    difference <= std::abs(cpu[index]) * 1.0e-4F);
            } else {
                assert(difference == 0.0F);
            }
        }
    }

    request = {};
    request.operation = DSLT_OP_SMOOTH_GAUSSIAN;
    request.backend = DSLT_BACKEND_CUDA;
    request.radius = 0;
    const auto zero_radius = run_float_operation(handle, request, DSLT_BACKEND_CUDA);
    assert(zero_radius == filter_source);

    request = {};
    request.operation = DSLT_OP_SMOOTH_GAUSSIAN;
    request.backend = DSLT_BACKEND_AUTO;
    request.radius = 1;
    (void)run_float_operation(handle, request, DSLT_BACKEND_CUDA);

    auto resample_desc = descriptor(3, 2, 4);
    std::vector<float> resample_source(resample_desc.element_count);
    for (std::size_t z = 0; z < resample_desc.depth; ++z) {
        for (std::size_t y = 0; y < resample_desc.height; ++y) {
            for (std::size_t x = 0; x < resample_desc.width; ++x) {
                const auto index = z * resample_desc.width * resample_desc.height +
                    y * resample_desc.width + x;
                resample_source[index] = static_cast<float>(100 * z + 10 * y + x);
            }
        }
    }
    require(dslt_set_volume_f32(
        handle, &resample_desc, resample_source.data(), resample_source.size()));

    for (const auto operation : {
             DSLT_OP_RESAMPLE_Z_AREA,
             DSLT_OP_RESAMPLE_Z_LANCZOS}) {
        for (const auto order : {2, 3}) {
            if (operation == DSLT_OP_RESAMPLE_Z_AREA && order == 3) continue;
            dslt_operation_request resample_request{};
            resample_request.operation = operation;
            resample_request.target_spacing_z = 1.0F;
            resample_request.lanczos_order = order;
            resample_request.backend = DSLT_BACKEND_CPU;
            const auto cpu = run_float_operation_result(
                handle, resample_request, DSLT_BACKEND_CPU);
            resample_request.backend = DSLT_BACKEND_CUDA;
            const auto cuda = run_float_operation_result(
                handle, resample_request, DSLT_BACKEND_CUDA);
            assert(cuda.result.width == 3);
            assert(cuda.result.height == 2);
            assert(cuda.result.depth == 8);
            assert(cpu.values.size() == cuda.values.size());
            for (std::size_t index = 0; index < cpu.values.size(); ++index) {
                const auto difference = std::abs(cpu.values[index] - cuda.values[index]);
                assert(difference <= 1.0e-5F ||
                    difference <= std::abs(cpu.values[index]) * 1.0e-4F);
            }
        }
    }

    for (const auto view : {
             std::pair{DSLT_OP_EXTRACT_XY, 2},
             std::pair{DSLT_OP_EXTRACT_YZ, 1},
             std::pair{DSLT_OP_EXTRACT_ZX, 1}}) {
        dslt_operation_request view_request{};
        view_request.operation = view.first;
        view_request.slice_index = view.second;
        view_request.backend = DSLT_BACKEND_CPU;
        const auto cpu = run_float_operation_result(
            handle, view_request, DSLT_BACKEND_CPU, DSLT_OUTPUT_IMAGE_FLOAT32);
        view_request.backend = DSLT_BACKEND_CUDA;
        const auto cuda = run_float_operation_result(
            handle, view_request, DSLT_BACKEND_CUDA, DSLT_OUTPUT_IMAGE_FLOAT32);
        assert(cpu.result.width == cuda.result.width);
        assert(cpu.result.height == cuda.result.height);
        assert(cuda.result.depth == 1);
        assert(cpu.values == cuda.values);
    }

    request = {};
    request.operation = DSLT_OP_EXTRACT_YZ;
    request.slice_index = 1;
    request.backend = DSLT_BACKEND_AUTO;
    const auto automatic_view = run_float_operation_result(
        handle, request, DSLT_BACKEND_CUDA, DSLT_OUTPUT_IMAGE_FLOAT32);
    assert(automatic_view.result.width == 4);
    assert(automatic_view.result.height == 2);

    dslt_operation_result result{};
    request = {};
    request.operation = DSLT_OP_RESAMPLE_Z_AREA;
    request.backend = DSLT_BACKEND_CUDA;
    request.target_spacing_z = 0.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = {};
    request.operation = DSLT_OP_RESAMPLE_Z_LANCZOS;
    request.backend = DSLT_BACKEND_CUDA;
    request.target_spacing_z = 1.0F;
    request.lanczos_order = 1;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = {};
    request.operation = DSLT_OP_EXTRACT_YZ;
    request.backend = DSLT_BACKEND_CUDA;
    request.slice_index = static_cast<std::int32_t>(resample_desc.width);
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = {};
    request.operation = DSLT_OP_RESAMPLE_Z_AREA;
    request.backend = DSLT_BACKEND_CUDA;
    request.target_spacing_z = 0.5F;
    const auto cancel_resample = [](float progress, void*) -> std::int32_t {
        return progress >= 0.4F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &request, cancel_resample, nullptr, &result), DSLT_CANCELLED);

    request = {};
    request.operation = DSLT_OP_DILATE_SPHERE;
    request.backend = DSLT_BACKEND_CUDA;
    request.radius = 65;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = {};
    request.operation = DSLT_OP_CONNECTED_COMPONENTS;
    request.backend = DSLT_BACKEND_CUDA;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_NOT_IMPLEMENTED);

    request = {};
    request.operation = DSLT_OP_SMOOTH_MEAN;
    request.backend = DSLT_BACKEND_CUDA;
    request.radius = 2;
    const auto cancel_filter = [](float progress, void*) -> std::int32_t {
        return progress >= 0.4F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &request, cancel_filter, nullptr, &result), DSLT_CANCELLED);

    request = {};
    request.operation = DSLT_OP_WINDOW_LEVEL;
    request.backend = DSLT_BACKEND_CUDA;
    request.window_min = 1.0F;
    request.window_max = 1.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = {};
    request.operation = DSLT_OP_THRESHOLD_3D;
    request.backend = DSLT_BACKEND_CUDA;
    request.threshold = 0.25F;
    const auto cancel = [](float progress, void*) -> std::int32_t { return progress >= 0.75F ? 1 : 0; };
    require(dslt_run_operation(handle, &request, cancel, nullptr, &result), DSLT_CANCELLED);

    request = {};
    request.operation = DSLT_OP_WINDOW_LEVEL;
    request.backend = DSLT_BACKEND_CUDA;
    request.window_min = -0.25F;
    request.window_max = 1.0F;
    (void)run_float_operation(handle, request, DSLT_BACKEND_CUDA); // Load kernels before the baseline.
    dslt_backend_info before{};
    require(dslt_get_backend_info(handle, &before));
    for (int iteration = 0; iteration < 100; ++iteration) {
        (void)run_float_operation(handle, request, DSLT_BACKEND_CUDA);
    }
    dslt_backend_info after{};
    require(dslt_get_backend_info(handle, &after));
    constexpr std::uint64_t measurement_tolerance = 1024ULL * 1024ULL;
    assert(after.device_memory_bytes + measurement_tolerance >= before.device_memory_bytes);

    dslt_destroy(handle);
}
#endif

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

    auto watershed_desc = descriptor(5, 1, 1);
    const std::vector<float> watershed_source(5, 0.0F);
    const std::vector<std::int32_t> watershed_seeds{10, -1, -1, -1, 20};
    const std::vector<std::int32_t> watershed_selected{10, 20};
    require(dslt_set_volume_f32(
        handle, &watershed_desc, watershed_source.data(), watershed_source.size()));
    require(dslt_set_label_state_i32(
        handle, watershed_seeds.data(), watershed_seeds.size() - 1,
        watershed_selected.data(), watershed_selected.size()), DSLT_INVALID_ARGUMENT);
    require(dslt_set_label_state_i32(
        handle, watershed_seeds.data(), watershed_seeds.size(),
        watershed_selected.data(), watershed_selected.size()));
    request = {};
    request.operation = DSLT_OP_WATERSHED;
    request.backend = DSLT_BACKEND_CPU;
    request.minimum_component_size = 0;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.output_kind == DSLT_OUTPUT_LABELS_INT32);
    assert(result.component_count == 2);
    assert(result.reserved == 256);
    labels.assign(result.element_count, -1);
    require(dslt_copy_labels_i32(handle, labels.data(), labels.size()));
    assert((labels == std::vector<std::int32_t>{10, 10, 10, 20, 20}));

    require(dslt_set_volume_f32(handle, &desc, objects.data(), objects.size()));

    request = {};
    request.operation = DSLT_OP_RESAMPLE_Z_AREA;
    request.backend = DSLT_BACKEND_CPU;
    request.target_spacing_z = 1.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.depth == 6);

    auto adaptive_desc = descriptor(1, 1, 3);
    const std::vector<float> adaptive_source{0.0F, 1.0F, 0.0F};
    require(dslt_set_volume_f32(
        handle, &adaptive_desc, adaptive_source.data(), adaptive_source.size()));
    request = {};
    request.operation = DSLT_OP_ADAPTIVE_THRESHOLD_2D;
    request.backend = DSLT_BACKEND_CPU;
    request.radius = 1;
    request.connectivity = 1; // Mean local kernel in the v1 common request.
    request.constant_c = 0.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    std::vector<float> adaptive(result.element_count);
    require(dslt_copy_output_f32(handle, adaptive.data(), adaptive.size()));
    assert(std::all_of(adaptive.begin(), adaptive.end(), [](float value) { return value == 0.0F; }));

    request.operation = DSLT_OP_ADAPTIVE_THRESHOLD_3D;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    require(dslt_copy_output_f32(handle, adaptive.data(), adaptive.size()));
    assert(adaptive[0] == 0.0F && adaptive[1] == 0.8F && adaptive[2] == 0.0F);

    request.connectivity = 2;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    const std::vector<float> h_minima_source{1.0F, 0.0F, 1.0F};
    require(dslt_set_volume_f32(
        handle, &adaptive_desc, h_minima_source.data(), h_minima_source.size()));
    request = {};
    request.operation = DSLT_OP_H_MINIMA;
    request.backend = DSLT_BACKEND_CPU;
    request.radius = 1; // Convergence check interval in the v1 common request.
    request.threshold = 0.5F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    std::vector<float> h_minima(result.element_count);
    require(dslt_copy_output_f32(handle, h_minima.data(), h_minima.size()));
    assert(h_minima[0] == 0.8F && h_minima[1] == 0.0F && h_minima[2] == 0.8F);

    request.radius = 0;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    auto height_desc = descriptor(1, 1, 5);
    const std::vector<float> height_source{0.0F, 0.0F, 0.25F, 0.75F, 1.0F};
    require(dslt_set_volume_f32(
        handle, &height_desc, height_source.data(), height_source.size()));
    request = {};
    request.operation = DSLT_OP_HEIGHT_MAP;
    request.backend = DSLT_BACKEND_CPU;
    request.radius = 0;        // XY smoothing radius.
    request.lanczos_order = 0; // Z filter radius.
    request.connectivity = 0;  // Gaussian kernel.
    request.slice_index = 0;   // XY smoothing passes.
    request.threshold = 0.5F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.output_kind == DSLT_OUTPUT_IMAGE_FLOAT32);
    assert(result.width == 1 && result.height == 1 && result.depth == 1);
    std::vector<float> height_result(result.element_count);
    require(dslt_copy_output_f32(handle, height_result.data(), height_result.size()));
    assert(height_result.size() == 1 && std::abs(height_result[0] - 2.5F) < 1.0e-6F);

    auto projection_desc = descriptor(1, 1, 4);
    const std::vector<float> projection_source{1.0F, 0.2F, 0.8F, 0.4F};
    require(dslt_set_volume_f32(
        handle, &projection_desc, projection_source.data(), projection_source.size()));
    request = {};
    request.operation = DSLT_OP_HEIGHT_PROJECTION;
    request.backend = DSLT_BACKEND_CPU;
    request.radius = 0;              // XY smoothing radius.
    request.lanczos_order = 0;       // Z filter radius.
    request.connectivity = 0;        // Gaussian kernel.
    request.slice_index = 0;         // XY smoothing passes.
    request.threshold = 0.5F;        // Surface threshold.
    request.target_spacing_z = 1.0F; // Z projection mode.
    request.minimum_component_size = 2; // Inclusive projection range.
    request.constant_c = 0.0F;       // Surface offset.
    request.window_min = 1.0F;       // Start depth.
    request.window_max = 0.0F;       // Projection threshold.
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.output_kind == DSLT_OUTPUT_IMAGE_FLOAT32);
    assert(result.width == 1 && result.height == 1 && result.depth == 1);
    std::vector<float> projection_result(result.element_count);
    require(dslt_copy_output_f32(handle, projection_result.data(), projection_result.size()));
    assert(projection_result.size() == 1 && std::abs(projection_result[0] - 0.8F) < 1.0e-6F);

    request.target_spacing_z = 2.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    auto depth_desc = descriptor(3, 1, 3);
    const std::vector<float> depth_source{
        1.0F, 0.0F, 1.0F,
        0.0F, 0.0F, 0.0F,
        0.0F, 0.0F, 0.0F,
    };
    require(dslt_set_volume_f32(
        handle, &depth_desc, depth_source.data(), depth_source.size()));
    request = {};
    request.operation = DSLT_OP_DEPTH_MAP;
    request.backend = DSLT_BACKEND_CPU;
    request.radius = 0;
    request.lanczos_order = 0;
    request.connectivity = 0;
    request.slice_index = 0;
    request.threshold = 0.5F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.output_kind == DSLT_OUTPUT_VOLUME_FLOAT32);
    std::vector<float> depth_result(result.element_count);
    require(dslt_copy_output_f32(handle, depth_result.data(), depth_result.size()));
    assert(depth_result.size() == depth_source.size());
    assert(std::abs(depth_result[6] - 1.0F) < 1.0e-6F);

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
#ifdef DSLT_TEST_CUDA
    require(cuda_status);
    assert(result.used_backend == DSLT_BACKEND_CUDA);
#else
    assert(cuda_status == DSLT_BACKEND_UNAVAILABLE || cuda_status == DSLT_NOT_IMPLEMENTED);
#endif

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
#ifdef DSLT_TEST_CUDA
    cuda_pointwise_parity_test();
#endif
    std::cout << "DSLT native synthetic tests passed\n";
    return 0;
}
