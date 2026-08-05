#include "dslt_czi.h"

#include <libCZI.h>
#include <libCZI_Metadata2.h>
#include <libCZI_Utilities.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <limits>
#include <memory>
#include <new>
#include <set>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

#if defined(_WIN32)
#include <windows.h>
#endif

namespace {

constexpr double meters_to_micrometers = 1'000'000.0;
constexpr uint32_t channel_supported = 1u << 0;

thread_local std::string last_error;

class status_error final : public std::runtime_error {
public:
    status_error(const dslt_czi_status status, std::string message)
        : std::runtime_error(std::move(message)), status_(status) {}

    [[nodiscard]] dslt_czi_status status() const noexcept { return status_; }

private:
    dslt_czi_status status_;
};

struct scene_record {
    int32_t index{};
    libCZI::IntRect rectangle{};
};

struct channel_record {
    int32_t index{};
    dslt_czi_voxel_type voxel_type{};
    bool supported{};
    std::string name;
    uint8_t red{255};
    uint8_t green{255};
    uint8_t blue{255};
    uint8_t alpha{255};
};

std::wstring utf8_to_wide(const char* text) {
    if (text == nullptr || *text == '\0') {
        throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI path is empty.");
    }
#if defined(_WIN32)
    const auto length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, -1, nullptr, 0);
    if (length <= 0) {
        throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI path is not valid UTF-8.");
    }
    std::wstring result(static_cast<size_t>(length), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, -1, result.data(), length) <= 0) {
        throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI path could not be converted to UTF-16.");
    }
    result.pop_back();
    return result;
#else
    const std::filesystem::path path = std::filesystem::u8path(text);
    return path.wstring();
#endif
}

std::string wide_to_utf8(const std::wstring& text) {
#if defined(_WIN32)
    if (text.empty()) return {};
    const auto length = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()),
        nullptr, 0, nullptr, nullptr);
    if (length <= 0) return {};
    std::string result(static_cast<size_t>(length), '\0');
    if (WideCharToMultiByte(
            CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()),
            result.data(), length, nullptr, nullptr) <= 0) {
        return {};
    }
    return result;
#else
    return std::filesystem::path(text).u8string();
#endif
}

bool try_interval(
    const libCZI::IDimBounds& bounds,
    const libCZI::DimensionIndex dimension,
    int32_t& start,
    int32_t& count) {
    int native_start{};
    int native_count{};
    if (!bounds.TryGetInterval(dimension, &native_start, &native_count)) {
        start = 0;
        count = 1;
        return false;
    }
    if (native_count <= 0) {
        throw status_error(DSLT_CZI_INVALID_DATA, "CZI contains an empty dimension interval.");
    }
    start = native_start;
    count = native_count;
    return true;
}

dslt_czi_voxel_type map_voxel_type(const libCZI::PixelType pixel_type) {
    switch (pixel_type) {
    case libCZI::PixelType::Gray8: return DSLT_CZI_VOXEL_UINT8;
    case libCZI::PixelType::Gray16: return DSLT_CZI_VOXEL_UINT16;
    case libCZI::PixelType::Gray32Float: return DSLT_CZI_VOXEL_FLOAT32;
    default: throw status_error(
        DSLT_CZI_UNSUPPORTED,
        std::string("Unsupported CZI pixel type: ") + libCZI::Utils::PixelTypeToInformalString(pixel_type) + ".");
    }
}

uint32_t bytes_per_sample(const dslt_czi_voxel_type voxel_type) {
    switch (voxel_type) {
    case DSLT_CZI_VOXEL_UINT8: return 1;
    case DSLT_CZI_VOXEL_UINT16: return 2;
    case DSLT_CZI_VOXEL_FLOAT32: return 4;
    default: throw status_error(DSLT_CZI_UNSUPPORTED, "Unsupported CZI voxel type.");
    }
}

uint64_t checked_multiply(const uint64_t left, const uint64_t right, const char* description) {
    if (left != 0 && right > (std::numeric_limits<uint64_t>::max)() / left) {
        throw status_error(DSLT_CZI_ALLOCATION_LIMIT, std::string(description) + " overflows 64-bit size.");
    }
    return left * right;
}

