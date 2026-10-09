using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System.Collections.Generic;

namespace AreaCapture.Editor
{
    /// <summary>
    /// Editor window for capturing and exporting 2D areas
    /// </summary>
    public class AreaCaptureWindow : EditorWindow
    {
        private AreaCaptureExporter.ExportSettings settings;

        [MenuItem("Window/Area Capture")]
        public static void ShowWindow()
        {
            GetWindow<AreaCaptureWindow>("Area Capture");
        }

        private void OnEnable()
        {
            settings = AreaCaptureExporter.LoadSettingsFromPrefs();
        }

        private void OnDisable()
        {
            if (settings != null)
            {
                EditorPrefs.SetInt(AreaCaptureExporter.PREF_KEY_PPU, settings.PixelPerUnit);
                EditorPrefs.SetString(AreaCaptureExporter.PREF_KEY_OUTDIR, settings.OutputDirectory);
                EditorPrefs.SetString(AreaCaptureExporter.PREF_KEY_META, settings.MetadataFilename);
                EditorPrefs.SetInt(AreaCaptureExporter.PREF_KEY_CLEARFLAG, (int)settings.ClearFlags);
                EditorPrefs.SetInt(AreaCaptureExporter.PREF_KEY_CULLMASK, settings.CullingMask);
                EditorPrefs.SetString(AreaCaptureExporter.PREF_KEY_BGCOLOR, "#" + ColorUtility.ToHtmlStringRGBA(settings.BackgroundColor));
                EditorPrefs.SetInt(AreaCaptureExporter.PREF_KEY_TILE, settings.TilePixels);
                EditorPrefs.SetInt(AreaCaptureExporter.PREF_KEY_LODLEVELS, settings.LodLevels);
                EditorPrefs.SetInt(AreaCaptureExporter.PREF_KEY_MINLEVEL, settings.MinLevelPixels);
                EditorPrefs.SetInt(AreaCaptureExporter.PREF_KEY_SKIPEMPTY, settings.SkipEmptyTiles ? 1 : 0);
                EditorPrefs.SetInt(AreaCaptureExporter.PREF_KEY_BGENCODE, settings.BackgroundEncoding ? 1 : 0);
            }
        }

        private HashSet<CaptureZone> selectedZones = new HashSet<CaptureZone>();
        private Vector2 zoneScrollPosition;

