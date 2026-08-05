cmake_minimum_required(VERSION 3.30)
project(dslt_workbench_native VERSION 0.1.0 LANGUAGES CXX)

option(DSLT_ENABLE_CUDA "Build the optional CUDA backend" OFF)
option(DSLT_BUILD_TESTS "Build native tests" ON)
option(DSLT_ENABLE_ASAN "Enable AddressSanitizer for native targets" OFF)

function(dslt_enable_address_sanitizer target_name)
    if(NOT DSLT_ENABLE_ASAN)
        return()
    endif()

    if(MSVC)
        target_compile_options(${target_name} PRIVATE /fsanitize=address /Zi)
        target_link_options(${target_name} PRIVATE /INCREMENTAL:NO /DEBUG)
        get_filename_component(dslt_msvc_compiler_directory "${CMAKE_CXX_COMPILER}" DIRECTORY)
        foreach(dslt_asan_runtime IN ITEMS
                clang_rt.asan_dynamic-x86_64.dll
                clang_rt.asan_dbg_dynamic-x86_64.dll)
            set(dslt_asan_runtime_path "${dslt_msvc_compiler_directory}/${dslt_asan_runtime}")
            if(EXISTS "${dslt_asan_runtime_path}")
                add_custom_command(TARGET ${target_name} POST_BUILD
                    COMMAND ${CMAKE_COMMAND} -E copy_if_different
                        "${dslt_asan_runtime_path}"
                        "$<TARGET_FILE_DIR:${target_name}>"
                    VERBATIM
                )
            endif()
        endforeach()
    else()
        target_compile_options(${target_name} PRIVATE -fsanitize=address -fno-omit-frame-pointer)
        target_link_options(${target_name} PRIVATE -fsanitize=address)
    endif()
endfunction()

add_library(dslt_core SHARED
    src/c_api.cpp
    src/engine.cpp
    src/operations.cpp
    src/tiff_io_stub.cpp
)

target_compile_features(dslt_core PUBLIC cxx_std_20)
target_include_directories(dslt_core
    PUBLIC $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
)
target_compile_definitions(dslt_core PRIVATE DSLT_CORE_EXPORTS)
dslt_enable_address_sanitizer(dslt_core)

if(MSVC)
    target_compile_options(dslt_core PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:/W4>
        $<$<COMPILE_LANGUAGE:CXX>:/permissive->
        $<$<COMPILE_LANGUAGE:CXX>:/EHsc>
        $<$<COMPILE_LANGUAGE:CXX>:/utf-8>
    )
else()
    target_compile_options(dslt_core PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:-Wall>
        $<$<COMPILE_LANGUAGE:CXX>:-Wextra>
        $<$<COMPILE_LANGUAGE:CXX>:-Wpedantic>
    )
endif()

if(DSLT_ENABLE_CUDA)
    enable_language(CUDA)
    target_sources(dslt_core PRIVATE src/cuda_backend.cu)
    target_compile_definitions(dslt_core PRIVATE DSLT_HAS_CUDA=1)
    set_target_properties(dslt_core PROPERTIES
        CUDA_STANDARD 20
        CUDA_STANDARD_REQUIRED ON
        CUDA_EXTENSIONS OFF
    )
else()
    target_sources(dslt_core PRIVATE src/cuda_backend_stub.cpp)
endif()

if(DSLT_BUILD_TESTS)
    enable_testing()
    add_executable(dslt_native_tests tests/native_tests.cpp)
    target_compile_features(dslt_native_tests PRIVATE cxx_std_20)
    target_link_libraries(dslt_native_tests PRIVATE dslt_core)
    if(DSLT_ENABLE_CUDA)
        target_compile_definitions(dslt_native_tests PRIVATE DSLT_TEST_CUDA=1)
    endif()
    dslt_enable_address_sanitizer(dslt_native_tests)
    if(MSVC)
        target_compile_options(dslt_native_tests PRIVATE /UNDEBUG)
    else()
        target_compile_options(dslt_native_tests PRIVATE -UNDEBUG)
    endif()
    add_test(NAME dslt_native_tests COMMAND dslt_native_tests)

    add_executable(dslt_algorithm_tests
        tests/algorithm_tests.cpp
        src/engine.cpp
        src/operations.cpp
        src/cuda_backend_stub.cpp
    )
    target_compile_features(dslt_algorithm_tests PRIVATE cxx_std_20)
    target_include_directories(dslt_algorithm_tests PRIVATE include src)
    dslt_enable_address_sanitizer(dslt_algorithm_tests)
    if(MSVC)
        target_compile_options(dslt_algorithm_tests PRIVATE /W4 /permissive- /EHsc /utf-8 /UNDEBUG)
    else()
        target_compile_options(dslt_algorithm_tests PRIVATE -Wall -Wextra -Wpedantic -UNDEBUG)
    endif()
    add_test(NAME dslt_algorithm_tests COMMAND dslt_algorithm_tests)
endif()
