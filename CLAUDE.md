# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

This is a Unity Package Manager (UPM) package (`com.komatrich.area-capture`) for capturing screenshots of designated 3D zones in a Unity scene and exporting them as PNG images (several levels of detail, tiled) with accompanying JSON metadata (schema v2). It is an **editor-only tool** — users place `CaptureZone` components in their scene, open the `Window > Area Capture` editor window, configure settings, and click export.

## Development

This is a Unity package with no CLI build system. Development happens inside a Unity project that has this package installed (via git URL or local path). There are no test suites or lint scripts.

To install locally during development: add the package via **Package Manager > Add package from disk** and point to `package.json`, or use the git URL.

Optional dependency: **NaughtyAttributes** (`com.dbrizov.naughtyattributes`) — detected at compile time via `NAUGHTY_ATTRIBUTES` scripting define in the runtime asmdef. Code using NaughtyAttributes attributes must be wrapped in `#if NAUGHTY_ATTRIBUTES` guards. It is deliberately **not** listed in `package.json` `dependencies` (UPM rejects git-URL dependencies there, which breaks `file:` installs); add `"com.dbrizov.naughtyattributes": "https://github.com/dbrizov/NaughtyAttributes.git#upm"` to the consuming project's `manifest.json` instead.

## Architecture

### Assembly split

| Assembly | Folder | Available |
|---|---|---|
| `Com.Komatrich.AreaCapture` | `Runtime/` | Runtime + Editor |
| `Com.Komatrich.AreaCapture.Editor` | `Editor/` | Editor only |

### Data flow

```
AreaCaptureWindow (UI, EditorPrefs persistence)
  └─ AreaCaptureExporter.ExportZones(zones[], ExportSettings)
       ├─ BuildPlan(): zones → CapturePlanner.PlanFace() → flat list of tile jobs + CaptureMetadata (v2)
       └─ per job (one per editor frame):
            RuntimeAreaCapture.CaptureTile(zone, TileJob)
              └─ Internal orthographic Camera → RenderTexture → Texture2D
            TileCoverage.IsEmpty(texture)? skip (SkipEmptyTiles, default on) : File.WriteAllBytes(path, texture.EncodeToPNG())
       └─ metadata.RemoveImages(skipped) → CaptureMetadataJson.Serialize(metadata) → File.WriteAllText
       └─ delete PNGs (+ .meta) an earlier export left under the skipped names
```

The exporter runs asynchronously via `EditorApplication.update` (state machine over the precomputed job list) to keep the editor responsive and show a cancelable progress bar. With `ExportSettings.BackgroundEncoding` (default on) it renders several tiles per frame (50 ms budget) and hands each tile's raw pixels to `TileWriteQueue` (≤3 in flight), where the empty check, `PngWriter.EncodeRgba` (pure C#, thread-safe, unlike `EncodeToPNG`) and the file write run on the thread pool; the queue is drained before the metadata JSON is written. Off = the original one-tile-per-frame main-thread path. Only one export runs at a time (they share the GPU, the camera and the progress bar), and each finished export logs its timing to the Console.

### Key classes

