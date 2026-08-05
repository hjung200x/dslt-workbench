#include "dslt_czi.h"

#include <libCZI.h>
#include <libCZI_Metadata2.h>

#include <cassert>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <memory>
#include <string>
#include <tuple>
#include <vector>

namespace {

constexpr int width = 4;
constexpr int height = 3;
constexpr int depth = 2;
constexpr int channels = 2;

std::string last_error() {
    std::size_t required{};
    const auto query_status = dslt_czi_copy_last_error_utf8(nullptr, 0, &required);
    assert(query_status == DSLT_CZI_BUFFER_TOO_SMALL);
    assert(required > 1);
    std::vector<char> buffer(required);
    assert(dslt_czi_copy_last_error_utf8(buffer.data(), buffer.size(), &required) == DSLT_CZI_OK);
    return buffer.data();
}

void require(const int32_t actual, const int32_t expected = DSLT_CZI_OK) {
    if (actual != expected) {
        std::cerr << "unexpected CZI status " << actual << ", expected " << expected
                  << ": " << last_error() << '\n';
        std::abort();
    }
}

std::filesystem::path temporary_path(const char* suffix) {
    static uint64_t sequence{};
    return std::filesystem::temp_directory_path() /
        ("dslt-czi-test-" + std::to_string(++sequence) + suffix);
}

struct fixture_options {
    libCZI::PixelType pixel_type{libCZI::PixelType::Gray8};
    bool skip_last_plane{};
    bool unsupported_h{};
    bool mosaic{};
};

uint32_t sample_bytes(const libCZI::PixelType type) {
    switch (type) {
    case libCZI::PixelType::Gray8: return 1;
    case libCZI::PixelType::Gray16: return 2;
    case libCZI::PixelType::Gray32Float: return 4;
    default: std::abort();
    }
}

std::vector<uint8_t> plane_values(const libCZI::PixelType type, const int channel, const int z) {
    const auto bytes = sample_bytes(type);
    std::vector<uint8_t> values(static_cast<size_t>(width * height) * bytes);
    for (int index = 0; index < width * height; ++index) {
        const auto base = channel * 100 + z * 20 + index;
        if (type == libCZI::PixelType::Gray8) {
            values[static_cast<size_t>(index)] = static_cast<uint8_t>(base);
        }
        else if (type == libCZI::PixelType::Gray16) {
            const auto value = static_cast<uint16_t>(1000 + base * 17);
            std::memcpy(values.data() + static_cast<size_t>(index) * bytes, &value, sizeof(value));
        }
        else {
            const auto value = static_cast<float>(base) / 7.0F - 3.0F;
            std::memcpy(values.data() + static_cast<size_t>(index) * bytes, &value, sizeof(value));
        }
    }
    return values;
}

void add_plane(
    const std::shared_ptr<libCZI::ICziWriter>& writer,
    const fixture_options& options,
    const int channel,
    const int z,
    const int h,
    const int m,
    const int x) {
    const auto values = plane_values(options.pixel_type, channel, z);
    libCZI::AddSubBlockInfoStridedBitmap info;
    info.Clear();
    info.coordinate.Set(libCZI::DimensionIndex::S, 0);
    info.coordinate.Set(libCZI::DimensionIndex::T, 0);
    info.coordinate.Set(libCZI::DimensionIndex::C, channel);
    info.coordinate.Set(libCZI::DimensionIndex::Z, z);
    if (options.unsupported_h) info.coordinate.Set(libCZI::DimensionIndex::H, h);
    info.mIndexValid = options.mosaic;
    info.mIndex = m;
    info.x = x;
    info.y = 0;
    info.logicalWidth = width;
    info.logicalHeight = height;
    info.physicalWidth = width;
    info.physicalHeight = height;
    info.PixelType = options.pixel_type;
    info.ptrBitmap = values.data();
    info.strideBitmap = static_cast<uint32_t>(width) * sample_bytes(options.pixel_type);
    writer->SyncAddSubBlock(info);
}

std::filesystem::path create_fixture(const fixture_options& options) {
    const auto path = temporary_path(".czi");
    const auto writer = libCZI::CreateCZIWriter();
    writer->Create(libCZI::CreateOutputStreamForFile(path.c_str(), true), nullptr);
    for (int channel = 0; channel < channels; ++channel) {
        for (int z = 0; z < depth; ++z) {
            if (options.skip_last_plane && channel == channels - 1 && z == depth - 1) continue;
            const auto h_count = options.unsupported_h ? 2 : 1;
            for (int h = 0; h < h_count; ++h) {
                add_plane(writer, options, channel, z, h, 0, 0);
                if (options.mosaic) add_plane(writer, options, channel, z, h, 1, width);
            }
        }
    }

    libCZI::PrepareMetadataInfo prepare;
    prepare.funcGenerateIdAndNameForChannel = [](const int channel) {
        return std::make_tuple(
            "Channel:" + std::to_string(channel),
            std::make_tuple(true, channel == 0 ? "CW2MR" : "mCherry"));
    };
    const auto builder = writer->GetPreparedMetadata(prepare);
    libCZI::ScalingInfoEx scaling;
    scaling.scaleX = 0.25e-6;
    scaling.scaleY = 0.5e-6;
    scaling.scaleZ = 1.5e-6;
    scaling.defaultUnitFormatX = scaling.defaultUnitFormatY = scaling.defaultUnitFormatZ = L"um";
    libCZI::MetadataUtils::WriteScalingInfoEx(builder.get(), scaling);
    libCZI::WriteMetadataInfo metadata;
    metadata.Clear();
    const auto& xml = builder->GetXml();
    metadata.szMetadata = xml.c_str();
    metadata.szMetadataSize = xml.size() + 1;
    writer->SyncWriteMetadata(metadata);
    writer->Close();
    return path;
}

struct scoped_file {
    explicit scoped_file(std::filesystem::path path) : path(std::move(path)) {}
    ~scoped_file() { std::error_code error; std::filesystem::remove(path, error); }
    std::filesystem::path path;
};

dslt_czi_document_info_v1 document_info(dslt_czi_document* document) {
    dslt_czi_document_info_v1 info{};
    info.struct_size = sizeof(info);
    require(dslt_czi_get_document_info(document, &info));
    return info;
}

std::vector<uint8_t> expected_volume(const libCZI::PixelType type) {
    std::vector<uint8_t> expected;
    for (int channel = 0; channel < channels; ++channel) {
        for (int z = 0; z < depth; ++z) {
            const auto plane = plane_values(type, channel, z);
            expected.insert(expected.end(), plane.begin(), plane.end());
        }
    }
    return expected;
}

void round_trip_test(const libCZI::PixelType pixel_type, const uint32_t expected_voxel_type) {
    const scoped_file fixture(create_fixture({pixel_type}));
    dslt_czi_document* document{};
    require(dslt_czi_open_utf8(fixture.path.string().c_str(), &document));
    const auto info = document_info(document);
    assert(info.abi_version == DSLT_CZI_ABI_VERSION_V1);
    assert(info.scene_count == 1 && info.time_count == 1 && info.channel_count == channels && info.z_count == depth);
    assert(info.spacing_flags == 0b111);
    assert(std::abs(info.spacing_x_um - 0.25) < 1e-12);
    assert(std::abs(info.spacing_y_um - 0.5) < 1e-12);
    assert(std::abs(info.spacing_z_um - 1.5) < 1e-12);

    dslt_czi_channel_info_v1 channel_info{};
    channel_info.struct_size = sizeof(channel_info);
    require(dslt_czi_get_channel_info(document, 0, &channel_info));
    assert(channel_info.voxel_type == expected_voxel_type && (channel_info.flags & 1u) != 0);
    size_t name_size{};
    require(dslt_czi_copy_channel_name_utf8(document, 0, nullptr, 0, &name_size), DSLT_CZI_BUFFER_TOO_SMALL);
    std::vector<char> name(name_size);
    require(dslt_czi_copy_channel_name_utf8(document, 0, name.data(), name.size(), &name_size));
    assert(std::string(name.data()) == "CW2MR");

    const int32_t selected_channels[]{0, 1};
    dslt_czi_selection_v1 selection{};
    selection.struct_size = sizeof(selection);
    selection.scene_index = 0;
    selection.time_index = 0;
    selection.channel_indices = selected_channels;
    selection.channel_count = channels;
    selection.maximum_output_bytes = std::numeric_limits<uint64_t>::max();
    dslt_czi_volume* volume{};
    require(dslt_czi_read_volume(document, &selection, nullptr, nullptr, &volume));
    dslt_czi_volume_info_v1 volume_info{};
    volume_info.struct_size = sizeof(volume_info);
    require(dslt_czi_get_volume_info(volume, &volume_info));
    assert(volume_info.width == width && volume_info.height == height && volume_info.depth == depth);
    assert(volume_info.channels == channels && volume_info.voxel_type == expected_voxel_type);
    const auto expected = expected_volume(pixel_type);
    std::vector<uint8_t> actual(expected.size());
    require(dslt_czi_copy_raw_samples(volume, actual.data(), actual.size()));
    assert(actual == expected);
    require(dslt_czi_release_volume(volume));

    selection.maximum_output_bytes = expected.size() - 1;
    volume = nullptr;
    require(dslt_czi_read_volume(document, &selection, nullptr, nullptr, &volume), DSLT_CZI_ALLOCATION_LIMIT);
    assert(volume == nullptr);

    auto cancel = [](const float progress, void*) -> int32_t { return progress > 0 ? 1 : 0; };
    selection.maximum_output_bytes = expected.size();
    require(dslt_czi_read_volume(document, &selection, cancel, nullptr, &volume), DSLT_CZI_CANCELLED);
    assert(volume == nullptr);
    require(dslt_czi_close(document));
}

void failure_fixture_tests() {
    {
        const scoped_file fixture(create_fixture({libCZI::PixelType::Gray8, true}));
        dslt_czi_document* document{};
        require(dslt_czi_open_utf8(fixture.path.string().c_str(), &document));
        const int32_t selected[]{0, 1};
        dslt_czi_selection_v1 selection{sizeof(selection), 0, 0, selected, 2, 4096};
        dslt_czi_volume* volume{};
        require(dslt_czi_read_volume(document, &selection, nullptr, nullptr, &volume), DSLT_CZI_INVALID_DATA);
        assert(volume == nullptr);
        require(dslt_czi_close(document));
    }
    {
        const scoped_file fixture(create_fixture({libCZI::PixelType::Gray8, false, true}));
        dslt_czi_document* document{};
        require(dslt_czi_open_utf8(fixture.path.string().c_str(), &document));
        assert(document_info(document).unsupported_dimensions_mask != 0);
        const int32_t selected[]{0};
        dslt_czi_selection_v1 selection{sizeof(selection), 0, 0, selected, 1, 4096};
        dslt_czi_volume* volume{};
        require(dslt_czi_read_volume(document, &selection, nullptr, nullptr, &volume), DSLT_CZI_UNSUPPORTED);
        require(dslt_czi_close(document));
    }
    {
        const scoped_file fixture(create_fixture({libCZI::PixelType::Gray8, false, false, true}));
        dslt_czi_document* document{};
        require(dslt_czi_open_utf8(fixture.path.string().c_str(), &document));
        assert((document_info(document).document_flags & DSLT_CZI_DOCUMENT_HAS_MULTIPLE_TILES) != 0);
        require(dslt_czi_close(document));
    }
    {
        const scoped_file corrupt(temporary_path("-corrupt.czi"));
        std::ofstream output(corrupt.path, std::ios::binary);
        output << "not a CZI";
        output.close();
        dslt_czi_document* document{};
        const auto status = dslt_czi_open_utf8(corrupt.path.string().c_str(), &document);
        assert(status == DSLT_CZI_INVALID_DATA || status == DSLT_CZI_IO_ERROR);
        assert(document == nullptr);
    }
}

void abi_contract_test() {
    assert(dslt_czi_get_abi_version() == DSLT_CZI_ABI_VERSION_V1);
    dslt_czi_document* document = nullptr;
    require(dslt_czi_open_utf8(nullptr, &document), DSLT_CZI_INVALID_ARGUMENT);
    assert(document == nullptr && !last_error().empty());
    require(dslt_czi_open_utf8("", &document), DSLT_CZI_INVALID_ARGUMENT);
    require(dslt_czi_open_utf8("Z:/this/path/does/not/exist/input.czi", &document), DSLT_CZI_IO_ERROR);
    dslt_czi_document_info_v1 info{};
    info.struct_size = sizeof(info);
    require(dslt_czi_get_document_info(nullptr, &info), DSLT_CZI_INVALID_ARGUMENT);
    require(dslt_czi_close(nullptr), DSLT_CZI_INVALID_ARGUMENT);
}

void buffer_contract_test() {
    std::size_t required{};
    require(dslt_czi_copy_last_error_utf8(nullptr, 0, &required), DSLT_CZI_BUFFER_TOO_SMALL);
    assert(required > 1);
    char too_small[1]{};
    require(dslt_czi_copy_last_error_utf8(too_small, sizeof(too_small), &required), DSLT_CZI_BUFFER_TOO_SMALL);
    std::vector<char> destination(required);
    require(dslt_czi_copy_last_error_utf8(destination.data(), destination.size(), &required));
    assert(destination.back() == '\0');
}

} // namespace

int main() {
    abi_contract_test();
    buffer_contract_test();
    round_trip_test(libCZI::PixelType::Gray8, DSLT_CZI_VOXEL_UINT8);
    round_trip_test(libCZI::PixelType::Gray16, DSLT_CZI_VOXEL_UINT16);
    round_trip_test(libCZI::PixelType::Gray32Float, DSLT_CZI_VOXEL_FLOAT32);
    failure_fixture_tests();
    std::cout << "CZI ABI, round-trip, cancellation, and failure tests passed.\n";
    return 0;
}
