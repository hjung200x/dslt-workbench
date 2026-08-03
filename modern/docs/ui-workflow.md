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
- Select a channel and XY Z-slice with bounds derived from the active volume.
- Adjust the display window independently of stored voxel values.
- Choose the processing backend and an exposed CPU-compatible operation.
- Configure threshold, radius, connectivity, component-size, and DSLT sweep
  parameters according to the selected operation.
- Keep general filter radius separate from the DSLT radius. DSLT starts at the
  legacy UI default of 14 and is bounded by the legacy UI maximum of 100.
- Present the preview offset on the legacy 0..200 scale with default 20 and map
  it explicitly to core `Cxy = -offset * 0.002`.
- Calculate DSLT work and memory estimates before execution. Requests outside
  native safety limits are rejected before processing.
- Run long operations asynchronously with progress and an enabled cancellation
  command.
- Preserve the last valid result and source volume after cancellation or
  processing failure.
- Advance label results to the edit stage and export the result with its JSON
  provenance sidecar.

## Automated state checks

`Dslt.App.Tests` verifies the following on an STA thread:

1. operation selection activates the matching workflow stage;
2. DSLT resource estimates are requested and shown;
3. a successful label operation publishes an image and advances to editing;
4. the UI cancellation command cancels an in-flight token while retaining the
   previous valid result;
5. processing failure retains the previous valid result;
6. opening a multi-channel volume updates channel and Z navigation and changing
   the channel refreshes the source image.

The same test executable retains the bit-exact TIFF type, ImageJ page-order,
calibration, and metadata fixtures.

## Remaining UI work

- Render synchronized XY, YZ, and ZX source/destination views with shared
  coordinates, zoom, and scroll state.
- Bind `LabelEditingSession` selection, merge, split, crop, dilate, erode, and
  undo operations to the edit stage.
- Add keyboard shortcuts compatible with the legacy editing workflow.
- Add end-to-end package export tests using the UI ViewModel and a temporary
  destination.
- Perform interactive accessibility, DPI, and large-volume responsiveness
  checks on Windows 10 22H2 and Windows 11.