- **`CapturePlanner`** (`Runtime/CapturePlan.cs`) — **Pure C#, no UnityEngine.** The rules: rotation classification (`ClassifyRotation`, `FaceSupportsRotation`), which world axes a face shows (`FaceExtents`), the tile grid (`FinestPixels` / `LevelPixels` / `GridFor`: constant-size tiles anchored at the top-left, cropped only at the right/bottom edge, image sizes derived from the finest level so the grids nest), `LevelPixelsPerUnit` (the LoD ladder: max ppu halved per level until the whole face fits in one tile or the next level would fall below the min ppu; tagged L0 = coarsest), `LevelTags` (all levels, or only the coarsest and finest for the preview, keeping their real tags), and `PlanFace` (levels → `TileJob`s with offsets and pixel sizes). Also defines `CaptureFace`, `RotationAxis`, `TileJob`.
- **`CaptureZone`** (`Runtime/CaptureZone.cs`) — `MonoBehaviour` + required `BoxCollider`. One free-form box: axis, cubemap flag, strict clipping, filename override, and an optional `Pixel Per Unit Override` (its max-quality ppu). The LoD ladder and the "overview + best only" preview are **global** export settings (`ExportSettings.MinPixelPerUnit` default 0 / `ExtremesOnly` default off), not per zone. `WorldCenter` / `OrientedSize` (collider size × lossy scale — **not** the world AABB) / `RotationAbout` give the true oriented box. Gizmo drawn in `OnDrawGizmos`.
- **`RuntimeAreaCapture`** (`Runtime/RuntimeAreaCapture.cs`) — Stateful renderer. Owns a hidden internal camera. `CaptureTile()` places the camera on the face along the zone's own axes, shifts it in the image plane to the tile centre, renders to a RenderTexture and reads back a `Texture2D`. `CaptureArea()` is a single-image convenience wrapper.
- **`AreaCaptureExporter`** (`Editor/AreaCaptureExporter.cs`) — Static export pipeline. `BuildPlan` is separated from rendering so counts/warnings need no rendering (`CountImages` is the cheap variant for GUI repaints). Files: `{Name}_{Face}_L{level}_{col}x{row}.png`. The default output dir is `Assets/Exports~/AreaCaptures` (`DEFAULT_OUTDIR`): a trailing `~` makes Unity's AssetDatabase ignore the folder, so the (many) PNGs are never imported. `WouldBeImportedByUnity` drives the window's warning for dirs under `Assets/`/`Packages/` without such a segment.
- **`TileCoverage`** (`Runtime/TileCoverage.cs`) — Pure C#. `IsEmpty(rgba)` is true when every alpha byte is 0. The exporter skips such tiles (`ExportSettings.SkipEmptyTiles`, window toggle, on by default) and `CaptureMetadata.RemoveImages` leaves them out of the metadata, dropping levels/faces/boxes that end up with no tile. Needs a transparent background; the dashboard accepts levels with missing tiles.
- **`CaptureMetadata` & co.** (`Runtime/CaptureMetadata.cs`) — Pure C# schema v2 model (`BoxMetadata` → `FaceMetadata` → `LodMetadata` → `ImageMetadata`) and `CaptureMetadataJson`, a hand-written culture-invariant JSON writer (4 dp for position/size, 6 dp for the quaternion).
- **`AreaCaptureWindow`** (`Editor/AreaCaptureWindow.cs`) — `EditorWindow` opened via `Window > Area Capture`. All settings persisted in `EditorPrefs`.

The metadata contract is documented in the dashboard repo: `Dashboard/docs/Capture Metadata v2.md`. The Godot addon implements the same rules in GDScript.

### Testing without Unity

`CapturePlan.cs`, `CaptureMetadata.cs`, `TileCoverage.cs` and `PngWriter.cs` compile without UnityEngine. `Tests~/CapturePlanCheck` compiles them into a plain .NET console app (`nix shell nixpkgs#dotnet-sdk_8 --command dotnet run`) to test the planner, the JSON writer, the empty-tile rule and the PNG encoder (the tile grid and JSON for the shared demo dataset in `Dashboard/oga-dashboard/tests/fixtures/capture-lod-demo/` must match the dashboard generator's output).

### Camera setup (RuntimeAreaCapture)

- Orthographic projection framing exactly one tile: size = half the tile height, aspect = tile width / height.
- The camera sits on the face's side of the box, 10 units back from the face along the zone's own axis, looking at it; then it is moved along the camera's right/up vectors to the tile centre.
- Zone axes come from the transform, sizes from `OrientedSize`, so a zone rotated about one axis is captured aligned to itself.
- **Normal clipping**: near = 0.3, far = 1000
- **Strict clipping**: near = 10, far = 10 + depth of zone along capture axis (clips to exact volume bounds)
- **Depth trim** (`CaptureZone.DepthTrim`): when > 0, near = 10 + trim (clamped to the zone depth), a section cut that removes ceilings/roofs; applies in both clipping modes.
- Level ppu: the finest level is the max ppu (zone override, else the window's Pixel Per Unit); level `k` steps down halve it until the whole face fits one tile or `Min Pixel Per Unit` is reached. Level tags are per box, L0 = coarsest.
- Every tile is `Tile Pixels` square (≤ `SystemInfo.maxTextureSize`) except the last column/row and levels smaller than one tile, which are cropped to the box (the camera frames exactly `pixel size / ppu` world units from the tile's top-left corner). A finer level replaces each tile with four; `LodMetadata.TilePixels` is written as `tile_size`.

### CaptureAxis enum

Six values (`PositiveX`, `NegativeX`, `PositiveY`, `NegativeY`, `PositiveZ`, `NegativeZ`) map to camera look directions (the camera sits on that side looking inward). `CaptureAxisExtensions.ToFace()` converts to the metadata face name: Front = -Z, Back = +Z, Left = -X, Right = +X, Top = +Y, Bottom = -Y.