void require_struct(const uint32_t actual, const uint32_t expected, const char* name) {
    if (actual != expected) {
        throw status_error(
            DSLT_CZI_INVALID_ARGUMENT,
            std::string(name) + " struct_size does not match ABI v1.");
    }
}

template <typename Function>
int32_t guarded(Function&& function) noexcept {
    try {
        function();
        last_error.clear();
        return DSLT_CZI_OK;
    }
    catch (const status_error& error) {
        last_error = error.what();
        return error.status();
    }
    catch (const std::bad_alloc&) {
        last_error = "CZI allocation failed.";
        return DSLT_CZI_ALLOCATION_LIMIT;
    }
    catch (const std::filesystem::filesystem_error& error) {
        last_error = error.what();
        return DSLT_CZI_IO_ERROR;
    }
    catch (const std::exception& error) {
        last_error = error.what();
        return DSLT_CZI_INVALID_DATA;
    }
    catch (...) {
        last_error = "Unknown native CZI error.";
        return DSLT_CZI_INTERNAL_ERROR;
    }
}

} // namespace

struct dslt_czi_document {
    std::shared_ptr<libCZI::ICZIReader> reader;
    libCZI::SubBlockStatistics statistics{};
    std::vector<scene_record> scenes;
    std::vector<channel_record> channels;
    int32_t time_start{};
    int32_t time_count{1};
    int32_t channel_start{};
    int32_t channel_count{1};
    int32_t z_start{};
    int32_t z_count{1};
    uint32_t flags{};
    uint32_t unsupported_dimensions_mask{};
    uint32_t spacing_flags{};
    double spacing_x_um{1};
    double spacing_y_um{1};
    double spacing_z_um{1};

    ~dslt_czi_document() {
        try {
            if (reader) reader->Close();
        }
        catch (...) {
        }
    }
};

struct dslt_czi_volume {
    dslt_czi_volume_info_v1 info{};
    std::vector<uint8_t> raw_samples;
};

