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

struct LabelOperationOutput final {
    std::vector<std::int32_t> values;
    dslt_operation_result result{};
};

LabelOperationOutput run_label_operation_result(
    dslt_handle handle,
    dslt_operation_request request,
    dslt_backend expected_backend) {
    LabelOperationOutput output{};
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &output.result));
    assert(output.result.used_backend == expected_backend);
    assert(output.result.output_kind == DSLT_OUTPUT_LABELS_INT32);
    output.values.resize(output.result.element_count);
    require(dslt_copy_labels_i32(handle, output.values.data(), output.values.size()));
    return output;
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

    dslt_crop_options z_gradient_surface_options{};
    z_gradient_surface_options.use_height_map = 1;
    std::vector<float> z_gradient_surface(
        static_cast<std::size_t>(filter_desc.width) * filter_desc.height, 1.25F);
    require(dslt_set_crop(
        handle, &z_gradient_surface_options,
        z_gradient_surface.data(), z_gradient_surface.size()));
    dslt_operation_request z_gradient_request{};
    z_gradient_request.operation = DSLT_OP_Z_GRADIENT;
    z_gradient_request.constant_c = 2.0F;
    z_gradient_request.threshold = 1.5F;
    z_gradient_request.window_min = -0.5F;
    z_gradient_request.window_max = 2.0F;
    z_gradient_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_z_gradient = run_float_operation(
        handle, z_gradient_request, DSLT_BACKEND_CPU);
    z_gradient_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_z_gradient = run_float_operation(
        handle, z_gradient_request, DSLT_BACKEND_CUDA);
    assert(cpu_z_gradient.size() == cuda_z_gradient.size());
    for (std::size_t index = 0; index < cpu_z_gradient.size(); ++index) {
        const auto difference = std::abs(cpu_z_gradient[index] - cuda_z_gradient[index]);
        const auto tolerance = 1.0e-5F + 1.0e-4F * std::abs(cpu_z_gradient[index]);
        assert(difference <= tolerance);
    }

    z_gradient_surface_options = {};
    require(dslt_set_crop(handle, &z_gradient_surface_options, nullptr, 0));

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

    for (const auto operation : {
             DSLT_OP_ADAPTIVE_THRESHOLD_2D,
             DSLT_OP_ADAPTIVE_THRESHOLD_3D}) {
        for (const auto kernel : {0, 1}) {
            for (const auto constant_c : {-0.04F, 0.07F}) {
                dslt_operation_request adaptive_request{};
                adaptive_request.operation = operation;
                adaptive_request.radius = 1;
                adaptive_request.connectivity = kernel;
                adaptive_request.constant_c = constant_c;
                adaptive_request.backend = DSLT_BACKEND_CPU;
                const auto cpu = run_float_operation(
                    handle, adaptive_request, DSLT_BACKEND_CPU);
                adaptive_request.backend = DSLT_BACKEND_CUDA;
                const auto cuda = run_float_operation(
                    handle, adaptive_request, DSLT_BACKEND_CUDA);
                assert(cpu == cuda);
            }
        }
    }

    dslt_operation_request adaptive_request{};
    adaptive_request.operation = DSLT_OP_ADAPTIVE_THRESHOLD_3D;
    adaptive_request.radius = 0;
    adaptive_request.connectivity = 1;
    adaptive_request.constant_c = 0.0F;
    adaptive_request.backend = DSLT_BACKEND_CUDA;
    const auto adaptive_tie = run_float_operation(
        handle, adaptive_request, DSLT_BACKEND_CUDA);
    assert(std::all_of(
        adaptive_tie.begin(), adaptive_tie.end(),
        [](float value) { return value == 0.0F; }));

    adaptive_request.radius = 1;
    adaptive_request.backend = DSLT_BACKEND_AUTO;
    (void)run_float_operation(handle, adaptive_request, DSLT_BACKEND_CUDA);

    dslt_operation_result adaptive_result{};
    adaptive_request.backend = DSLT_BACKEND_CUDA;
    adaptive_request.radius = 101;
    require(dslt_run_operation(
        handle, &adaptive_request, nullptr, nullptr, &adaptive_result), DSLT_INVALID_ARGUMENT);
    adaptive_request.radius = 1;
    adaptive_request.connectivity = 2;
    require(dslt_run_operation(
        handle, &adaptive_request, nullptr, nullptr, &adaptive_result), DSLT_INVALID_ARGUMENT);
    adaptive_request.connectivity = 1;
    adaptive_request.constant_c = std::numeric_limits<float>::quiet_NaN();
    require(dslt_run_operation(
        handle, &adaptive_request, nullptr, nullptr, &adaptive_result), DSLT_INVALID_ARGUMENT);
    adaptive_request.constant_c = 0.0F;
    const auto cancel_adaptive = [](float progress, void*) -> std::int32_t {
        return progress >= 0.55F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &adaptive_request, cancel_adaptive, nullptr, &adaptive_result), DSLT_CANCELLED);

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

    dslt_operation_request excessive_resample{};
    excessive_resample.operation = DSLT_OP_RESAMPLE_Z_AREA;
    excessive_resample.target_spacing_z = std::numeric_limits<float>::min();
    excessive_resample.backend = DSLT_BACKEND_CUDA;
    dslt_operation_result excessive_result{};
    require(dslt_run_operation(
        handle, &excessive_resample, nullptr, nullptr, &excessive_result), DSLT_RESOURCE_LIMIT);

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

    auto projection_desc = descriptor(4, 3, 5);
    std::vector<float> projection_source(projection_desc.element_count);
    for (std::size_t z = 0; z < projection_desc.depth; ++z) {
        for (std::size_t y = 0; y < projection_desc.height; ++y) {
            for (std::size_t x = 0; x < projection_desc.width; ++x) {
                const auto surface = 1.0F + static_cast<float>((x + 2 * y) % 3);
                const auto value = 0.2F + 0.35F * (static_cast<float>(z) - surface) +
                    0.03F * static_cast<float>(x);
                const auto index = z * projection_desc.width * projection_desc.height +
                    y * projection_desc.width + x;
                projection_source[index] = std::clamp(value, 0.0F, 1.0F);
            }
        }
    }
    require(dslt_set_volume_f32(
        handle, &projection_desc, projection_source.data(), projection_source.size()));

    const auto assert_float_parity = [](const auto& cpu, const auto& cuda) {
        assert(cpu.values.size() == cuda.values.size());
        for (std::size_t index = 0; index < cpu.values.size(); ++index) {
            const auto difference = std::abs(cpu.values[index] - cuda.values[index]);
            assert(difference <= 1.0e-5F ||
                difference <= std::abs(cpu.values[index]) * 1.0e-4F);
        }
    };

    for (const auto kernel_type : {0, 1}) {
        dslt_operation_request height_request{};
        height_request.operation = DSLT_OP_HEIGHT_MAP;
        height_request.radius = 1;
        height_request.lanczos_order = 1;
        height_request.connectivity = kernel_type;
        height_request.slice_index = 2;
        height_request.threshold = 0.5F;
        height_request.backend = DSLT_BACKEND_CPU;
        const auto cpu = run_float_operation_result(
            handle, height_request, DSLT_BACKEND_CPU, DSLT_OUTPUT_IMAGE_FLOAT32);
        height_request.backend = DSLT_BACKEND_CUDA;
        const auto cuda = run_float_operation_result(
            handle, height_request, DSLT_BACKEND_CUDA, DSLT_OUTPUT_IMAGE_FLOAT32);
        assert(cuda.result.width == projection_desc.width);
        assert(cuda.result.height == projection_desc.height);
        assert(cuda.result.depth == 1);
        assert_float_parity(cpu, cuda);
    }

    dslt_operation_request depth_request{};
    depth_request.operation = DSLT_OP_DEPTH_MAP;
    depth_request.radius = 1;
    depth_request.lanczos_order = 1;
    depth_request.connectivity = 0;
    depth_request.slice_index = 1;
    depth_request.threshold = 0.5F;
    depth_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_depth = run_float_operation_result(
        handle, depth_request, DSLT_BACKEND_CPU);
    depth_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_depth = run_float_operation_result(
        handle, depth_request, DSLT_BACKEND_CUDA);
    assert(cuda_depth.result.width == projection_desc.width);
    assert(cuda_depth.result.height == projection_desc.height);
    assert(cuda_depth.result.depth == projection_desc.depth);
    assert_float_parity(cpu_depth, cuda_depth);

    for (const auto mode : {0, 1}) {
        dslt_operation_request projection_request{};
        projection_request.operation = DSLT_OP_HEIGHT_PROJECTION;
        projection_request.radius = 1;
        projection_request.lanczos_order = 1;
        projection_request.connectivity = 0;
        projection_request.slice_index = 1;
        projection_request.threshold = 0.5F;
        projection_request.target_spacing_z = static_cast<float>(mode);
        projection_request.minimum_component_size = 2;
        projection_request.constant_c = 0.1F;
        projection_request.window_min = 0.0F;
        projection_request.window_max = 0.0F;
        projection_request.backend = DSLT_BACKEND_CPU;
        const auto cpu = run_float_operation_result(
            handle, projection_request, DSLT_BACKEND_CPU, DSLT_OUTPUT_IMAGE_FLOAT32);
        projection_request.backend = DSLT_BACKEND_CUDA;
        const auto cuda = run_float_operation_result(
            handle, projection_request, DSLT_BACKEND_CUDA, DSLT_OUTPUT_IMAGE_FLOAT32);
        assert(cuda.result.width == projection_desc.width);
        assert(cuda.result.height == projection_desc.height);
        assert(cuda.result.depth == 1);
        assert_float_parity(cpu, cuda);
    }

    dslt_crop_options provided_surface_options{};
    provided_surface_options.use_height_map = 1;
    const std::vector<float> provided_surface(
        static_cast<std::size_t>(projection_desc.width) * projection_desc.height, 2.0F);
    require(dslt_set_crop(
        handle, &provided_surface_options, provided_surface.data(), provided_surface.size()));
    depth_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_provided_depth = run_float_operation_result(
        handle, depth_request, DSLT_BACKEND_CPU);
    depth_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_provided_depth = run_float_operation_result(
        handle, depth_request, DSLT_BACKEND_CUDA);
    assert_float_parity(cpu_provided_depth, cuda_provided_depth);

    dslt_operation_request provided_projection_request{};
    provided_projection_request.operation = DSLT_OP_HEIGHT_PROJECTION;
    provided_projection_request.target_spacing_z = 1.0F;
    provided_projection_request.minimum_component_size = 1;
    provided_projection_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_provided_projection = run_float_operation_result(
        handle, provided_projection_request, DSLT_BACKEND_CPU, DSLT_OUTPUT_IMAGE_FLOAT32);
    provided_projection_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_provided_projection = run_float_operation_result(
        handle, provided_projection_request, DSLT_BACKEND_CUDA, DSLT_OUTPUT_IMAGE_FLOAT32);
    assert_float_parity(cpu_provided_projection, cuda_provided_projection);

    provided_surface_options = {};
    require(dslt_set_crop(handle, &provided_surface_options, nullptr, 0));

    request = {};
    request.operation = DSLT_OP_HEIGHT_MAP;
    request.radius = 1;
    request.lanczos_order = 1;
    request.connectivity = 1;
    request.slice_index = 1;
    request.threshold = 0.5F;
    request.backend = DSLT_BACKEND_AUTO;
    const auto automatic_height = run_float_operation_result(
        handle, request, DSLT_BACKEND_CUDA, DSLT_OUTPUT_IMAGE_FLOAT32);
    assert(automatic_height.result.width == projection_desc.width);
    assert(automatic_height.result.height == projection_desc.height);

    auto components_desc = descriptor(4, 4, 3);
    std::vector<float> components_source(components_desc.element_count, 0.0F);
    const auto component_index = [&](std::size_t x, std::size_t y, std::size_t z) {
        return z * components_desc.width * components_desc.height +
            y * components_desc.width + x;
    };
    for (const auto index : {
             component_index(0, 0, 0), component_index(1, 0, 0),
             component_index(2, 1, 0), component_index(3, 2, 1),
             component_index(0, 3, 1), component_index(0, 3, 2)}) {
        components_source[index] = 1.0F;
    }
    require(dslt_set_volume_f32(
        handle, &components_desc, components_source.data(), components_source.size()));

    for (const auto connectivity : {6, 18, 26}) {
        for (const auto minimum_size : {1, 2}) {
            dslt_operation_request component_request{};
            component_request.operation = DSLT_OP_CONNECTED_COMPONENTS;
            component_request.threshold = 0.5F;
            component_request.connectivity = connectivity;
            component_request.minimum_component_size = minimum_size;
            component_request.backend = DSLT_BACKEND_CPU;
            const auto cpu = run_label_operation_result(
                handle, component_request, DSLT_BACKEND_CPU);
            component_request.backend = DSLT_BACKEND_CUDA;
            const auto cuda = run_label_operation_result(
                handle, component_request, DSLT_BACKEND_CUDA);
            assert(cuda.result.width == components_desc.width);
            assert(cuda.result.height == components_desc.height);
            assert(cuda.result.depth == components_desc.depth);
            assert(cpu.result.component_count == cuda.result.component_count);
            assert(cpu.values == cuda.values);
        }
    }

    dslt_operation_request nan_component_request{};
    nan_component_request.operation = DSLT_OP_CONNECTED_COMPONENTS;
    nan_component_request.threshold = std::numeric_limits<float>::quiet_NaN();
    nan_component_request.connectivity = 26;
    nan_component_request.minimum_component_size = 1;
    nan_component_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_nan_components = run_label_operation_result(
        handle, nan_component_request, DSLT_BACKEND_CPU);
    nan_component_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_nan_components = run_label_operation_result(
        handle, nan_component_request, DSLT_BACKEND_CUDA);
    assert(cpu_nan_components.result.component_count == components_source.size());
    assert(cpu_nan_components.result.component_count == cuda_nan_components.result.component_count);
    assert(cpu_nan_components.values == cuda_nan_components.values);

    request = {};
    request.operation = DSLT_OP_CONNECTED_COMPONENTS;
    request.threshold = 0.5F;
    request.connectivity = 18;
    request.minimum_component_size = 1;
    request.backend = DSLT_BACKEND_AUTO;
    const auto automatic_components = run_label_operation_result(
        handle, request, DSLT_BACKEND_CUDA);
    assert(automatic_components.result.component_count > 0);

    auto h_minima_desc = descriptor(3, 3, 3);
    std::vector<float> h_minima_source(h_minima_desc.element_count, 1.0F);
    const auto h_minima_center = static_cast<std::size_t>(13);
    h_minima_source[h_minima_center] = 0.0F;
    require(dslt_set_volume_f32(
        handle, &h_minima_desc, h_minima_source.data(), h_minima_source.size()));

    for (const auto check_interval : {1, 50}) {
        dslt_operation_request h_minima_request{};
        h_minima_request.operation = DSLT_OP_H_MINIMA;
        h_minima_request.threshold = 0.5F;
        h_minima_request.radius = check_interval;
        h_minima_request.backend = DSLT_BACKEND_CPU;
        const auto cpu = run_float_operation_result(
            handle, h_minima_request, DSLT_BACKEND_CPU);
        h_minima_request.backend = DSLT_BACKEND_CUDA;
        const auto cuda = run_float_operation_result(
            handle, h_minima_request, DSLT_BACKEND_CUDA);
        assert(cpu.values == cuda.values);
        assert(cuda.values[h_minima_center] == 0.0F);
        assert(std::count(cuda.values.begin(), cuda.values.end(), 0.8F) == 26);
    }

    dslt_operation_request zero_h_minima_request{};
    zero_h_minima_request.operation = DSLT_OP_H_MINIMA;
    zero_h_minima_request.threshold = 0.0F;
    zero_h_minima_request.radius = 1;
    zero_h_minima_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_zero_h_minima = run_float_operation_result(
        handle, zero_h_minima_request, DSLT_BACKEND_CPU);
    zero_h_minima_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_zero_h_minima = run_float_operation_result(
        handle, zero_h_minima_request, DSLT_BACKEND_CUDA);
    assert(cpu_zero_h_minima.values == cuda_zero_h_minima.values);

    auto shallow_h_minima_source = h_minima_source;
    shallow_h_minima_source[h_minima_center] = 0.8F;
    require(dslt_set_volume_f32(
        handle, &h_minima_desc,
        shallow_h_minima_source.data(), shallow_h_minima_source.size()));
    dslt_operation_request shallow_h_minima_request{};
    shallow_h_minima_request.operation = DSLT_OP_H_MINIMA;
    shallow_h_minima_request.threshold = 0.3F;
    shallow_h_minima_request.radius = 1;
    shallow_h_minima_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_shallow_h_minima = run_float_operation_result(
        handle, shallow_h_minima_request, DSLT_BACKEND_CPU);
    shallow_h_minima_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_shallow_h_minima = run_float_operation_result(
        handle, shallow_h_minima_request, DSLT_BACKEND_CUDA);
    assert(cpu_shallow_h_minima.values == cuda_shallow_h_minima.values);
    assert(std::all_of(
        cuda_shallow_h_minima.values.begin(), cuda_shallow_h_minima.values.end(),
        [](float value) { return value == 0.0F; }));
    require(dslt_set_volume_f32(
        handle, &h_minima_desc, h_minima_source.data(), h_minima_source.size()));

    request = {};
    request.operation = DSLT_OP_H_MINIMA;
    request.threshold = 0.5F;
    request.radius = 50;
    request.backend = DSLT_BACKEND_AUTO;
    const auto automatic_h_minima = run_float_operation_result(
        handle, request, DSLT_BACKEND_CUDA);
    assert(automatic_h_minima.values[h_minima_center] == 0.0F);

    auto dslt_cuda_desc = descriptor(4, 3, 3);
    std::vector<float> dslt_cuda_source(dslt_cuda_desc.element_count);
    for (std::size_t index = 0; index < dslt_cuda_source.size(); ++index) {
        dslt_cuda_source[index] = static_cast<float>((index * 17U + 3U) % 31U) / 30.0F;
    }
    dslt_cuda_source[0] = 1.5F;
    dslt_cuda_source[dslt_cuda_source.size() / 2] = -0.5F;
    require(dslt_set_volume_f32(
        handle, &dslt_cuda_desc, dslt_cuda_source.data(), dslt_cuda_source.size()));

    std::vector<dslt_operation_request> dslt_threshold_requests(3);
    for (auto& dslt_request : dslt_threshold_requests) {
        dslt_request.operation = DSLT_OP_DSLT_THRESHOLD;
    }
    dslt_threshold_requests[0].radius = 1;
    dslt_threshold_requests[0].lanczos_order = 1;
    dslt_threshold_requests[0].connectivity = 1;
    dslt_threshold_requests[0].constant_c = 0.03F;
    dslt_threshold_requests[0].target_spacing_z = 0.2F;
    dslt_threshold_requests[1].radius = 2;
    dslt_threshold_requests[1].lanczos_order = 1;
    dslt_threshold_requests[1].connectivity = 0;
    dslt_threshold_requests[1].constant_c = -0.03F;
    dslt_threshold_requests[1].target_spacing_z = 0.5F;
    dslt_threshold_requests[2].radius = 1;
    dslt_threshold_requests[2].lanczos_order = 2;
    dslt_threshold_requests[2].connectivity = 1;
    dslt_threshold_requests[2].constant_c = 0.02F;
    dslt_threshold_requests[2].target_spacing_z = 1.7F;

    auto saw_dslt_lower = false;
    auto saw_dslt_upper = false;
    for (auto dslt_request : dslt_threshold_requests) {
        dslt_request.backend = DSLT_BACKEND_CPU;
        const auto cpu = run_float_operation_result(
            handle, dslt_request, DSLT_BACKEND_CPU);
        dslt_request.backend = DSLT_BACKEND_CUDA;
        const auto cuda = run_float_operation_result(
            handle, dslt_request, DSLT_BACKEND_CUDA);
        assert(cpu.values == cuda.values);
        saw_dslt_lower = saw_dslt_lower ||
            std::find(cuda.values.begin(), cuda.values.end(), 0.0F) != cuda.values.end();
        saw_dslt_upper = saw_dslt_upper ||
            std::find(cuda.values.begin(), cuda.values.end(), 0.8F) != cuda.values.end();
    }
    assert(saw_dslt_lower && saw_dslt_upper);

    auto automatic_dslt_request = dslt_threshold_requests[0];
    automatic_dslt_request.backend = DSLT_BACKEND_AUTO;
    (void)run_float_operation_result(
        handle, automatic_dslt_request, DSLT_BACKEND_CUDA);

    dslt_operation_request dslt_segmentation_request{};
    dslt_segmentation_request.operation = DSLT_OP_DSLT_SEGMENTATION;
    dslt_segmentation_request.radius = 1;
    dslt_segmentation_request.lanczos_order = 1;
    dslt_segmentation_request.connectivity = 1;
    dslt_segmentation_request.minimum_component_size = 0;
    dslt_segmentation_request.slice_index = 0;
    dslt_segmentation_request.threshold = 0.0F;
    dslt_segmentation_request.constant_c = -0.03F;
    dslt_segmentation_request.window_min = 0.03F;
    dslt_segmentation_request.window_max = 0.03F;
    dslt_segmentation_request.target_spacing_z = 0.5F;
    dslt_segmentation_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_dslt_segmentation = run_label_operation_result(
        handle, dslt_segmentation_request, DSLT_BACKEND_CPU);
    dslt_segmentation_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_dslt_segmentation = run_label_operation_result(
        handle, dslt_segmentation_request, DSLT_BACKEND_CUDA);
    assert(cpu_dslt_segmentation.values == cuda_dslt_segmentation.values);
    assert(cpu_dslt_segmentation.result.component_count ==
        cuda_dslt_segmentation.result.component_count);
    assert(cpu_dslt_segmentation.result.reserved ==
        cuda_dslt_segmentation.result.reserved);
    assert(cuda_dslt_segmentation.result.reserved > 0);

    auto closing_dslt_segmentation_request = dslt_segmentation_request;
    closing_dslt_segmentation_request.slice_index = 1;
    closing_dslt_segmentation_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_closing_dslt_segmentation = run_label_operation_result(
        handle, closing_dslt_segmentation_request, DSLT_BACKEND_CPU);
    closing_dslt_segmentation_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_closing_dslt_segmentation = run_label_operation_result(
        handle, closing_dslt_segmentation_request, DSLT_BACKEND_CUDA);
    assert(cpu_closing_dslt_segmentation.values == cuda_closing_dslt_segmentation.values);
    assert(cpu_closing_dslt_segmentation.result.component_count ==
        cuda_closing_dslt_segmentation.result.component_count);
    assert(cpu_closing_dslt_segmentation.result.reserved ==
        cuda_closing_dslt_segmentation.result.reserved);

    auto automatic_dslt_segmentation_request = dslt_segmentation_request;
    automatic_dslt_segmentation_request.backend = DSLT_BACKEND_AUTO;
    const auto automatic_dslt_segmentation = run_label_operation_result(
        handle, automatic_dslt_segmentation_request, DSLT_BACKEND_CUDA);
    assert(automatic_dslt_segmentation.values == cuda_dslt_segmentation.values);

    auto threshold_sweep_desc = descriptor(9, 5, 1);
    std::vector<float> threshold_sweep_source(threshold_sweep_desc.element_count, 1.0F);
    const auto threshold_sweep_index = [&threshold_sweep_desc](std::size_t x, std::size_t y) {
        return y * threshold_sweep_desc.width + x;
    };
    threshold_sweep_source[threshold_sweep_index(0, 2)] = 0.0F;
    for (std::size_t y = 1; y <= 3; ++y) {
        for (std::size_t x = 4; x <= 6; ++x) {
            if (x != 5 || y != 2) threshold_sweep_source[threshold_sweep_index(x, y)] = 0.0F;
        }
    }
    require(dslt_set_volume_f32(
        handle, &threshold_sweep_desc,
        threshold_sweep_source.data(), threshold_sweep_source.size()));

    auto staged_dslt_segmentation_request = dslt_segmentation_request;
    staged_dslt_segmentation_request.constant_c = 0.0F;
    staged_dslt_segmentation_request.window_min = 0.1F;
    staged_dslt_segmentation_request.window_max = 0.1F;
    staged_dslt_segmentation_request.target_spacing_z = 0.2F;
    staged_dslt_segmentation_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_staged_dslt_segmentation = run_label_operation_result(
        handle, staged_dslt_segmentation_request, DSLT_BACKEND_CPU);
    staged_dslt_segmentation_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_staged_dslt_segmentation = run_label_operation_result(
        handle, staged_dslt_segmentation_request, DSLT_BACKEND_CUDA);
    assert(cpu_staged_dslt_segmentation.values == cuda_staged_dslt_segmentation.values);
    assert(cuda_staged_dslt_segmentation.result.component_count == 2);
    assert(cuda_staged_dslt_segmentation.result.reserved == 2);
    assert(cuda_staged_dslt_segmentation.values[threshold_sweep_index(0, 2)] >= 0);
    assert(cuda_staged_dslt_segmentation.values[threshold_sweep_index(5, 2)] == -1);

    dslt_operation_request threshold_sweep_request{};
    threshold_sweep_request.operation = DSLT_OP_THRESHOLD_SWEEP;
    threshold_sweep_request.constant_c = 0.4F;
    threshold_sweep_request.window_min = 0.8F;
    threshold_sweep_request.window_max = 0.4F;
    threshold_sweep_request.slice_index = 0;
    threshold_sweep_request.minimum_component_size = 0;
    threshold_sweep_request.threshold = 0.0F;
    threshold_sweep_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_threshold_sweep = run_label_operation_result(
        handle, threshold_sweep_request, DSLT_BACKEND_CPU);
    threshold_sweep_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_threshold_sweep = run_label_operation_result(
        handle, threshold_sweep_request, DSLT_BACKEND_CUDA);
    assert(cpu_threshold_sweep.values == cuda_threshold_sweep.values);
    assert(cuda_threshold_sweep.result.component_count == 2);
    assert(cuda_threshold_sweep.result.reserved == 2);
    assert(cuda_threshold_sweep.values[threshold_sweep_index(0, 2)] == 0);
    assert(cuda_threshold_sweep.values[threshold_sweep_index(5, 2)] == -1);

    auto closing_sweep_request = threshold_sweep_request;
    closing_sweep_request.slice_index = 1;
    closing_sweep_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_closing_sweep = run_label_operation_result(
        handle, closing_sweep_request, DSLT_BACKEND_CPU);
    closing_sweep_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_closing_sweep = run_label_operation_result(
        handle, closing_sweep_request, DSLT_BACKEND_CUDA);
    assert(cpu_closing_sweep.values == cuda_closing_sweep.values);
    assert(cpu_closing_sweep.result.component_count ==
        cuda_closing_sweep.result.component_count);
    assert(cpu_closing_sweep.result.reserved == cuda_closing_sweep.result.reserved);

    auto automatic_sweep_request = threshold_sweep_request;
    automatic_sweep_request.backend = DSLT_BACKEND_AUTO;
    const auto automatic_threshold_sweep = run_label_operation_result(
        handle, automatic_sweep_request, DSLT_BACKEND_CUDA);
    assert(automatic_threshold_sweep.values == cuda_threshold_sweep.values);

    auto crop_sweep_desc = descriptor(9, 5, 3);
    std::vector<float> crop_sweep_source(crop_sweep_desc.element_count, 1.0F);
    for (std::size_t z = 0; z < crop_sweep_desc.depth; ++z) {
        std::copy(
            threshold_sweep_source.begin(), threshold_sweep_source.end(),
            crop_sweep_source.begin() + static_cast<std::ptrdiff_t>(
                z * threshold_sweep_source.size()));
    }
    require(dslt_set_volume_f32(
        handle, &crop_sweep_desc, crop_sweep_source.data(), crop_sweep_source.size()));
    dslt_crop_options sweep_crop{};
    sweep_crop.enabled = 1;
    sweep_crop.upper = 1;
    sweep_crop.lower = 1;
    require(dslt_set_crop(handle, &sweep_crop, nullptr, 0));
    threshold_sweep_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_fixed_crop_sweep = run_label_operation_result(
        handle, threshold_sweep_request, DSLT_BACKEND_CPU);
    threshold_sweep_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_fixed_crop_sweep = run_label_operation_result(
        handle, threshold_sweep_request, DSLT_BACKEND_CUDA);
    assert(cpu_fixed_crop_sweep.values == cuda_fixed_crop_sweep.values);

    require(dslt_set_volume_f32(
        handle, &crop_sweep_desc, crop_sweep_source.data(), crop_sweep_source.size()));
    sweep_crop.use_height_map = 1;
    sweep_crop.upper = 0;
    sweep_crop.lower = 0;
    const std::vector<float> sweep_height_map(
        static_cast<std::size_t>(crop_sweep_desc.width) * crop_sweep_desc.height, 1.0F);
    require(dslt_set_crop(
        handle, &sweep_crop, sweep_height_map.data(), sweep_height_map.size()));
    threshold_sweep_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_height_crop_sweep = run_label_operation_result(
        handle, threshold_sweep_request, DSLT_BACKEND_CPU);
    threshold_sweep_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_height_crop_sweep = run_label_operation_result(
        handle, threshold_sweep_request, DSLT_BACKEND_CUDA);
    assert(cpu_height_crop_sweep.values == cuda_height_crop_sweep.values);

    staged_dslt_segmentation_request.backend = DSLT_BACKEND_CPU;
    const auto cpu_height_crop_dslt_segmentation = run_label_operation_result(
        handle, staged_dslt_segmentation_request, DSLT_BACKEND_CPU);
    staged_dslt_segmentation_request.backend = DSLT_BACKEND_CUDA;
    const auto cuda_height_crop_dslt_segmentation = run_label_operation_result(
        handle, staged_dslt_segmentation_request, DSLT_BACKEND_CUDA);
    assert(cpu_height_crop_dslt_segmentation.values ==
        cuda_height_crop_dslt_segmentation.values);
    assert(cpu_height_crop_dslt_segmentation.result.component_count ==
        cuda_height_crop_dslt_segmentation.result.component_count);
    assert(cpu_height_crop_dslt_segmentation.result.reserved ==
        cuda_height_crop_dslt_segmentation.result.reserved);

    auto watershed_desc = descriptor(5, 1, 1);
    const std::vector<float> watershed_source(watershed_desc.element_count, 0.0F);
    const std::vector<std::int32_t> watershed_seeds{10, -1, -1, -1, 20};
    const std::vector<std::int32_t> watershed_selected{10, 20};
    require(dslt_set_volume_f32(
        handle, &watershed_desc, watershed_source.data(), watershed_source.size()));
    const auto run_watershed_backend = [&handle](
        const std::vector<std::int32_t>& seeds,
        const std::vector<std::int32_t>& selected,
        dslt_operation_request watershed_request,
        dslt_backend backend) {
        require(dslt_set_label_state_i32(
            handle, seeds.data(), seeds.size(), selected.data(), selected.size()));
        watershed_request.backend = backend;
        return run_label_operation_result(handle, watershed_request, backend);
    };

    dslt_operation_request watershed_request{};
    watershed_request.operation = DSLT_OP_WATERSHED;
    watershed_request.minimum_component_size = 0;
    const auto cpu_watershed = run_watershed_backend(
        watershed_seeds, watershed_selected, watershed_request, DSLT_BACKEND_CPU);
    const auto cuda_watershed = run_watershed_backend(
        watershed_seeds, watershed_selected, watershed_request, DSLT_BACKEND_CUDA);
    assert(cpu_watershed.result.component_count == 2);
    assert(cpu_watershed.result.component_count == cuda_watershed.result.component_count);
    assert(cuda_watershed.result.reserved == 256);
    assert(cpu_watershed.values == cuda_watershed.values);
    assert((cuda_watershed.values == std::vector<std::int32_t>{10, 10, 10, 20, 20}));

    const std::vector<std::int32_t> single_selected{20};
    const auto cpu_single_watershed = run_watershed_backend(
        watershed_seeds, single_selected, watershed_request, DSLT_BACKEND_CPU);
    const auto cuda_single_watershed = run_watershed_backend(
        watershed_seeds, single_selected, watershed_request, DSLT_BACKEND_CUDA);
    assert(cpu_single_watershed.values == cuda_single_watershed.values);
    assert(cuda_single_watershed.result.component_count == 1);

    const std::vector<std::int32_t> sized_watershed_seeds{10, 10, -1, -1, 20};
    watershed_request.minimum_component_size = 2;
    const auto cpu_sized_watershed = run_watershed_backend(
        sized_watershed_seeds, watershed_selected, watershed_request, DSLT_BACKEND_CPU);
    const auto cuda_sized_watershed = run_watershed_backend(
        sized_watershed_seeds, watershed_selected, watershed_request, DSLT_BACKEND_CUDA);
    assert(cpu_sized_watershed.values == cuda_sized_watershed.values);
    assert(cuda_sized_watershed.result.component_count == 1);

    watershed_request.minimum_component_size = 0;
    require(dslt_set_label_state_i32(
        handle, watershed_seeds.data(), watershed_seeds.size(),
        watershed_selected.data(), watershed_selected.size()));
    watershed_request.backend = DSLT_BACKEND_AUTO;
    const auto automatic_watershed = run_label_operation_result(
        handle, watershed_request, DSLT_BACKEND_CUDA);
    assert(automatic_watershed.result.used_backend == DSLT_BACKEND_CUDA);
    assert(automatic_watershed.values == cuda_watershed.values);

    auto crop_watershed_desc = descriptor(5, 1, 3);
    const std::vector<float> crop_watershed_source(crop_watershed_desc.element_count, 0.0F);
    std::vector<std::int32_t> crop_watershed_seeds(crop_watershed_desc.element_count, -1);
    crop_watershed_seeds[5] = 10;
    crop_watershed_seeds[9] = 20;
    require(dslt_set_volume_f32(
        handle, &crop_watershed_desc,
        crop_watershed_source.data(), crop_watershed_source.size()));
    dslt_crop_options crop_options{};
    crop_options.enabled = 1;
    crop_options.upper = 1;
    crop_options.lower = 1;
    require(dslt_set_crop(handle, &crop_options, nullptr, 0));
    const auto cpu_crop_watershed = run_watershed_backend(
        crop_watershed_seeds, watershed_selected, watershed_request, DSLT_BACKEND_CPU);
    require(dslt_set_volume_f32(
        handle, &crop_watershed_desc,
        crop_watershed_source.data(), crop_watershed_source.size()));
    require(dslt_set_crop(handle, &crop_options, nullptr, 0));
    const auto cuda_crop_watershed = run_watershed_backend(
        crop_watershed_seeds, watershed_selected, watershed_request, DSLT_BACKEND_CUDA);
    assert(cpu_crop_watershed.values == cuda_crop_watershed.values);
    assert(cpu_crop_watershed.result.component_count == cuda_crop_watershed.result.component_count);

    require(dslt_set_volume_f32(
        handle, &watershed_desc, watershed_source.data(), watershed_source.size()));

    dslt_operation_result result{};
    request = {};
    request.operation = DSLT_OP_WATERSHED;
    request.backend = DSLT_BACKEND_CUDA;
    request.minimum_component_size = 0;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    require(dslt_set_label_state_i32(
        handle, watershed_seeds.data(), watershed_seeds.size(),
        watershed_selected.data(), watershed_selected.size()));
    request.minimum_component_size = -1;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    require(dslt_set_label_state_i32(
        handle, watershed_seeds.data(), watershed_seeds.size(),
        watershed_selected.data(), watershed_selected.size()));
    request.minimum_component_size = 2;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    require(dslt_set_volume_f32(
        handle, &dslt_cuda_desc, dslt_cuda_source.data(), dslt_cuda_source.size()));
    request = dslt_threshold_requests[0];
    request.backend = DSLT_BACKEND_CUDA;
    request.radius = 0;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request.radius = 128;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = dslt_threshold_requests[0];
    request.backend = DSLT_BACKEND_CUDA;
    request.lanczos_order = 0;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request.lanczos_order = 6;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = dslt_threshold_requests[0];
    request.backend = DSLT_BACKEND_CUDA;
    request.connectivity = 2;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = dslt_threshold_requests[0];
    request.backend = DSLT_BACKEND_CUDA;
    request.constant_c = std::numeric_limits<float>::quiet_NaN();
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = dslt_threshold_requests[0];
    request.backend = DSLT_BACKEND_CUDA;
    request.target_spacing_z = -0.1F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    auto dslt_limit_desc = descriptor(50, 50, 50);
    const std::vector<float> dslt_limit_source(dslt_limit_desc.element_count, 0.5F);
    require(dslt_set_volume_f32(
        handle, &dslt_limit_desc, dslt_limit_source.data(), dslt_limit_source.size()));
    request = dslt_threshold_requests[0];
    request.backend = DSLT_BACKEND_CUDA;
    request.radius = 127;
    request.lanczos_order = 5;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_RESOURCE_LIMIT);

    require(dslt_set_volume_f32(
        handle, &threshold_sweep_desc,
        threshold_sweep_source.data(), threshold_sweep_source.size()));
    request = threshold_sweep_request;
    request.backend = DSLT_BACKEND_CUDA;
    request.constant_c = 0.9F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = threshold_sweep_request;
    request.backend = DSLT_BACKEND_CUDA;
    request.window_max = 0.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = threshold_sweep_request;
    request.backend = DSLT_BACKEND_CUDA;
    request.slice_index = 65;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = threshold_sweep_request;
    request.backend = DSLT_BACKEND_CUDA;
    request.minimum_component_size = -1;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = threshold_sweep_request;
    request.backend = DSLT_BACKEND_CUDA;
    request.threshold = 0.5F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request.threshold = static_cast<float>(std::numeric_limits<int>::max());
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

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
    request.slice_index = static_cast<std::int32_t>(threshold_sweep_desc.width);
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = {};
    request.operation = DSLT_OP_HEIGHT_MAP;
    request.backend = DSLT_BACKEND_CUDA;
    request.radius = 65;
    request.lanczos_order = 0;
    request.connectivity = 0;
    request.slice_index = 0;
    request.threshold = 0.5F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request.radius = 0;
    request.connectivity = 2;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request.connectivity = 0;
    request.slice_index = 11;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request.slice_index = 0;
    request.threshold = std::numeric_limits<float>::quiet_NaN();
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = {};
    request.operation = DSLT_OP_HEIGHT_PROJECTION;
    request.backend = DSLT_BACKEND_CUDA;
    request.connectivity = 0;
    request.target_spacing_z = 2.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request.target_spacing_z = 0.0F;
    request.minimum_component_size = -1;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request.minimum_component_size = 0;
    request.window_min = std::numeric_limits<float>::infinity();
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = {};
    request.operation = DSLT_OP_CONNECTED_COMPONENTS;
    request.backend = DSLT_BACKEND_CUDA;
    request.threshold = 0.5F;
    request.connectivity = 12;
    request.minimum_component_size = 1;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request.connectivity = 6;
    request.minimum_component_size = 0;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = {};
    request.operation = DSLT_OP_H_MINIMA;
    request.backend = DSLT_BACKEND_CUDA;
    request.threshold = -0.1F;
    request.radius = 1;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request.threshold = 0.5F;
    request.radius = 0;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    auto non_finite_h_minima_source = h_minima_source;
    non_finite_h_minima_source[h_minima_center] = std::numeric_limits<float>::infinity();
    require(dslt_set_volume_f32(
        handle, &h_minima_desc,
        non_finite_h_minima_source.data(), non_finite_h_minima_source.size()));
    request.radius = 1;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    require(dslt_set_volume_f32(
        handle, &h_minima_desc, h_minima_source.data(), h_minima_source.size()));

    request = {};
    request.operation = DSLT_OP_RESAMPLE_Z_AREA;
    request.backend = DSLT_BACKEND_CUDA;
    request.target_spacing_z = 0.5F;
    const auto cancel_resample = [](float progress, void*) -> std::int32_t {
        return progress >= 0.4F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &request, cancel_resample, nullptr, &result), DSLT_CANCELLED);

    request = depth_request;
    request.backend = DSLT_BACKEND_CUDA;
    const auto cancel_depth = [](float progress, void*) -> std::int32_t {
        return progress >= 0.6F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &request, cancel_depth, nullptr, &result), DSLT_CANCELLED);

    request = {};
    request.operation = DSLT_OP_CONNECTED_COMPONENTS;
    request.backend = DSLT_BACKEND_CUDA;
    request.threshold = 0.5F;
    request.connectivity = 26;
    request.minimum_component_size = 1;
    const auto cancel_components = [](float progress, void*) -> std::int32_t {
        return progress >= 0.31F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &request, cancel_components, nullptr, &result), DSLT_CANCELLED);

    request = {};
    request.operation = DSLT_OP_H_MINIMA;
    request.backend = DSLT_BACKEND_CUDA;
    request.threshold = 0.5F;
    request.radius = 50;
    const auto cancel_h_minima = [](float progress, void*) -> std::int32_t {
        return progress > 0.30F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &request, cancel_h_minima, nullptr, &result), DSLT_CANCELLED);

    require(dslt_set_volume_f32(
        handle, &threshold_sweep_desc,
        threshold_sweep_source.data(), threshold_sweep_source.size()));
    request = threshold_sweep_request;
    request.backend = DSLT_BACKEND_CUDA;
    const auto cancel_threshold_sweep = [](float progress, void*) -> std::int32_t {
        return progress > 0.25F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &request, cancel_threshold_sweep, nullptr, &result), DSLT_CANCELLED);

    require(dslt_set_volume_f32(
        handle, &dslt_cuda_desc, dslt_cuda_source.data(), dslt_cuda_source.size()));
    request = dslt_segmentation_request;
    request.backend = DSLT_BACKEND_CUDA;
    const auto cancel_dslt_segmentation = [](float progress, void*) -> std::int32_t {
        return progress > 0.25F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &request, cancel_dslt_segmentation, nullptr, &result), DSLT_CANCELLED);

    request = dslt_threshold_requests[2];
    request.backend = DSLT_BACKEND_CUDA;
    const auto cancel_dslt_threshold = [](float progress, void*) -> std::int32_t {
        return progress > 0.25F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &request, cancel_dslt_threshold, nullptr, &result), DSLT_CANCELLED);

    require(dslt_set_volume_f32(
        handle, &watershed_desc, watershed_source.data(), watershed_source.size()));
    require(dslt_set_label_state_i32(
        handle, watershed_seeds.data(), watershed_seeds.size(),
        watershed_selected.data(), watershed_selected.size()));
    request = {};
    request.operation = DSLT_OP_WATERSHED;
    request.backend = DSLT_BACKEND_CUDA;
    request.minimum_component_size = 0;
    const auto cancel_watershed = [](float progress, void*) -> std::int32_t {
        return progress > 0.25F ? 1 : 0;
    };
    require(dslt_run_operation(
        handle, &request, cancel_watershed, nullptr, &result), DSLT_CANCELLED);

    request = {};
    request.operation = DSLT_OP_DILATE_SPHERE;
    request.backend = DSLT_BACKEND_CUDA;
    request.radius = 65;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    request = dslt_segmentation_request;
    request.backend = DSLT_BACKEND_CUDA;
    request.window_min = request.constant_c - 0.1F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = dslt_segmentation_request;
    request.backend = DSLT_BACKEND_CUDA;
    request.window_max = 0.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = dslt_segmentation_request;
    request.backend = DSLT_BACKEND_CUDA;
    request.slice_index = 65;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = dslt_segmentation_request;
    request.backend = DSLT_BACKEND_CUDA;
    request.minimum_component_size = -1;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);
    request = dslt_segmentation_request;
    request.backend = DSLT_BACKEND_CUDA;
    request.threshold = 0.5F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

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

    // Keep the direct auxiliary-surface path active through the repeated
    // DepthMap/HeightProjection requests so its request-owned CUDA buffer is
    // covered by the memory-drift gate below.
    require(dslt_set_crop(
        handle, &provided_surface_options, provided_surface.data(), provided_surface.size()));

    auto memory_height = depth_request;
    memory_height.operation = DSLT_OP_HEIGHT_MAP;
    memory_height.backend = DSLT_BACKEND_CUDA;
    (void)run_float_operation_result(
        handle, memory_height, DSLT_BACKEND_CUDA, DSLT_OUTPUT_IMAGE_FLOAT32);
    auto memory_depth = depth_request;
    memory_depth.backend = DSLT_BACKEND_CUDA;
    (void)run_float_operation(handle, memory_depth, DSLT_BACKEND_CUDA);
    auto memory_projection = memory_height;
    memory_projection.operation = DSLT_OP_HEIGHT_PROJECTION;
    memory_projection.target_spacing_z = 0.0F;
    memory_projection.minimum_component_size = 2;
    memory_projection.constant_c = 0.1F;
    memory_projection.window_min = 0.0F;
    memory_projection.window_max = 0.0F;
    (void)run_float_operation_result(
        handle, memory_projection, DSLT_BACKEND_CUDA, DSLT_OUTPUT_IMAGE_FLOAT32);
    dslt_operation_request memory_components{};
    memory_components.operation = DSLT_OP_CONNECTED_COMPONENTS;
    memory_components.backend = DSLT_BACKEND_CUDA;
    memory_components.threshold = 0.5F;
    memory_components.connectivity = 26;
    memory_components.minimum_component_size = 1;
    (void)run_label_operation_result(handle, memory_components, DSLT_BACKEND_CUDA);
    dslt_operation_request memory_h_minima{};
    memory_h_minima.operation = DSLT_OP_H_MINIMA;
    memory_h_minima.backend = DSLT_BACKEND_CUDA;
    memory_h_minima.threshold = 0.5F;
    memory_h_minima.radius = 50;
    (void)run_float_operation(handle, memory_h_minima, DSLT_BACKEND_CUDA);
    dslt_operation_request memory_watershed{};
    memory_watershed.operation = DSLT_OP_WATERSHED;
    memory_watershed.backend = DSLT_BACKEND_CUDA;
    memory_watershed.minimum_component_size = 0;
    require(dslt_set_label_state_i32(
        handle, watershed_seeds.data(), watershed_seeds.size(),
        watershed_selected.data(), watershed_selected.size()));
    (void)run_label_operation_result(handle, memory_watershed, DSLT_BACKEND_CUDA);
    auto memory_dslt_threshold = dslt_threshold_requests[0];
    memory_dslt_threshold.backend = DSLT_BACKEND_CUDA;
    (void)run_float_operation(handle, memory_dslt_threshold, DSLT_BACKEND_CUDA);
    auto memory_threshold_sweep = threshold_sweep_request;
    memory_threshold_sweep.backend = DSLT_BACKEND_CUDA;
    (void)run_label_operation_result(handle, memory_threshold_sweep, DSLT_BACKEND_CUDA);
    auto memory_dslt_segmentation = dslt_segmentation_request;
    memory_dslt_segmentation.backend = DSLT_BACKEND_CUDA;
    (void)run_label_operation_result(handle, memory_dslt_segmentation, DSLT_BACKEND_CUDA);
    dslt_operation_request memory_adaptive{};
    memory_adaptive.operation = DSLT_OP_ADAPTIVE_THRESHOLD_3D;
    memory_adaptive.backend = DSLT_BACKEND_CUDA;
    memory_adaptive.radius = 1;
    memory_adaptive.connectivity = 0;
    memory_adaptive.constant_c = 0.05F;
    (void)run_float_operation(handle, memory_adaptive, DSLT_BACKEND_CUDA);

    dslt_backend_info before{};
    require(dslt_get_backend_info(handle, &before));
    for (int iteration = 0; iteration < 100; ++iteration) {
        switch (iteration % 11) {
        case 0:
            (void)run_float_operation(handle, request, DSLT_BACKEND_CUDA);
            break;
        case 1:
            (void)run_float_operation_result(
                handle, memory_height, DSLT_BACKEND_CUDA, DSLT_OUTPUT_IMAGE_FLOAT32);
            break;
        case 2:
            (void)run_float_operation(handle, memory_depth, DSLT_BACKEND_CUDA);
            break;
        case 3:
            memory_projection.target_spacing_z = iteration % 8 == 3 ? 0.0F : 1.0F;
            (void)run_float_operation_result(
                handle, memory_projection, DSLT_BACKEND_CUDA, DSLT_OUTPUT_IMAGE_FLOAT32);
            break;
        case 5:
            (void)run_float_operation(handle, memory_h_minima, DSLT_BACKEND_CUDA);
            break;
        case 6:
            require(dslt_set_label_state_i32(
                handle, watershed_seeds.data(), watershed_seeds.size(),
                watershed_selected.data(), watershed_selected.size()));
            (void)run_label_operation_result(handle, memory_watershed, DSLT_BACKEND_CUDA);
            break;
        case 7:
            (void)run_float_operation(handle, memory_dslt_threshold, DSLT_BACKEND_CUDA);
            break;
        case 8:
            (void)run_label_operation_result(handle, memory_threshold_sweep, DSLT_BACKEND_CUDA);
            break;
        case 9:
            (void)run_label_operation_result(handle, memory_dslt_segmentation, DSLT_BACKEND_CUDA);
            break;
        case 10:
            (void)run_float_operation(handle, memory_adaptive, DSLT_BACKEND_CUDA);
            break;
        default:
            (void)run_label_operation_result(handle, memory_components, DSLT_BACKEND_CUDA);
            break;
        }
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

    dslt_crop_options provided_surface_options{};
    provided_surface_options.use_height_map = 1;
    const std::vector<float> provided_surface{2.0F};
    require(dslt_set_crop(
        handle, &provided_surface_options, provided_surface.data(), provided_surface.size()));
    request.window_min = 0.0F;
    request.minimum_component_size = 0;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    projection_result.resize(result.element_count);
    require(dslt_copy_output_f32(handle, projection_result.data(), projection_result.size()));
    assert(projection_result.size() == 1 && std::abs(projection_result[0] - 0.8F) < 1.0e-6F);

    request = {};
    request.operation = DSLT_OP_DEPTH_MAP;
    request.backend = DSLT_BACKEND_CPU;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    std::vector<float> provided_depth_result(result.element_count);
    require(dslt_copy_output_f32(
        handle, provided_depth_result.data(), provided_depth_result.size()));
    assert(provided_depth_result.size() == 4);
    assert(provided_depth_result[0] == 0.0F && provided_depth_result[1] == 0.0F &&
        provided_depth_result[2] == 0.0F &&
        std::abs(provided_depth_result[3] - 1.0F) < 1.0e-6F);

    provided_surface_options = {};
    require(dslt_set_crop(handle, &provided_surface_options, nullptr, 0));

    request = {};
    request.operation = DSLT_OP_HEIGHT_PROJECTION;
    request.backend = DSLT_BACKEND_CPU;
    request.target_spacing_z = 2.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_INVALID_ARGUMENT);

    const std::vector<float> z_gradient_source{0.1F, 0.1F, 0.1F, 0.1F};
    require(dslt_set_volume_f32(
        handle, &projection_desc, z_gradient_source.data(), z_gradient_source.size()));
    dslt_crop_options z_gradient_crop{};
    z_gradient_crop.use_height_map = 1;
    const std::vector<float> z_gradient_height_map{1.0F};
    require(dslt_set_crop(
        handle, &z_gradient_crop, z_gradient_height_map.data(), z_gradient_height_map.size()));
    request = {};
    request.operation = DSLT_OP_Z_GRADIENT;
    request.backend = DSLT_BACKEND_CPU;
    request.constant_c = 2.0F;
    request.threshold = 1.0F;
    request.window_min = 0.0F;
    request.window_max = 1.0F;
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result));
    assert(result.output_kind == DSLT_OUTPUT_VOLUME_FLOAT32 && result.element_count == 4);
    std::vector<float> z_gradient_result(result.element_count);
    require(dslt_copy_output_f32(
        handle, z_gradient_result.data(), z_gradient_result.size()));
    assert(std::abs(z_gradient_result[0] - 0.1F) < 1.0e-6F);
    assert(std::abs(z_gradient_result[2] - 0.15F) < 1.0e-6F);
    assert(std::abs(z_gradient_result[3] - 0.2F) < 1.0e-6F);

    request.constant_c = -1.0F;
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

    request = {};
    request.operation = DSLT_OP_RESAMPLE_Z_AREA;
    request.backend = DSLT_BACKEND_CPU;
    request.target_spacing_z = std::numeric_limits<float>::min();
    require(dslt_run_operation(handle, &request, nullptr, nullptr, &result), DSLT_RESOURCE_LIMIT);

    dslt_destroy(handle);
    lifecycle_stress_test();
#ifdef DSLT_TEST_CUDA
    cuda_pointwise_parity_test();
#endif
    std::cout << "DSLT native synthetic tests passed\n";
    return 0;
}
