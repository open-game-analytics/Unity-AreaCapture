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

- **Max Pixel Per Unit**: Maximum quality — resolution density of the finest exported image. The LoD levels below it are derived automatically.
- **Min Pixel Per Unit** (default 0 = no floor): The lowest resolution a LoD level may have. The ladder halves the resolution from the max until a level's whole image fits in one tile (that is the overview) or the next level would fall below this.
- **Overview + Best Only** (default off): Export just the coarsest and the finest level of every face — a fast preview of the worst and best LoD. Level numbers stay the real ones, so a full export later fills in the rest.
- **Output Directory**: Where to save the exported files (default `Assets/Exports~/AreaCaptures`). Unity ignores folders ending in `~`, so the PNGs are not imported (no `.meta` files, no import time); the window warns if you pick a folder Unity would import. A path outside the project works too.
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

## Levels of detail

**Max Pixel Per Unit** is the maximum quality, and the program derives the smaller versions below it (applied to every zone): the finest level renders at the max, and each further level at half the resolution of the one before, until the whole image of a level fits in a single tile (**Tile Pixels**), which is the overview and the last (coarsest) level. A 20 × 10 unit zone at 100 ppu with 512 px tiles gets 25 / 50 / 100 ppu. Levels are tagged from `L0` (coarsest) up to the finest, and each is cut into tiles of the same pixel size, so a finer level replaces one tile with four and every tile costs the same to load and stream. A viewer (the OGA dashboard) picks the level that matches its zoom.

**Min Pixel Per Unit** sets a floor: the ladder stops before going below it, even if the box does not fit one tile yet, so no needlessly low-resolution textures are written. The finest level is always exported, so a small zone gets fewer levels than a large one.

A zone can set its own **Pixel Per Unit Override** (its max quality) — e.g. for a hand-placed detail area inside a bigger zone. Zones can overlap and be placed, sized and rotated freely. Note: level tags are per box (every box starts at `L0`).

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
- `Pixel Per Unit Override`: Max-quality resolution for this zone (0 = use the window's Pixel Per Unit). The LoD levels are derived from the window's Min Pixel Per Unit and Tile Pixels (see above).
- `Show Gizmo`: Display the capture volume in the scene view (labelled with its name).

## Tips

- **Better Quality**: Increase "Max Pixel Per Unit". The levels below it are derived and only add smaller versions.
- **Large Areas**: No need to lower the resolution — areas larger than one tile are tiled automatically; "Tile Pixels" (default 1024) sets the size of every tile, smaller tiles stream in finer steps but mean more files.
- **Filenames**: Use the "Filename Override" in the CaptureZone inspector for specific naming requirements.
- **Selective Export**: Use the checkboxes in the Area Capture window to only re-export changed areas.
