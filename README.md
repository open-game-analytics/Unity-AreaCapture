# Area Capture Plugin

A Unity Package Manager (UPM) compatible plugin that captures 3D areas and exports them as PNG images with JSON metadata.

## Installation

You can add this package to your Unity project using the Unity Package Manager via a Git URL:

1. Open the Unity Package Manager (**Window > Package Manager**).
2. Click the **+** button in the top left corner.
3. Select **"Add package from git URL..."**.
4. Enter the URL of this repository.

## Features

- **CaptureZone Component**: Mark areas for capture by adding this component to GameObjects with a BoxCollider.
- **Editor Export Window**: User-friendly interface to export selected or all capture zones.
- **JSON Metadata**: Exports transform and size information for each captured area with numeric precision.
- **Configurable Output**: Customize capture resolution, background, and export directory.
- **Strict Clipping**: Option to clip objects outside the capture volume.
- **Cubemap Export**: Support for exporting cubemaps from capture zones.
- **Levels of Detail**: Export a zone at several resolutions, split into tiles, for zoomable maps.
- **Free-form Boxes**: Place, scale and rotate (about one axis) capture boxes freely; they may overlap.

## How to Use

### Step 1: Add CaptureZone Component

1. Select a GameObject in your scene (or create a new empty GameObject).
2. Add the `CaptureZone` component via the Inspector.
3. It will automatically add a `BoxCollider` if one isn't present.
4. Configure the capture size (via BoxCollider) and orientation (via the Axis property).

### Step 2: Open Area Capture Window

Go to **Window > Area Capture** in the Unity Editor menu.

### Step 3: Configure Export Settings

- **Pixel Per Unit**: Maximum quality — resolution density of the finest exported image.
- **Lod Levels** (default 4): How many resolutions to export. The finest is Pixel Per Unit; every further level is half the resolution of the previous one.
- **Min Level Pixels** (default 256): Degraded levels whose whole image would be smaller than this are skipped.
- **Tile Pixels** (default 1024): Pixel size of every tile of every level (limited to the GPU texture size).
- **Skip Empty Tiles** (default on): Tiles in which nothing was rendered (fully transparent) are not saved and are left out of the metadata; a level, face or box left without any tile is dropped. A PNG an earlier export left under such a tile's name is deleted once the new metadata is written. Needs a transparent Background Color: with an opaque one no tile is empty. The OGA dashboard draws nothing where a tile is missing.
- **Background Encoding** (default on): Encodes and writes the PNGs on worker threads while the next tile renders, several tiles per editor frame; the Console logs the timing of every export. Turn it off for the original one-tile-per-frame behaviour.
- **Output Directory**: Where to save the exported files.
- **Metadata Filename**: Name of the JSON metadata file.

### Step 4: Export

1. Use the checkboxes in the list to select specific zones, or leave them all unchecked to export everything.
2. Click **"Export"** to render and save files.

### Example Output

