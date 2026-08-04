# WPF workflow contract

The Workbench UI follows the research workflow `inspect -> process -> segment
-> edit -> export`. This document describes the application behavior that is
implemented and automatically tested. It does not claim legacy-data
equivalence; that remains gated by representative datasets.

## Implemented workflow shell

- Open a multi-page TIFF or ImageJ hyperstack without replacing the current
  volume when decoding fails.
- Generate a deterministic synthetic volume for installation and backend
  checks.
- Show the main window before native/CUDA discovery completes, initialize the
  processing engine off the UI thread, and replace the temporary status model
  only while the window is still active.
- Select a channel by preserved source name when available (or a deterministic
  numeric fallback) and use shared X/Y/Z coordinates with bounds derived from
  the active volume.
- Keep navigation/volume and processing/editing controls independently
  scrollable so the 900 x 500 logical minimum remains usable on a 1920 x 1080
  display through 200% scaling.
- Render synchronized XY, YZ, and ZX source/result planes, apply one shared zoom
  factor, and propagate normalized scroll offsets across all six views.
- Adjust the display window independently of stored voxel values.
- Choose the processing backend and an exposed CPU-compatible operation.
- Configure threshold, radius, connectivity, component-size, non-DSLT
  threshold-sweep, and DSLT sweep parameters according to the selected
  operation.
- Keep general filter radius separate from the DSLT radius. DSLT starts at the
  legacy UI default of 14 and is bounded by the legacy UI maximum of 100.
- Present the preview offset on the legacy 0..200 scale with default 20 and map
  it explicitly to core `Cxy = -offset * 0.002`.
- Calculate DSLT work and memory estimates before execution. Requests outside
  native safety limits are rejected before processing.
- Run long operations asynchronously with progress and an enabled cancellation
  command.
- Optionally compose Z height projection as legacy HSV depth color with default
  range 100. The scalar projection, height map, and depth map come from the
  selected CPU/CUDA backend; the deterministic RGB24 display remains separate
  from the exported scalar payload.
- Run both 2D and 3D global thresholding, cube or sphere morphology, and Z
  area-average or Lanczos 2/3 resampling from the operation selector. The 2D
  threshold uses the shared Z cursor; resampling defaults target Z spacing to
  the input X spacing and records target spacing/order in provenance.
- `Copy` remains an internal ABI operation, while `ExtractXy/Yz/Zx` back the
  always-visible synchronized orthogonal views instead of duplicating them in
  the operation selector.
- Preserve the last valid result and source volume after cancellation or
  processing failure, including failures or cancellation during auxiliary
  height/depth requests for RGB presentation.
- Advance label results to the edit stage and export the result with its JSON
  provenance sidecar.
- Select the label at the shared cursor, optionally add labels to the selection,
  select all, select/deselect by strict active-channel mean threshold, and merge,
  split, crop, dilate, erode with optional image-edge clamp, clear, or undo
  through `LabelEditingSession`.
  Selected labels are highlighted consistently in all result planes.
- Preserve the manual's Ctrl+O/Ctrl+S, Alt+J/Alt+S, Ctrl+Z and Alt+A/Alt+D
  bindings. Shift-left/right-click zooms all synchronized views; middle-click
  maps a rendered result-plane pixel to its XY/YZ/ZX source coordinate and
  selects that label without accepting clicks in image letterboxing.
- Treat crop dimensions, source-coordinate origin, labels, and selection as one
  undoable transaction. Shared source/result coordinates remain meaningful
  after crop.
- Run Watershed only when one or more full-volume labels are selected. The
  current labels are passed as seeds, the result is installed as an undoable
  label edit, and cancellation or failure preserves the seed result.
- Store successful label-edit actions and structured output origin fields in
  provenance schema 1.9 so an exported result records its source commit, ordered prior processing steps, and distinguishes processing output
  from subsequent manual edits and records Watershed seed hash/selection.