namespace {

void populate_metadata(dslt_czi_document& document) {
    try {
        const auto metadata_segment = document.reader->ReadMetadataSegment();
        if (!metadata_segment) return;
        const auto metadata = metadata_segment->CreateMetaFromMetadataSegment();
        if (!metadata || !metadata->IsXmlValid()) return;
        const auto info = metadata->GetDocumentInfo();
        if (!info) return;
        document.flags |= DSLT_CZI_DOCUMENT_METADATA_VALID;

        const auto scaling = info->GetScalingInfoEx();
        if (scaling.IsScaleXValid() && std::isfinite(scaling.scaleX) && scaling.scaleX > 0) {
            document.spacing_x_um = scaling.scaleX * meters_to_micrometers;
            document.spacing_flags |= DSLT_CZI_SPACING_X_VALID;
        }
        if (scaling.IsScaleYValid() && std::isfinite(scaling.scaleY) && scaling.scaleY > 0) {
            document.spacing_y_um = scaling.scaleY * meters_to_micrometers;
            document.spacing_flags |= DSLT_CZI_SPACING_Y_VALID;
        }
        if (scaling.IsScaleZValid() && std::isfinite(scaling.scaleZ) && scaling.scaleZ > 0) {
            document.spacing_z_um = scaling.scaleZ * meters_to_micrometers;
            document.spacing_flags |= DSLT_CZI_SPACING_Z_VALID;
        }

        const auto channel_info = info->GetDimensionChannelsInfo();
        const auto display_settings = info->GetDisplaySettings();
        for (size_t ordinal = 0; ordinal < document.channels.size(); ++ordinal) {
            auto& target = document.channels[ordinal];
            if (channel_info && ordinal < static_cast<size_t>(channel_info->GetChannelCount())) {
                const auto source = channel_info->GetChannel(static_cast<int>(ordinal));
                if (source) {
                    std::wstring name;
                    if (source->TryGetAttributeName(&name) && !name.empty()) {
                        target.name = wide_to_utf8(name);
                    }
                    libCZI::Rgb8Color color{};
                    if (source->TryGetColor(&color)) {
                        target.red = color.r;
                        target.green = color.g;
                        target.blue = color.b;
                    }
                }
            }
            if (display_settings) {
                const auto display = display_settings->GetChannelDisplaySettings(target.index);
                libCZI::Rgb8Color color{};
                if (display && display->TryGetTintingColorRgb8(&color)) {
                    target.red = color.r;
                    target.green = color.g;
                    target.blue = color.b;
                }
            }
        }
    }
    catch (...) {
        document.flags &= ~DSLT_CZI_DOCUMENT_METADATA_VALID;
        document.spacing_flags = 0;
        document.spacing_x_um = document.spacing_y_um = document.spacing_z_um = 1;
    }
}

void inspect_document(dslt_czi_document& document) {
    document.statistics = document.reader->GetStatistics();
    if (document.statistics.subBlockCount <= 0 ||
        !document.statistics.boundingBoxLayer0Only.IsNonEmpty()) {
        throw status_error(DSLT_CZI_INVALID_DATA, "CZI contains no full-resolution image subblocks.");
    }

    auto& bounds = document.statistics.dimBounds;
    try_interval(bounds, libCZI::DimensionIndex::T, document.time_start, document.time_count);
    try_interval(bounds, libCZI::DimensionIndex::C, document.channel_start, document.channel_count);
    try_interval(bounds, libCZI::DimensionIndex::Z, document.z_start, document.z_count);

    for (const auto dimension : {
             libCZI::DimensionIndex::R,
             libCZI::DimensionIndex::I,
             libCZI::DimensionIndex::H,
             libCZI::DimensionIndex::V,
             libCZI::DimensionIndex::B}) {
        int32_t start{};
        int32_t count{};
        if (try_interval(bounds, dimension, start, count) && count > 1) {
            document.unsupported_dimensions_mask |= 1u << static_cast<uint32_t>(dimension);
        }
    }

    if (document.statistics.sceneBoundingBoxes.empty()) {
        int32_t scene_start{};
        int32_t scene_count{};
        const auto has_scene = try_interval(
            bounds, libCZI::DimensionIndex::S, scene_start, scene_count);
        if (!has_scene) {
            document.scenes.push_back({0, document.statistics.boundingBoxLayer0Only});
        }
        else {
            for (int32_t ordinal = 0; ordinal < scene_count; ++ordinal) {
                document.scenes.push_back({
                    scene_start + ordinal,
                    document.statistics.boundingBoxLayer0Only});
            }
        }
    }
    else {
        for (const auto& [index, boxes] : document.statistics.sceneBoundingBoxes) {
            if (boxes.boundingBoxLayer0.IsNonEmpty()) {
                document.scenes.push_back({index, boxes.boundingBoxLayer0});
            }
        }
        std::sort(document.scenes.begin(), document.scenes.end(), [](const auto& left, const auto& right) {
            return left.index < right.index;
        });
    }
    if (document.scenes.empty()) {
        throw status_error(DSLT_CZI_INVALID_DATA, "CZI has no valid scene bounding rectangle.");
    }

    if (document.statistics.IsMIndexValid() &&
        document.statistics.minMindex != document.statistics.maxMindex) {
        document.flags |= DSLT_CZI_DOCUMENT_HAS_MULTIPLE_TILES;
    }
    const auto pyramids = document.reader->GetPyramidStatistics();
    for (const auto& [scene, layers] : pyramids.scenePyramidStatistics) {
        (void)scene;
        if (std::any_of(layers.begin(), layers.end(), [](const auto& layer) {
                return layer.count > 0 && !layer.layerInfo.IsLayer0();
            })) {
            document.flags |= DSLT_CZI_DOCUMENT_HAS_PYRAMID;
            break;
        }
    }

    document.channels.reserve(static_cast<size_t>(document.channel_count));
    for (int32_t ordinal = 0; ordinal < document.channel_count; ++ordinal) {
        channel_record channel{};
        channel.index = document.channel_start + ordinal;
        channel.name = "Channel " + std::to_string(channel.index);
        libCZI::SubBlockInfo info{};
        if (document.reader->TryGetSubBlockInfoOfArbitrarySubBlockInChannel(channel.index, info)) {
            try {
                channel.voxel_type = map_voxel_type(info.pixelType);
                channel.supported = true;
            }
            catch (const status_error&) {
                channel.supported = false;
            }
        }
        document.channels.push_back(std::move(channel));
    }

    populate_metadata(document);
}

const scene_record& find_scene(const dslt_czi_document& document, const int32_t index) {
    const auto scene = std::find_if(document.scenes.begin(), document.scenes.end(), [index](const auto& item) {
        return item.index == index;
    });
    if (scene == document.scenes.end()) {
        throw status_error(DSLT_CZI_INVALID_ARGUMENT, "Selected CZI scene does not exist.");
    }
    return *scene;
}

const channel_record& find_channel(const dslt_czi_document& document, const int32_t index) {
    const auto channel = std::find_if(document.channels.begin(), document.channels.end(), [index](const auto& item) {
        return item.index == index;
    });
    if (channel == document.channels.end()) {
        throw status_error(DSLT_CZI_INVALID_ARGUMENT, "Selected CZI channel does not exist.");
    }
    return *channel;
}

void set_coordinate_if_present(
    libCZI::CDimCoordinate& coordinate,
    const libCZI::IDimBounds& bounds,
    const libCZI::DimensionIndex dimension,
    const int32_t value) {
    if (bounds.IsValid(dimension)) coordinate.Set(dimension, value);
}

} // namespace