Metadata **schema v2** (see the dashboard's `docs/Capture Metadata v2.md`): box → face → LoD level → tiles.

```json
{
	"schema_version": 2,
	"boxes": [
		{
			"id": "Overview",
			"transform": {
				"position": { "x": 0, "y": 0, "z": 0 },
				"rotation": { "x": 0, "y": 0, "z": 0, "w": 1 },
				"size": { "x": 100, "y": 50, "z": 10 }
			},
			"faces": {
				"Front": { "lods": [
					{ "level": 0, "pixels_per_unit": 2, "grid": [1, 1], "tile_size": [200, 200],
					  "images": [
						{ "col": 0, "row": 0, "filename": "Overview_Front_L0_0x0.png", "pixel_size": [200, 100] }
					  ] },
					{ "level": 1, "pixels_per_unit": 4, "grid": [2, 1], "tile_size": [200, 200],
					  "images": [
						{ "col": 0, "row": 0, "filename": "Overview_Front_L1_0x0.png", "pixel_size": [200, 200] },
						{ "col": 1, "row": 0, "filename": "Overview_Front_L1_1x0.png", "pixel_size": [200, 200] }
					  ] }
				] }
			}
		}
	]
}
```

- `size` is the box's **oriented** size (collider size × scale), and `position` its world centre.
- Files are named `{Name}_{Face}_L{level}_{col}x{row}.png`; `row` 0 is the top of the box.
- Every level is cut into tiles of one constant size, **Tile Pixels** (default 1024), anchored at the top-left corner of the box: `tile_size` in the metadata. Only the last column/row of tiles (and a level smaller than one tile) is cropped to the box.
- With **Skip Empty Tiles** a level lists only the tiles in which something was rendered, so `images` can hold fewer than `columns × rows` entries.

## Levels of detail

**Pixel Per Unit** is the maximum quality. **Lod Levels** (a setting of the Area Capture window, applied to every zone) adds smaller versions below it: the finest level renders at `Pixel Per Unit`, and each further level at half the resolution of the one before. With 4 levels at 100 ppu that is 12.5 / 25 / 50 / 100 ppu. Levels are tagged from `L0` (coarsest) up to the finest, and each is cut into tiles of the same pixel size (**Tile Pixels**), so a finer level replaces one tile with four and every tile costs the same to load and stream. A viewer (the OGA dashboard) picks the level that matches its zoom.

**Min Level Pixels** prevents tiny textures: a degraded level is skipped when the longest edge of its whole image would be below this size, so a small zone gets fewer levels than a large one. The finest level is always exported.

A zone can set its own **Pixel Per Unit Override** (its max quality) — e.g. for a hand-placed detail area inside a bigger zone. Zones can overlap and be placed, sized and rotated freely. Note: level tags are per box (every box starts at `L0`, unless Skip Empty Tiles dropped its coarsest levels because nothing showed at their resolution).

**Rotation:** a zone may be rotated about **one** world axis. Only the faces looking along that axis are exported (rotation about Y → `Top`/`Bottom`, Z → `Front`/`Back`, X → `Left`/`Right`); a zone tilted about several axes is skipped with a warning.

## Upgrading from 1.x

The metadata file changed from a flat `{zone}_{Face}` dictionary to schema v2, and the classes `AreaMetadata`/`CaptureMetadata.AddArea` were replaced by `BoxMetadata` & co. Old files still load in the dashboard (it converts them), but new exports are v2 only. Image files are now named `{Name}_{Face}_L{level}_{col}x{row}.png`.

## Namespace

- Core classes: `AreaCapture` (`CaptureZone`, `CapturePlanner`, `CaptureMetadata`)
- Runtime capture: `AreaCapture.Runtime` (`RuntimeAreaCapture`)
- Editor classes: `AreaCapture.Editor`

## Components

### CaptureZone
Marks a volume for capture.

**Properties:**
- `Axis`: Direction from which the area is captured.
- `Export Cubemap`: Toggle to export all six faces instead of one.
- `Filename Override`: Optional custom name (box id and file prefix).
- `Use Strict Clipping`: If enabled, only objects inside the volume are rendered.
- `Depth Trim`: World units cut off the capture side of the box, like a section cut. Set it slightly thicker than a roof/ceiling to see inside rooms (0 = off).
- `Pixel Per Unit Override`: Max-quality resolution for this zone (0 = use the window's Pixel Per Unit). The number of LoD levels is a window setting (see above).
- `Show Gizmo`: Display the capture volume in the scene view (labelled with its name).

## Tips

- **Better Quality**: Increase "Pixel Per Unit" (the maximum). "Lod Levels" only adds smaller versions below it.
- **Large Areas**: No need to lower the resolution — areas larger than one tile are tiled automatically; "Tile Pixels" (default 1024) sets the size of every tile, smaller tiles stream in finer steps but mean more files.
- **Filenames**: Use the "Filename Override" in the CaptureZone inspector for specific naming requirements.
- **Selective Export**: Use the checkboxes in the Area Capture window to only re-export changed areas.