## Automated state checks

`Dslt.App.Tests` verifies the following on an STA thread:

1. operation selection activates the matching workflow stage;
2. DSLT resource estimates are requested and shown;
3. a successful label operation publishes an image and advances to editing;
4. the UI cancellation command cancels an in-flight token while retaining the
   previous valid result;
5. processing failure retains the previous valid result;
6. opening a multi-channel volume updates named channel and Z navigation,
   preserves source RGBA/timestamp metadata, and changing the channel refreshes
   the source image;
7. YZ and ZX source/result plane dimensions follow the volume axes;
8. cursor selection, label dilation, and undo update and restore the published
   result;
9. ViewModel export writes label-edit history into the JSON provenance sidecar.
10. normalized horizontal and vertical offsets propagate between differently
    sized WPF viewports without feedback recursion.
11. crop and undo restore dimensions, source-coordinate origin, and labels
    bit-exactly, and exported provenance retains the cropped output origin.
12. Watershed is enabled only with a selected full-volume seed, passes seed
    state to the engine, records seed provenance, and remains undoable.
13. the real `Application.Run()` startup path creates and loads the main window,
    runs in a `PerMonitorV2` DPI-awareness context, keeps the read-only progress
    property on a one-way binding, and gives every slider, combo box, text box,
    list, progress indicator, and scrollable view an explicit UI Automation
    name.
14. a 512 x 512 x 64 float volume publishes all navigation bounds, updates the
    active Z plane without a two-second caller stall, exposes cancellation while
    work is in flight, returns from the cancel command within one second, and
    preserves the source view and last valid result state.
15. the 900 x 500 logical minimum fits within 1920 x 1080 at 125%, 150%, and
    200% scaling, both control columns expose vertical scrolling, and Open, Run,
    Cancel, and Export can each be scrolled into the live window.
16. a file-backed 512 x 512 x 32 Gray16 multipage TIFF decodes within the
    30-second gate while preserving dimensions, voxel type, 16 MiB of canonical
    source bytes, page/voxel order, and normalized maximum intensity.
17. Z-only depth coloring preserves default/range parameters in provenance,
    runs scalar projection, height map, and depth map in order, publishes one
    RGB24 XY image, and matches the legacy HSV byte oracle. The native-CPU job
    repeats the composition with actual C ABI outputs.
18. every user-facing native operation is present in the WPF selector; 2D
    threshold preserves the active Z slice, cube morphology preserves radius,
    and area/Lanczos Z resampling preserves target spacing, order, output depth,
    orthogonal result geometry, cancellation state, and Float32 provenance.
19. active-channel mean-intensity selection/deselection, select-all, image-edge
    clamp semantics, and uniform-image pointer mapping match the manual-derived
    interaction contract.

The same test executable retains the bit-exact TIFF type, ImageJ page-order,
calibration, and metadata fixtures.

## Current interactive smoke evidence

On 2026-08-04, a locally published self-contained `win-x64` build was launched
with the hosted CUDA artifact previously validated on the same RTX 4060. The
window loaded at 1500 x 920 on a 96-DPI Windows host, reported `CPU + CUDA` and
the RTX 4060 device, ran with `PerMonitorV2=True`, exposed 107 UI Automation
descendants, and had zero keyboard-focusable elements without an accessible
name. The app then accepted a normal window-close request and exited. This is
one-host startup evidence, not completion of the dual-OS and multi-DPI release
gate.

The responsive minimum was also exercised as a self-contained build at 900 x
500 device-independent units. It retained `PerMonitorV2=True`, exposed 144 UI Automation
descendants with zero unnamed keyboard-focusable elements, and UI Automation
successfully scrolled Open, Run, Cancel, and Export into view independently.

## Remaining UI work

- Perform the remaining interactive cancellation, recovery, non-100%-DPI, and
  representative file-backed large-volume checks on Windows 10 22H2 and
  Windows 11.