extern "C" {

uint32_t dslt_czi_get_abi_version(void) {
    return DSLT_CZI_ABI_VERSION_V1;
}

int32_t dslt_czi_open_utf8(const char* path_utf8, dslt_czi_document** out_document) {
    return guarded([&] {
        if (out_document == nullptr) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "out_document is null.");
        }
        *out_document = nullptr;
        const auto wide_path = utf8_to_wide(path_utf8);
        if (!std::filesystem::is_regular_file(std::filesystem::path(wide_path))) {
            throw status_error(DSLT_CZI_IO_ERROR, "CZI file does not exist or is not a regular file.");
        }
        auto document = std::make_unique<dslt_czi_document>();
        auto stream = libCZI::CreateStreamFromFile(wide_path.c_str());
        if (!stream) throw status_error(DSLT_CZI_IO_ERROR, "Could not create the CZI input stream.");
        document->reader = libCZI::CreateCZIReader();
        document->reader->Open(stream);
        inspect_document(*document);
        *out_document = document.release();
    });
}

int32_t dslt_czi_close(dslt_czi_document* document) {
    return guarded([&] {
        if (document == nullptr) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI document handle is null.");
        }
        delete document;
    });
}

int32_t dslt_czi_get_document_info(
    const dslt_czi_document* document,
    dslt_czi_document_info_v1* out_info) {
    return guarded([&] {
        if (document == nullptr || out_info == nullptr) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI document or output info is null.");
        }
        require_struct(out_info->struct_size, sizeof(dslt_czi_document_info_v1), "document info");
        const auto struct_size = out_info->struct_size;
        *out_info = {};
        out_info->struct_size = struct_size;
        out_info->abi_version = DSLT_CZI_ABI_VERSION_V1;
        out_info->scene_count = static_cast<int32_t>(document->scenes.size());
        out_info->time_start = document->time_start;
        out_info->time_count = document->time_count;
        out_info->channel_start = document->channel_start;
        out_info->channel_count = document->channel_count;
        out_info->z_start = document->z_start;
        out_info->z_count = document->z_count;
        out_info->document_flags = document->flags;
        out_info->unsupported_dimensions_mask = document->unsupported_dimensions_mask;
        out_info->spacing_flags = document->spacing_flags;
        out_info->spacing_x_um = document->spacing_x_um;
        out_info->spacing_y_um = document->spacing_y_um;
        out_info->spacing_z_um = document->spacing_z_um;
        out_info->subblock_count = static_cast<uint64_t>(document->statistics.subBlockCount);
    });
}

int32_t dslt_czi_get_scene_info(
    const dslt_czi_document* document,
    const int32_t ordinal,
    dslt_czi_scene_info_v1* out_info) {
    return guarded([&] {
        if (document == nullptr || out_info == nullptr || ordinal < 0 ||
            static_cast<size_t>(ordinal) >= document->scenes.size()) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI scene ordinal is invalid.");
        }
        require_struct(out_info->struct_size, sizeof(dslt_czi_scene_info_v1), "scene info");
        const auto size = out_info->struct_size;
        const auto& scene = document->scenes[static_cast<size_t>(ordinal)];
        *out_info = {};
        out_info->struct_size = size;
        out_info->ordinal = ordinal;
        out_info->scene_index = scene.index;
        out_info->x = scene.rectangle.x;
        out_info->y = scene.rectangle.y;
        out_info->width = scene.rectangle.w;
        out_info->height = scene.rectangle.h;
    });
}

