# Source and license

DSLT Workbench is distributed under **GNU GPL version 3 or any later version**.
The complete GPLv3 text is included as `COPYING.GPLv3`. Original third-party
notices retained from DSLT Demo are included as
`LEGACY-THIRD-PARTY-NOTICES.txt`. The redistributed .NET runtime license and
notices are included as `DOTNET-LICENSE.txt` and
`DOTNET-THIRD-PARTY-NOTICES.txt`.

Optional read-only CZI input uses ZEISS libCZI 0.69.1 at pinned revision
`61f74ff097d6d0fbe6e36f204ff59d92e299d7cd` as a dynamically linked library.
libCZI is LGPL-3.0-or-later; binary packages include the upstream
`LIBCZI-COPYING.txt`, `LIBCZI-THIRD-PARTY-LICENSES.txt`, and exact dependency
lock file. The unmodified corresponding source is available from
<https://github.com/ZEISS/libczi> at that revision.
The same package also includes `ZSTD-LICENSE.txt` and all upstream Eigen
`COPYING.*` files used by the pinned libCZI build. zstd 1.5.7 is linked into
libCZI statically; Eigen 3.4.0 headers are pinned by libCZI at commit
`3147391d946bb4b6c68edd901f2add6ac1f31f8c`.

Corresponding source and complete Git history are available at:

- Workbench: <https://github.com/hjung200x/dslt-workbench>
- Original upstream: <https://github.com/takashi310/DSLT>
- Preserved upstream commit: `aae2b3e5310fcaad4151a878ad65ed2a3fa29146`
- Local preservation tag: `legacy-baseline-aae2b3e`

The original source remains at the repository root as the behavioral reference.
New implementation files are isolated under `modern/`. `BUILD-INFO.json` in
each binary package identifies the exact Workbench commit used for that build.

The paper's Appendix S1 identifies the original user manual and states that the
application, source code, sample data, and manual are licensed under GPL3.0. The
official publisher byte stream is preserved as
`modern/legacy/DSLT_Demo_User_Manual_v1.11.pdf`; its provenance and SHA-256 are
recorded in `modern/legacy/README.md` and `docs/legacy-manual-audit.md`.

The software is provided without warranty. Preview packages are
synthetic-data-validated research builds and must not be represented as v1.0
legacy-equivalent releases.

The developer-only `Dslt.Validation.Prepare` tool uses PureHDF 2.1.4 under the
MIT license to read public HDF5 validation fixtures. PureHDF is not included in
the Workbench application package. See <https://github.com/Apollo3zehn/PureHDF>.
