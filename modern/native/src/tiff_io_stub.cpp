// TIFF/LSM I/O is intentionally kept out of the stable ABI until libtiff
// metadata round-trip fixtures are available. In-memory volume processing is
// fully usable without an image I/O dependency.
namespace dslt::tiff_io {
constexpr bool available = false;
}

