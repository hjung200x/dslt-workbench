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
- Select a channel and shared X/Y/Z coordinates with bounds derived from the
  active volume.
- Render synchronized XY, YZ, and ZX source/result planes, apply one shared zoom
  factor, and propagate normalized scroll offsets across all six views.
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
- Select the label at the shared cursor, optionally add labels to the selection,
  and merge, split, crop, dilate, erode, clear, or undo through
  `LabelEditingSession`.
  Selected labels are highlighted consistently in all result planes.
- Treat crop dimensions, source-coordinate origin, labels, and selection as one
  undoable transaction. Shared source/result coordinates remain meaningful
  after crop.
- Store successful label-edit actions and structured output origin fields in
  provenance schema 1.3 so an exported result distinguishes processing output
  from subsequent manual edits.

## Automated state checks

`Dslt.App.Tests` verifies the following on an STA thread:

1. operation selection activates the matching workflow stage;
2. DSLT resource estimates are requested and shown;
3. a successful label operation publishes an image and advances to editing;
4. the UI cancellation command cancels an in-flight token while retaining the
   previous valid result;
5. processing failure retains the previous valid result;
6. opening a multi-channel volume updates channel and Z navigation and changing
   the channel refreshes the source image;
7. YZ and ZX source/result plane dimensions follow the volume axes;
8. cursor selection, label dilation, and undo update and restore the published
   result;
9. ViewModel export writes label-edit history into the JSON provenance sidecar.
10. normalized horizontal and vertical offsets propagate between differently
    sized WPF viewports without feedback recursion.
11. crop and undo restore dimensions, source-coordinate origin, and labels
    bit-exactly, and exported provenance retains the cropped output origin.

The same test executable retains the bit-exact TIFF type, ImageJ page-order,
calibration, and metadata fixtures.

## Remaining UI work

- Recover and add the remaining legacy editing shortcuts. `Ctrl+Z` is currently
  bound to undo; shortcut behavior without source evidence is not guessed.
- Perform interactive accessibility, DPI, and large-volume responsiveness
  checks on Windows 10 22H2 and Windows 11.