int32_t dslt_czi_get_channel_info(
    const dslt_czi_document* document,
    const int32_t channel_index,
    dslt_czi_channel_info_v1* out_info) {
    return guarded([&] {
        if (document == nullptr || out_info == nullptr) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI document or channel output is null.");
        }
        require_struct(out_info->struct_size, sizeof(dslt_czi_channel_info_v1), "channel info");
        const auto size = out_info->struct_size;
        const auto& channel = find_channel(*document, channel_index);
        *out_info = {};
        out_info->struct_size = size;
        out_info->channel_index = channel.index;
        out_info->voxel_type = channel.supported ? channel.voxel_type : 0;
        out_info->red = channel.red;
        out_info->green = channel.green;
        out_info->blue = channel.blue;
        out_info->alpha = channel.alpha;
        out_info->flags = channel.supported ? channel_supported : 0;
    });
}

int32_t dslt_czi_copy_channel_name_utf8(
    const dslt_czi_document* document,
    const int32_t channel_index,
    char* destination,
    const size_t destination_size,
    size_t* required_size) {
    return guarded([&] {
        if (document == nullptr || required_size == nullptr) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI document or required_size is null.");
        }
        const auto& name = find_channel(*document, channel_index).name;
        *required_size = name.size() + 1;
        if (destination == nullptr || destination_size < *required_size) {
            throw status_error(DSLT_CZI_BUFFER_TOO_SMALL, "Channel name buffer is too small.");
        }
        std::memcpy(destination, name.c_str(), *required_size);
    });
}

