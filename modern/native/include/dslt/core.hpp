#ifndef DSLT_CORE_HPP
#define DSLT_CORE_HPP

#include "dslt/c_api.h"

#include <cstddef>
#include <cstdint>
#include <functional>
#include <span>
#include <string>
#include <unordered_set>
#include <vector>

namespace dslt {

class Volume final {
public:
    Volume() = default;
    Volume(dslt_volume_descriptor descriptor, std::span<const float> samples);

    [[nodiscard]] bool empty() const noexcept { return data_.empty(); }
    [[nodiscard]] const dslt_volume_descriptor& descriptor() const noexcept { return descriptor_; }
    [[nodiscard]] std::span<const float> data() const noexcept { return data_; }
    [[nodiscard]] std::span<float> data() noexcept { return data_; }
    [[nodiscard]] std::size_t voxel_count() const noexcept;
    [[nodiscard]] std::size_t index(std::size_t x, std::size_t y, std::size_t z) const;

private:
    dslt_volume_descriptor descriptor_{};
    std::vector<float> data_;
};

struct BackendState final {
    bool compiled{};
    bool available{};
    std::uint64_t device_memory_bytes{};
    std::string device_name;
};

BackendState query_cuda_backend() noexcept;

class Engine final {
public:
    using Progress = std::function<bool(float)>;

    void set_volume(const dslt_volume_descriptor& descriptor, std::span<const float> samples);
    [[nodiscard]] const Volume& volume() const noexcept { return source_; }
    [[nodiscard]] const std::vector<float>& output() const noexcept { return output_; }
    [[nodiscard]] const std::vector<std::int32_t>& labels() const noexcept { return labels_; }
    [[nodiscard]] const dslt_operation_result& result() const noexcept { return result_; }
    [[nodiscard]] BackendState cuda_state() const noexcept { return query_cuda_backend(); }

    dslt_status run(const dslt_operation_request& request, const Progress& progress);
    void set_error(std::string value) { last_error_ = std::move(value); }
    [[nodiscard]] const std::string& last_error() const noexcept { return last_error_; }

private:
    Volume source_;
    std::vector<float> output_;
    std::vector<std::int32_t> labels_;
    std::unordered_set<std::int32_t> selection_;
    dslt_operation_result result_{};
    std::string last_error_;
};

std::size_t checked_element_count(const dslt_volume_descriptor& descriptor);

} // namespace dslt

#endif