        private void OnGUI()
        {
            GUILayout.Label("2D Area Capture", EditorStyles.boldLabel);

            EditorGUILayout.Space();

            GUILayout.Label("Capture Settings", EditorStyles.boldLabel);
            settings.PixelPerUnit = EditorGUILayout.IntField(new GUIContent("Pixel Per Unit", "Maximum quality: pixels per world unit of the finest LoD level (a zone can override it). A 1-unit zone at 100 PPU produces a 100×100 px image. Higher values = sharper output and larger files."), settings.PixelPerUnit);
            settings.LodLevels = EditorGUILayout.IntSlider(new GUIContent("Lod Levels", "How many resolutions to export per face. The finest is Pixel Per Unit; each further level is half the resolution of the one before, so a viewer can load smaller images while zoomed out. 1 = only the maximum quality."), settings.LodLevels, 1, CapturePlanner.MaxLodLevels);
            settings.MinLevelPixels = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Min Level Pixels", "Degraded levels whose whole image would be smaller than this (longest edge, in pixels) are not exported, so no tiny textures. The maximum quality level is always exported. 0 = no limit."), settings.MinLevelPixels));
            settings.TilePixels = EditorGUILayout.IntField(new GUIContent("Tile Pixels", $"Pixel size of every tile of every level: each level is cut into tiles this big, so a finer level replaces one tile with four. Tiles on the right/bottom edge and levels smaller than one tile are cropped. Limited to the GPU texture size ({SystemInfo.maxTextureSize})."), settings.TilePixels);
            settings.TilePixels = Mathf.Clamp(settings.TilePixels, 64, SystemInfo.maxTextureSize);
            settings.SkipEmptyTiles = EditorGUILayout.Toggle(new GUIContent("Skip Empty Tiles", "Do not save tiles that are fully transparent (nothing was rendered there) and leave them out of the metadata, so empty parts of a box take no disk space. Needs a transparent background: with an opaque Background Color no tile is empty."), settings.SkipEmptyTiles);
            settings.BackgroundEncoding = EditorGUILayout.Toggle(new GUIContent("Background Encoding", "Encode and write the PNGs on worker threads while the next tile renders, and render several tiles per editor frame. Faster; the Console logs the timing of every export. Turn off for the original one-tile-per-frame behaviour (e.g. to compare speed)."), settings.BackgroundEncoding);

            EditorGUILayout.Space();

            GUILayout.Label("Rendering Options", EditorStyles.boldLabel);
            settings.ClearFlags = (CameraClearFlags)EditorGUILayout.EnumPopup(new GUIContent("Clear Flags", "How the capture camera clears the background before rendering. 'Solid Color' fills with the Background Color. 'Depth Only'/'Don't Clear' may composite existing render artifacts."), settings.ClearFlags);
            
            if (settings.ClearFlags == CameraClearFlags.SolidColor)
            {
                settings.BackgroundColor = EditorGUILayout.ColorField(new GUIContent("Background Color", "The solid background color applied when Clear Flags is set to 'Solid Color'."), settings.BackgroundColor);
            }

            settings.CullingMask = EditorGUILayout.MaskField(new GUIContent("Culling Mask", "Which scene layers the capture camera renders. Toggle layers to include or exclude objects from captured images."),
                InternalEditorUtility.LayerMaskToConcatenatedLayersMask(new LayerMask { value = settings.CullingMask }),
                InternalEditorUtility.layers);
            settings.CullingMask = InternalEditorUtility.ConcatenatedLayersMaskToLayerMask(settings.CullingMask).value;

            EditorGUILayout.Space();

            GUILayout.Label("Export Settings", EditorStyles.boldLabel);
            settings.OutputDirectory = EditorGUILayout.TextField(new GUIContent("Output Directory", "Folder where exported PNG files are saved."), settings.OutputDirectory);
            if (GUILayout.Button("Browse..."))
            {
                string selectedPath = EditorUtility.OpenFolderPanel("Select Export Directory", "Assets", "");
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    // Convert to relative path
                    if (selectedPath.StartsWith(Application.dataPath))
                    {
                        settings.OutputDirectory = "Assets" + selectedPath.Substring(Application.dataPath.Length);
                    }
                }
            }

            settings.MetadataFilename = EditorGUILayout.TextField(new GUIContent("Metadata Filename", "Name of the JSON file written alongside images. Contains world position, size, and rotation for each captured zone."), settings.MetadataFilename);

            EditorGUILayout.Space();

            // Scene info
            GUILayout.Label("Scene Information", EditorStyles.boldLabel);
            CaptureZone[] zones = Object.FindObjectsByType<CaptureZone>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.HelpBox($"Found {zones.Length} CaptureZone component(s) in the scene.", MessageType.Info);
            