int32_t dslt_czi_read_volume(
    dslt_czi_document* document,
    const dslt_czi_selection_v1* selection,
    dslt_czi_progress_callback progress,
    void* user_data,
    dslt_czi_volume** out_volume) {
    return guarded([&] {
        if (document == nullptr || selection == nullptr || out_volume == nullptr) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI read argument is null.");
        }
        *out_volume = nullptr;
        require_struct(selection->struct_size, sizeof(dslt_czi_selection_v1), "selection");
        if (selection->channel_indices == nullptr || selection->channel_count == 0) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "At least one CZI channel must be selected.");
        }
        if (document->unsupported_dimensions_mask != 0) {
            throw status_error(DSLT_CZI_UNSUPPORTED, "CZI contains a non-unit unsupported acquisition dimension.");
        }
        if ((document->flags & DSLT_CZI_DOCUMENT_HAS_MULTIPLE_TILES) != 0) {
            throw status_error(DSLT_CZI_UNSUPPORTED, "Mosaic or multi-tile CZI input is not supported in phase one.");
        }
        if (selection->time_index < document->time_start ||
            selection->time_index >= document->time_start + document->time_count) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "Selected CZI time point does not exist.");
        }
        const auto& scene = find_scene(*document, selection->scene_index);
        std::set<int32_t> unique_channels;
        dslt_czi_voxel_type voxel_type{};
        for (size_t ordinal = 0; ordinal < selection->channel_count; ++ordinal) {
            const auto index = selection->channel_indices[ordinal];
            if (!unique_channels.insert(index).second) {
                throw status_error(DSLT_CZI_INVALID_ARGUMENT, "Selected CZI channels must be unique.");
            }
            const auto& channel = find_channel(*document, index);
            if (!channel.supported) {
                throw status_error(DSLT_CZI_UNSUPPORTED, "Selected CZI channel has an unsupported pixel type.");
            }
            if (ordinal == 0) voxel_type = channel.voxel_type;
            else if (voxel_type != channel.voxel_type) {
                throw status_error(DSLT_CZI_UNSUPPORTED, "Selected CZI channels use different pixel types.");
            }
        }

        const auto sample_bytes = bytes_per_sample(voxel_type);
        auto total_bytes = checked_multiply(
            static_cast<uint64_t>(scene.rectangle.w),
            static_cast<uint64_t>(scene.rectangle.h), "CZI plane size");
        total_bytes = checked_multiply(total_bytes, static_cast<uint64_t>(document->z_count), "CZI volume size");
        total_bytes = checked_multiply(total_bytes, static_cast<uint64_t>(selection->channel_count), "CZI channel volume size");
        total_bytes = checked_multiply(total_bytes, sample_bytes, "CZI raw output size");
        if (total_bytes > static_cast<uint64_t>((std::numeric_limits<size_t>::max)())) {
            throw status_error(DSLT_CZI_ALLOCATION_LIMIT, "CZI output exceeds the addressable buffer size.");
        }
        if (selection->maximum_output_bytes != 0 && total_bytes > selection->maximum_output_bytes) {
            throw status_error(DSLT_CZI_ALLOCATION_LIMIT, "CZI output exceeds the caller's allocation limit.");
        }

        auto volume = std::make_unique<dslt_czi_volume>();
        volume->raw_samples.resize(static_cast<size_t>(total_bytes));
        volume->info = {
            sizeof(dslt_czi_volume_info_v1),
            static_cast<uint32_t>(voxel_type),
            scene.rectangle.w,
            scene.rectangle.h,
            document->z_count,
            static_cast<int32_t>(selection->channel_count),
            sample_bytes,
            document->spacing_flags,
            document->spacing_x_um,
            document->spacing_y_um,
            document->spacing_z_um,
            total_bytes};

        const auto plane_bytes = checked_multiply(
            checked_multiply(static_cast<uint64_t>(scene.rectangle.w), static_cast<uint64_t>(scene.rectangle.h), "CZI plane"),
            sample_bytes, "CZI plane bytes");
        const auto plane_count = checked_multiply(
            static_cast<uint64_t>(selection->channel_count), static_cast<uint64_t>(document->z_count), "CZI plane count");
        uint64_t completed_planes = 0;
        if (progress && progress(0.0F, user_data) != 0) {
            throw status_error(DSLT_CZI_CANCELLED, "CZI import was cancelled.");
        }

        for (size_t channel_ordinal = 0; channel_ordinal < selection->channel_count; ++channel_ordinal) {
            const auto channel_index = selection->channel_indices[channel_ordinal];
            for (int32_t z_ordinal = 0; z_ordinal < document->z_count; ++z_ordinal) {
                libCZI::CDimCoordinate coordinate;
                set_coordinate_if_present(coordinate, document->statistics.dimBounds, libCZI::DimensionIndex::S, selection->scene_index);
                set_coordinate_if_present(coordinate, document->statistics.dimBounds, libCZI::DimensionIndex::T, selection->time_index);
                set_coordinate_if_present(coordinate, document->statistics.dimBounds, libCZI::DimensionIndex::C, channel_index);
                set_coordinate_if_present(coordinate, document->statistics.dimBounds, libCZI::DimensionIndex::Z, document->z_start + z_ordinal);
                for (const auto dimension : {
                         libCZI::DimensionIndex::R,
                         libCZI::DimensionIndex::I,
                         libCZI::DimensionIndex::H,
                         libCZI::DimensionIndex::V,
                         libCZI::DimensionIndex::B}) {
                    int32_t start{};
                    int32_t count{};
                    if (try_interval(document->statistics.dimBounds, dimension, start, count)) {
                        coordinate.Set(dimension, start);
                    }
                }

                std::vector<int> subblocks;
                document->reader->EnumSubset(
                    &coordinate, &scene.rectangle, true,
                    [&](const int index, const libCZI::SubBlockInfo&) {
                        subblocks.push_back(index);
                        return subblocks.size() <= 1;
                    });
                if (subblocks.empty()) {
                    throw status_error(DSLT_CZI_INVALID_DATA, "CZI selection contains a missing image plane.");
                }
                if (subblocks.size() != 1) {
                    throw status_error(DSLT_CZI_UNSUPPORTED, "CZI selection contains multiple tiles in one plane.");
                }
                const auto subblock = document->reader->ReadSubBlock(subblocks.front());
                if (!subblock) throw status_error(DSLT_CZI_INVALID_DATA, "CZI image subblock could not be read.");
                const auto& subblock_info = subblock->GetSubBlockInfo();
                if (subblock_info.logicalRect.x != scene.rectangle.x ||
                    subblock_info.logicalRect.y != scene.rectangle.y ||
                    subblock_info.logicalRect.w != scene.rectangle.w ||
                    subblock_info.logicalRect.h != scene.rectangle.h ||
                    subblock_info.physicalSize.w != static_cast<uint32_t>(scene.rectangle.w) ||
                    subblock_info.physicalSize.h != static_cast<uint32_t>(scene.rectangle.h)) {
                    throw status_error(DSLT_CZI_UNSUPPORTED, "CZI plane does not have complete, layer-zero scene coverage.");
                }
                if (map_voxel_type(subblock_info.pixelType) != voxel_type) {
                    throw status_error(DSLT_CZI_INVALID_DATA, "CZI pixel type changes between selected planes.");
                }
                const auto bitmap = subblock->CreateBitmap();
                if (!bitmap || bitmap->GetPixelType() != subblock_info.pixelType) {
                    throw status_error(DSLT_CZI_INVALID_DATA, "CZI decoder returned an unexpected bitmap type.");
                }
                const auto bitmap_size = bitmap->GetSize();
                if (bitmap_size.w != static_cast<uint32_t>(scene.rectangle.w) ||
                    bitmap_size.h != static_cast<uint32_t>(scene.rectangle.h)) {
                    throw status_error(DSLT_CZI_INVALID_DATA, "CZI decoder returned an unexpected bitmap size.");
                }
                const libCZI::ScopedBitmapLockerSP lock(bitmap);
                const auto row_bytes = checked_multiply(
                    static_cast<uint64_t>(scene.rectangle.w), sample_bytes, "CZI row bytes");
                if (lock.ptrDataRoi == nullptr || lock.stride < row_bytes) {
                    throw status_error(DSLT_CZI_INVALID_DATA, "CZI decoder returned an invalid bitmap stride.");
                }
                const auto destination_plane = checked_multiply(
                    static_cast<uint64_t>(channel_ordinal) * static_cast<uint64_t>(document->z_count) +
                        static_cast<uint64_t>(z_ordinal),
                    plane_bytes, "CZI destination plane offset");
                auto* destination = volume->raw_samples.data() + static_cast<size_t>(destination_plane);
                const auto* source = static_cast<const uint8_t*>(lock.ptrDataRoi);
                for (int32_t row = 0; row < scene.rectangle.h; ++row) {
                    std::memcpy(
                        destination + static_cast<size_t>(row) * static_cast<size_t>(row_bytes),
                        source + static_cast<size_t>(row) * lock.stride,
                        static_cast<size_t>(row_bytes));
                }

                ++completed_planes;
                if (progress && progress(static_cast<float>(completed_planes) / static_cast<float>(plane_count), user_data) != 0) {
                    throw status_error(DSLT_CZI_CANCELLED, "CZI import was cancelled.");
                }
            }
        }
        *out_volume = volume.release();
    });
}

