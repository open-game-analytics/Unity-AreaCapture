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

- **Pixel Per Unit**: Resolution density of the exported images.
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
					{ "level": 0, "pixels_per_unit": 2, "grid": [1, 1],
					  "images": [
						{ "col": 0, "row": 0, "filename": "Overview_Front_L0_0x0.png", "pixel_size": [200, 100] }
					  ] },
					{ "level": 1, "pixels_per_unit": 4, "grid": [2, 1],
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
- Every PNG is at most **Max Tile Pixels** wide/tall; larger areas are split into a tile grid.

## Levels of detail

Set **Lod Levels** on a zone to export it at several resolutions: level *i* is rendered at `Pixel Per Unit × 2^i` and split into as many tiles as needed. A viewer (the OGA dashboard) picks the level that matches its zoom.

For a hand-placed detail area inside a bigger zone, add a second `CaptureZone` with a higher **First Level** and a higher **Pixel Per Unit Override**. Zones can overlap and be placed, sized and rotated freely.

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
- `Lod Levels` / `First Level` / `Pixel Per Unit Override`: level-of-detail settings (see above).
- `Show Gizmo`: Display the capture volume in the scene view (labelled with its LoD levels).

## Tips

- **Better Quality**: Increase "Pixel Per Unit" or "Lod Levels".
- **Large Areas**: No need to lower the resolution — areas larger than "Max Tile Pixels" are tiled automatically.
- **Filenames**: Use the "Filename Override" in the CaptureZone inspector for specific naming requirements.
- **Selective Export**: Use the checkboxes in the Area Capture window to only re-export changed areas.
