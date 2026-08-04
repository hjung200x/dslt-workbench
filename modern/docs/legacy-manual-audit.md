# DSLT Demo 1.11 manual audit

## Preserved evidence

The original paper identifies its Appendix S1 as the DSLT_Demo user manual and
states that the application, source code, sample data, and manual are under the
GPL3.0 license. The official 2,192,662-byte supporting-information file is
preserved byte-for-byte at
`modern/legacy/DSLT_Demo_User_Manual_v1.11.pdf`; its SHA-256 is
`aa2c21a6fe3aafc7a431d117aed4ede66b6878f807304805d73ffea602392014`.

Sources:

- <https://doi.org/10.1111/tpj.12738>
- Publisher filename: `tpj12738-sup-0005-DocumentaionD1.pdf`
- Legacy site filename: `DSLT_manual_v111.pdf`

The PDF has 10 pages and identifies itself as the DSLT_Demo version 1.11 user
manual. Every page was rendered and inspected; text extraction was used only as
a secondary aid.

## Page-by-page contract

| Pages | Confirmed behavior | Workbench mapping |
|---|---|---|
| 1-2 | Source and destination orthogonal views, position/channel/brightness/height-map/segment controls, shared view splitter, Shift-click zoom | Orthogonal views, synchronized coordinates/zoom/scroll, channel selection, window/level, and Shift-left/right zoom are implemented |
| 3 | 8/16/32-bit TIFF stacks and ImageJ TIFF HyperStacks; channel 1 is the usual segmentation channel; height-map mean/Gaussian filter, radius, smoothing level, Z radius, threshold, offset and projection range | TIFF/HyperStack, channel selection, height map, and height projection are implemented or explicitly reference-pending in the compatibility matrix |
| 4 | The manual prints the simplified depth-dependent brightness formula `Inew = coefficient * z^exp * I`; mean/Gaussian smoothing; apply adjustments to every channel | The source-authoritative `applyBC` formula, optional height-map-relative depth, range adjustment, window/level, and smoothing are implemented; per-channel application is achieved by selecting and processing each channel |
| 5 | DSLT mean/Gaussian kernel, radius, level, Z correction alpha, min/max C, interval, validation threshold and closing; fixed or height-map crop bounds and XY edge | DSLT threshold/segmentation, validation, closing, and fixed/height-map crop are implemented with synthetic/CUDA validation; runtime oracle remains pending |
| 6 | Segment erosion/dilation, optional clamp, middle-click select, Alt+J merge, Alt+S split, Ctrl+Z undo, Alt+A select all and Alt+D deselect all | Edit algorithms, image-edge clamp, keyboard bindings and result-plane middle-click selection are implemented |
| 7 | Select/deselect segments by their mean intensity in the active channel | Strict active-channel mean-threshold selection/deselection is implemented, including cropped result origin mapping |
| 8 | Watershed ignores unselected/hidden segments, optional minimum size, morphologic smoothing recipe, save result and save segments | Selected-seed watershed, minimum size, morphology, segment TIFF import/export, and the strict minimum-size display filter are implemented |
| 9-10 | Qualitative effects of max C, min C and ValidTH; segment validation based on extracted inner structures | Parameter semantics and validation order are fixed in `dslt-algorithm-spec.md`; legacy-runtime comparison remains pending |

## Defaults confirmed by the manual screenshots

The following values are evidence from the version 1.11 manual rather than
assumptions inferred from malformed legacy slider declarations:

- Height map: Gaussian, radius 64, smoothing level 1, Z radius 4, threshold 0.1.
- Brightness Z gradient: coefficient 10, exponent 1.
- Smoothing example: Gaussian, radius 2.
- DSLT segmentation: Gaussian, radius 14, level 2, Z correction 0.2,
  max C 10, min C 4, interval -1, ValidTH 800, closing radius 2.
- Segment erosion/dilation example: radius 3; erosion clamp enabled.
- Segment selection threshold example: 0.1.
- Segment display minimum size example: 100.

These are manual examples and observed UI values, not universal recommended
settings. Conflicts with source control ranges or code-behind sign conversion
remain `capture-required` until the archived executable can be observed.

## Source-only command audit

The root XAML binds unmodified S/D to Source/Result XY/YZ/ZX TIFF snapshots;
Workbench implements those active commands and their three-file naming. H/J
actively read/write the binary 120/240-header `.hmp` surface format, and A
actively calculates height-surface area maps; those remain tracked gaps. C/V/M/N
are observable no-ops at the pinned baseline because all calls in their command
bodies are commented out, so Workbench excludes them rather than inventing new
behavior.

## Explicit remaining gaps

The audit prevents implemented algorithms from being mistaken for complete
legacy interaction equivalence. Before a v1.0 claim, the Workbench still needs:

1. a real or archived-runtime oracle for ambiguous parameter mappings;
2. legacy `.hmp` height-map import/export and external-surface processing;
3. a deterministic replacement for the legacy height-surface area-map command;
4. representative original or expert-labelled microscopy data.
