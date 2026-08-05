cmake_minimum_required(VERSION 3.30)
project(dslt_workbench_native VERSION 0.1.0 LANGUAGES CXX)

option(DSLT_ENABLE_CUDA "Build the optional CUDA backend" OFF)
option(DSLT_ENABLE_CZI "Build the optional read-only CZI input adapter" ON)
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

if(DSLT_ENABLE_CZI)
    include(FetchContent)

    # libCZI is intentionally pinned. Update this URL and hash together with
    # modern/validation/libczi-dependency.lock.json after a compatibility review.
    set(LIBCZI_DO_NOT_SET_MSVC_RUNTIME_LIBRARY ON CACHE BOOL "" FORCE)
    set(LIBCZI_BUILD_UNITTESTS OFF CACHE BOOL "" FORCE)
    set(LIBCZI_BUILD_CZICMD OFF CACHE BOOL "" FORCE)
    set(LIBCZI_BUILD_DYNLIB ON CACHE BOOL "" FORCE)
    set(LIBCZI_ENABLE_INSTALL OFF CACHE BOOL "" FORCE)
    set(LIBCZI_BUILD_LIBCZIAPI OFF CACHE BOOL "" FORCE)
    set(LIBCZI_BUILD_CURL_BASED_STREAM OFF CACHE BOOL "" FORCE)
    set(LIBCZI_BUILD_AZURESDK_BASED_STREAM OFF CACHE BOOL "" FORCE)
    set(LIBCZI_BUILD_PREFER_EXTERNALPACKAGE_EIGEN3 OFF CACHE BOOL "" FORCE)
    set(LIBCZI_BUILD_PREFER_EXTERNALPACKAGE_ZSTD OFF CACHE BOOL "" FORCE)
    set(LIBCZI_BUILD_ENABLE_EXPERIMENTAL_FUNCTIONALITY OFF CACHE STRING "" FORCE)
    set(LIBCZI_BUILD_EXPERIMENTAL_CHUNKED_COMPRESSION OFF CACHE BOOL "" FORCE)

    FetchContent_Declare(libczi
        URL https://github.com/ZEISS/libczi/archive/61f74ff097d6d0fbe6e36f204ff59d92e299d7cd.zip
        URL_HASH SHA256=fe8e9917903cfb5c716e0a0d2b7805cd298e2fb758b61f330c1bc5385a99ab5f
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    )
    FetchContent_MakeAvailable(libczi)

    add_library(dslt_czi SHARED src/czi_api.cpp)
    target_compile_features(dslt_czi PUBLIC cxx_std_20)
    target_include_directories(dslt_czi PUBLIC $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>)
    target_compile_definitions(dslt_czi PRIVATE DSLT_CZI_EXPORTS)
    target_link_libraries(dslt_czi PRIVATE libCZI)
    dslt_enable_address_sanitizer(dslt_czi)
    if(MSVC)
        target_compile_options(dslt_czi PRIVATE /W4 /permissive- /EHsc /utf-8)
    else()
        target_compile_options(dslt_czi PRIVATE -Wall -Wextra -Wpedantic)
    endif()
    add_custom_command(TARGET dslt_czi POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            $<TARGET_FILE:libCZI>
            $<TARGET_FILE_DIR:dslt_czi>
        VERBATIM
    )
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

    if(DSLT_ENABLE_CZI)
        add_executable(dslt_czi_tests tests/czi_tests.cpp)
        target_compile_features(dslt_czi_tests PRIVATE cxx_std_20)
        target_link_libraries(dslt_czi_tests PRIVATE dslt_czi libCZI)
        dslt_enable_address_sanitizer(dslt_czi_tests)
        if(MSVC)
            target_compile_options(dslt_czi_tests PRIVATE /W4 /permissive- /EHsc /utf-8 /UNDEBUG)
        else()
            target_compile_options(dslt_czi_tests PRIVATE -Wall -Wextra -Wpedantic -UNDEBUG)
        endif()
        add_test(NAME dslt_czi_tests COMMAND dslt_czi_tests)
    endif()
endif()