int32_t dslt_czi_get_volume_info(
    const dslt_czi_volume* volume,
    dslt_czi_volume_info_v1* out_info) {
    return guarded([&] {
        if (volume == nullptr || out_info == nullptr) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI volume or output info is null.");
        }
        require_struct(out_info->struct_size, sizeof(dslt_czi_volume_info_v1), "volume info");
        *out_info = volume->info;
    });
}

int32_t dslt_czi_copy_raw_samples(
    const dslt_czi_volume* volume,
    void* destination,
    const size_t destination_size) {
    return guarded([&] {
        if (volume == nullptr || destination == nullptr) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI volume or raw destination is null.");
        }
        if (destination_size != volume->raw_samples.size()) {
            throw status_error(DSLT_CZI_BUFFER_TOO_SMALL, "CZI raw destination size does not match the decoded volume.");
        }
        std::memcpy(destination, volume->raw_samples.data(), destination_size);
    });
}

int32_t dslt_czi_release_volume(dslt_czi_volume* volume) {
    return guarded([&] {
        if (volume == nullptr) {
            throw status_error(DSLT_CZI_INVALID_ARGUMENT, "CZI volume handle is null.");
        }
        delete volume;
    });
}

int32_t dslt_czi_copy_last_error_utf8(
    char* destination,
    const size_t destination_size,
    size_t* required_size) {
    if (required_size == nullptr) return DSLT_CZI_INVALID_ARGUMENT;
    *required_size = last_error.size() + 1;
    if (destination == nullptr || destination_size < *required_size) return DSLT_CZI_BUFFER_TOO_SMALL;
    std::memcpy(destination, last_error.c_str(), *required_size);
    return DSLT_CZI_OK;
}

} // extern "C"