            EditorGUILayout.BeginVertical(GUILayout.Width(120));
            if (GUILayout.Button("Select All", EditorStyles.miniButton))
            {
                foreach (var z in zones) selectedZones.Add(z);
            }
            if (GUILayout.Button("Deselect All", EditorStyles.miniButton))
            {
                selectedZones.Clear();
            }
            if (GUILayout.Button("Show/Hide Gizmos", EditorStyles.miniButton))
            {
                bool show = zones.Length > 0 && !zones[0].ShowGizmo;
                foreach (var z in zones) { z.ShowGizmo = show; EditorUtility.SetDirty(z); }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            if (zones.Length > 0)
            {
                EditorGUILayout.Space(5);
                zoneScrollPosition = EditorGUILayout.BeginScrollView(zoneScrollPosition, GUILayout.MaxHeight(400));
                foreach (var zone in zones)
                {
                    bool isSelected = selectedZones.Contains(zone);
                    
                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                    
                    bool newSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(20));
                    if (newSelected != isSelected)
                    {
                        if (newSelected) selectedZones.Add(zone);
                        else selectedZones.Remove(zone);
                    }

                    EditorGUILayout.BeginVertical();
                    EditorGUILayout.LabelField(zone.gameObject.name, EditorStyles.boldLabel);
                    string info = $"Axis: {zone.Axis}";
                    if (zone.ExportCubemap) info += " | [Cubemap]";
                    if (zone.UseStrictClipping) info += " | [Clipped]";
                    if (!string.IsNullOrEmpty(zone.FilenameOverride)) info += $" | Name: {zone.FilenameOverride}";
                    EditorGUILayout.LabelField(info, EditorStyles.miniLabel);

                    List<double> ladder = AreaCaptureExporter.LevelLadder(zone, settings);
                    int imageCount = AreaCaptureExporter.CountImages(zone, settings);
                    string levels = ladder.Count == 0 ? "no LoD" : ladder.Count == 1
                        ? $"L0 {ladder[0]:0.##} ppu"
                        : $"L0–L{ladder.Count - 1} {ladder[0]:0.##}–{ladder[ladder.Count - 1]:0.##} ppu";
                    EditorGUILayout.LabelField($"{levels} | {imageCount} image(s)", EditorStyles.miniLabel);

                    string rotationNote = AreaCaptureExporter.GetRotationNote(zone, out MessageType rotationType);
                    if (rotationNote != null)
                        EditorGUILayout.HelpBox(rotationNote, rotationType);
                    EditorGUILayout.EndVertical();

                    if (GUILayout.Button("Show", GUILayout.Width(60)))
                    {
                        Selection.activeGameObject = zone.gameObject;
                        EditorGUIUtility.PingObject(zone.gameObject);
                    }

                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space();

            // Export button
            GUI.backgroundColor = Color.green;
            string exportLabel = selectedZones.Count == 0 || selectedZones.Count == zones.Length
                ? "Export All Zones"
                : $"Export Selected ({selectedZones.Count})";

            if (GUILayout.Button(exportLabel, GUILayout.Height(40)))
            {
                ExportZones(selectedZones.Count > 0 ? System.Linq.Enumerable.ToArray(selectedZones) : zones);
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space();

            GUILayout.Label("Instructions", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Add CaptureZone component to GameObjects you want to capture\n" +
                "2. Place, scale and rotate the box freely (rotation about one axis is supported)\n" +
                "3. Set 'Pixel Per Unit' to the best quality you want; 'Lod Levels' adds smaller, halved resolutions below it\n" +
                "4. 'Min Level Pixels' drops degraded levels that would be tiny; a zone can override Pixel Per Unit\n" +
                "5. Use the list above to select specific zones if needed\n" +
                "6. Click 'Export' to render and save files",
                MessageType.Info);
        }

        private void ExportZones(CaptureZone[] zonesToExport)
        {
            if (settings.PixelPerUnit <= 0)
            {
                EditorUtility.DisplayDialog("Invalid Settings", "PixelPerUnit must be greater than 0", "OK");
                return;
            }

            try
            {
                AreaCaptureExporter.ExportZones(zonesToExport, settings, success => 
                {
                    if (success)
                    {
                        EditorUtility.DisplayDialog("Success", $"Export completed! Files saved to:\n{settings.OutputDirectory}", "OK");
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Aborted", "Export process was canceled or failed due to an error. Check the Console for details.", "OK");
                    }
                });
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Error", $"Export failed:\n{ex.Message}", "OK");
                Debug.LogError($"Export error: {ex}");
            }
        }
    }
}
