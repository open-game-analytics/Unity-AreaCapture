using UnityEngine;
using System.Collections.Generic;
using System.IO;
using AreaCapture.Runtime;

using UnityEditor;

namespace AreaCapture.Editor
{
    /// <summary>
    /// Exports captured areas to PNG images and JSON metadata (schema v2: box → face → LoD level → tiles)
    /// </summary>
    public class AreaCaptureExporter
    {
        internal const string PREF_KEY_PPU        = "AreaCapture_PPU";
        internal const string PREF_KEY_OUTDIR     = "AreaCapture_OutDir";
        internal const string PREF_KEY_META       = "AreaCapture_Meta";
        internal const string PREF_KEY_CLEARFLAG  = "AreaCapture_ClearFlag";
        internal const string PREF_KEY_BGCOLOR    = "AreaCapture_BGColor";
        internal const string PREF_KEY_CULLMASK   = "AreaCapture_CullMask";
        internal const string PREF_KEY_TILE       = "AreaCapture_TilePixels";
        internal const string PREF_KEY_LODLEVELS  = "AreaCapture_LodLevels";
        internal const string PREF_KEY_MINLEVEL   = "AreaCapture_MinLevelPixels";
        public static ExportSettings LoadSettingsFromPrefs()
        {
            var s = new ExportSettings
            {
                PixelPerUnit     = EditorPrefs.GetInt(PREF_KEY_PPU, 100),
                OutputDirectory  = EditorPrefs.GetString(PREF_KEY_OUTDIR, "Assets/Exports/AreaCaptures"),
                MetadataFilename = EditorPrefs.GetString(PREF_KEY_META, "capture_metadata.json"),
                ClearFlags       = (CameraClearFlags)EditorPrefs.GetInt(PREF_KEY_CLEARFLAG, (int)CameraClearFlags.SolidColor),
                CullingMask      = EditorPrefs.GetInt(PREF_KEY_CULLMASK, -1),
                TilePixels    = EditorPrefs.GetInt(PREF_KEY_TILE, CapturePlanner.DefaultTilePixels),
                LodLevels        = EditorPrefs.GetInt(PREF_KEY_LODLEVELS, CapturePlanner.DefaultLodLevels),
                MinLevelPixels   = EditorPrefs.GetInt(PREF_KEY_MINLEVEL, CapturePlanner.DefaultMinLevelPixels),
            };
            string html = EditorPrefs.GetString(PREF_KEY_BGCOLOR, "#00000000");
            if (ColorUtility.TryParseHtmlString(html, out Color c)) s.BackgroundColor = c;
            return s;
        }

        public class ExportSettings
        {
            /// <summary>Pixels per world unit of the finest (max quality) LoD level. A zone can override it.</summary>
            public int PixelPerUnit;
            public string OutputDirectory;
            public string MetadataFilename;

            /// <summary>Largest PNG edge in pixels. Bigger areas are split into tiles instead of failing.</summary>
            public int TilePixels = CapturePlanner.DefaultTilePixels;

            /// <summary>Levels of detail per face: the finest is <see cref="PixelPerUnit"/>, each further one halves it.</summary>
            public int LodLevels = CapturePlanner.DefaultLodLevels;

            /// <summary>A degraded level whose whole image would be shorter than this (longest edge, px) is not exported.</summary>
            public int MinLevelPixels = CapturePlanner.DefaultMinLevelPixels;

            // Rendering options
            public CameraClearFlags ClearFlags = CameraClearFlags.SolidColor;
            public Color BackgroundColor = new Color(0, 0, 0, 0); // Transparent black by default
            public int CullingMask = -1; // Everything
        }

        /// <summary>One PNG to render and where to write it.</summary>
        internal class ExportJob
        {
            public CaptureZone Zone;
            public TileJob Tile;
            public string Filename;
        }

        /// <summary>Everything an export will do, worked out before anything is rendered.</summary>
        public class ExportPlan
        {
            internal readonly List<ExportJob> Jobs = new List<ExportJob>();
            public readonly CaptureMetadata Metadata = new CaptureMetadata();
            public readonly List<string> Warnings = new List<string>();

            public int ImageCount => Jobs.Count;
        }

        /// <summary>
        /// Exports all CaptureZone components in the scene
        /// </summary>
        public static void ExportAllCaptureZones(ExportSettings settings = null, System.Action<bool> onComplete = null)
        {
            if (settings == null)
                settings = new ExportSettings();

            // Find all CaptureZone components in the scene
            CaptureZone[] zones = Object.FindObjectsByType<CaptureZone>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            if (zones.Length == 0)
            {
                Debug.LogWarning("No CaptureZone components found in the scene!");
                onComplete?.Invoke(false);
                return;
            }

            ExportZones(zones, settings, onComplete);
        }

        /// <summary>Tile size actually used: the setting, limited by what the GPU can render.</summary>
        public static int EffectiveTilePixels(ExportSettings settings)
        {
            int requested = settings.TilePixels <= 0 ? CapturePlanner.DefaultTilePixels : settings.TilePixels;
            return Mathf.Clamp(requested, 64, SystemInfo.maxTextureSize);
        }

        /// <summary>Pixels per unit of a zone's finest level: its own override, else the window's setting.</summary>
        public static float EffectivePixelsPerUnit(CaptureZone zone, ExportSettings settings)
        {
            return zone.PixelsPerUnitOverride > 0 ? zone.PixelsPerUnitOverride : settings.PixelPerUnit;
        }

        /// <summary>
        /// The pixels per unit of each LoD level (coarsest first, list index = level tag) a zone would export for its
        /// first exportable face. Empty if the zone cannot be exported. For a cubemap zone other faces may keep
        /// fewer levels, since the minimum size is checked against each face's own extent.
        /// </summary>
        public static List<double> LevelLadder(CaptureZone zone, ExportSettings settings)
        {
            Vector3 size = zone.OrientedSize;
            RotationAxis rotation = zone.RotationAbout;
            if (size.x <= 0f || size.y <= 0f || size.z <= 0f || rotation == RotationAxis.Unsupported || settings.PixelPerUnit <= 0)
                return new List<double>();

            foreach (CaptureFace face in zone.FacesToExport())
            {
                if (!CapturePlanner.FaceSupportsRotation(face, rotation)) continue;
                return CapturePlanner.LevelPixelsPerUnit(face, size.x, size.y, size.z,
                    EffectivePixelsPerUnit(zone, settings), settings.LodLevels, settings.MinLevelPixels);
            }
            return new List<double>();
        }

        /// <summary>
        /// How many PNGs a zone would export. Mirrors <see cref="BuildPlan"/> but builds nothing, so it is safe to
        /// call from GUI repaints.
        /// </summary>
        public static int CountImages(CaptureZone zone, ExportSettings settings)
        {
            Vector3 size = zone.OrientedSize;
            RotationAxis rotation = zone.RotationAbout;
            if (size.x <= 0f || size.y <= 0f || size.z <= 0f || rotation == RotationAxis.Unsupported) return 0;

            float maxPpu = EffectivePixelsPerUnit(zone, settings);
            int tilePixels = EffectiveTilePixels(settings);

            int total = 0;
            foreach (CaptureFace face in zone.FacesToExport())
            {
                if (!CapturePlanner.FaceSupportsRotation(face, rotation)) continue;
                total += CapturePlanner.CountTiles(face, size.x, size.y, size.z, maxPpu, settings.LodLevels, settings.MinLevelPixels, tilePixels);
            }
            return total;
        }

        /// <summary>
        /// Works out every image an export would produce, plus the metadata describing them. Renders nothing,
        /// so the editor UI can use it for counts. Problems are reported in <see cref="ExportPlan.Warnings"/>.
        /// </summary>
        public static ExportPlan BuildPlan(CaptureZone[] zones, ExportSettings settings)
        {
            var plan = new ExportPlan();
            int tilePixels = EffectiveTilePixels(settings);
            var usedIds = new HashSet<string>();

            for (int z = 0; z < zones.Length; z++)
            {
                CaptureZone zone = zones[z];
                if (zone == null) continue;

                string id = UniqueId(GetZoneName(zone, z), usedIds);
                Vector3 size = zone.OrientedSize;

                if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
                {
                    plan.Warnings.Add($"'{zone.name}' has no volume (check its BoxCollider size and scale) and was skipped.");
                    continue;
                }

                RotationAxis rotation = zone.RotationAbout;
                if (rotation == RotationAxis.Unsupported)
                {
                    plan.Warnings.Add($"'{zone.name}' is rotated about more than one axis and was skipped. Only rotation about a single axis (X, Y or Z) can be exported.");
                    continue;
                }

                float maxPpu = EffectivePixelsPerUnit(zone, settings);
                Vector3 center = zone.WorldCenter;
                Quaternion q = zone.transform.rotation;

                var box = new BoxMetadata
                {
                    Id = id,
                    Position = new MetaVec3(center.x, center.y, center.z),
                    Rotation = new MetaQuat(q.x, q.y, q.z, q.w),
                    Size = new MetaVec3(size.x, size.y, size.z),
                };

                var skippedFaces = new List<CaptureFace>();
                foreach (CaptureFace face in zone.FacesToExport())
                {
                    if (!CapturePlanner.FaceSupportsRotation(face, rotation))
                    {
                        skippedFaces.Add(face);
                        continue;
                    }

                    FaceMetadata faceMeta = box.GetOrAddFace(face);
                    LodMetadata lod = null;
                    foreach (TileJob tile in CapturePlanner.PlanFace(face, size.x, size.y, size.z, maxPpu, settings.LodLevels, settings.MinLevelPixels, tilePixels))
                    {
                        if (lod == null || lod.Level != tile.Level)
                        {
                            lod = new LodMetadata { Level = tile.Level, PixelsPerUnit = tile.PixelsPerUnit, Cols = tile.Cols, Rows = tile.Rows, TilePixels = tile.TilePixels };
                            faceMeta.Lods.Add(lod);
                        }

                        string filename = $"{id}_{face}_L{tile.Level}_{tile.Col}x{tile.Row}.png";
                        lod.Images.Add(new ImageMetadata { Col = tile.Col, Row = tile.Row, Filename = filename, PixelWidth = tile.PixelWidth, PixelHeight = tile.PixelHeight });
                        plan.Jobs.Add(new ExportJob { Zone = zone, Tile = tile, Filename = filename });
                    }
                }

                if (skippedFaces.Count > 0)
                {
                    plan.Warnings.Add($"'{zone.name}' is rotated about {rotation}, so only the faces looking along that axis can be exported. Skipped: {string.Join(", ", skippedFaces)}.");
                }

                if (box.Faces.Count > 0) plan.Metadata.Boxes.Add(box);
            }

            return plan;
        }

        /// <summary>
        /// Exports specific CaptureZone components asynchronously to prevent Editor freezing
        /// </summary>
        public static void ExportZones(CaptureZone[] zones, ExportSettings settings = null, System.Action<bool> onComplete = null)
        {
            if (zones == null || zones.Length == 0)
            {
                onComplete?.Invoke(false);
                return;
            }

            if (settings == null)
                settings = new ExportSettings();

            ExportPlan plan = BuildPlan(zones, settings);
            foreach (string warning in plan.Warnings) Debug.LogWarning(warning);

            if (plan.Jobs.Count == 0)
            {
                Debug.LogWarning("Nothing to export: none of the selected zones can produce an image.");
                onComplete?.Invoke(false);
                return;
            }

            // Create output directory
            if (!Directory.Exists(settings.OutputDirectory))
            {
                Directory.CreateDirectory(settings.OutputDirectory);
            }

            // Render the planned tiles one per editor frame with a state machine on EditorApplication.update
            var capturer = new RuntimeAreaCapture();
            int currentIndex = 0;
            int total = plan.Jobs.Count;
            bool isInitializing = true;

            EditorApplication.CallbackFunction updateAction = null;

            // Common teardown for the two failure exits below (cancel, capture failure): stop the
            // progress bar, unsubscribe from the update loop, release the capturer, and report failure.
            void Abort(string warning)
            {
                EditorUtility.ClearProgressBar();
                EditorApplication.update -= updateAction;
                capturer.Cleanup();
                Debug.LogWarning(warning);
                onComplete?.Invoke(false);
            }

            updateAction = () =>
            {
                if (isInitializing)
                {
                    // Let Unity draw the requested progress bar for exactly 1 frame before freezing the thread with a capture
                    isInitializing = false;
                    return;
                }

                if (currentIndex >= total)
                {
                    // Finished
                    EditorUtility.ClearProgressBar();
                    EditorApplication.update -= updateAction;
                    capturer.Cleanup();

                    // Save metadata JSON
                    string jsonPath = Path.Combine(settings.OutputDirectory, settings.MetadataFilename);
                    File.WriteAllText(jsonPath, CaptureMetadataJson.Serialize(plan.Metadata));

                    Debug.Log($"Export complete! {total} image(s) saved to: {settings.OutputDirectory}");
                    onComplete?.Invoke(true);
                    return;
                }

                ExportJob job = plan.Jobs[currentIndex];

                bool canceled = EditorUtility.DisplayCancelableProgressBar(
                    "Exporting Area Captures",
                    $"Capturing {job.Filename} ({currentIndex + 1}/{total})...",
                    (float)currentIndex / total);

                if (canceled)
                {
                    Abort("Capture export canceled by user.");
                    return;
                }

                Texture2D texture = capturer.CaptureTile(
                    job.Zone,
                    job.Tile,
                    settings.ClearFlags,
                    settings.BackgroundColor,
                    settings.CullingMask
                );

                if (texture == null)
                {
                    // Failed capture (e.g. out of memory)
                    Abort($"Capture export aborted at '{job.Filename}' due to a rendering failure.");
                    return;
                }

                string path = Path.Combine(settings.OutputDirectory, job.Filename);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);

                currentIndex++;
            };

            // Initial Progress Bar setup
            EditorUtility.DisplayProgressBar("Exporting Area Captures", "Initializing...", 0f);

            // Start the async process
            EditorApplication.update += updateAction;
        }

        /// <summary>
        /// A short explanation of what a zone's rotation means for the export, or null when it is not rotated.
        /// </summary>
        public static string GetRotationNote(CaptureZone zone, out MessageType type)
        {
            RotationAxis rotation = zone.RotationAbout;
            switch (rotation)
            {
                case RotationAxis.None:
                    type = MessageType.None;
                    return null;
                case RotationAxis.Unsupported:
                    type = MessageType.Warning;
                    return "Rotated about more than one axis. Only rotation about a single axis (X, Y or Z) can be exported, so this zone will be skipped.";
                default:
                    type = MessageType.Info;
                    string faces = rotation == RotationAxis.X ? "Left / Right" : rotation == RotationAxis.Y ? "Top / Bottom" : "Front / Back";
                    return $"Rotated about {rotation}. The rotation is exported; only the faces looking along that axis ({faces}) are captured.";
            }
        }

        /// <summary>The GameObject name (or the filename override) made safe for file names and unique via <paramref name="used"/>.</summary>
        private static string UniqueId(string name, HashSet<string> used)
        {
            string id = name;
            for (int n = 2; !used.Add(id); n++) id = $"{name}_{n}";
            return id;
        }

        private static string GetZoneName(CaptureZone zone, int index)
        {
            string raw = !string.IsNullOrEmpty(zone.FilenameOverride) ? zone.FilenameOverride : zone.name;
            if (raw.ToLowerInvariant().EndsWith(".png")) raw = raw.Substring(0, raw.Length - 4);
            if (string.IsNullOrEmpty(raw)) raw = $"Zone{index}";

            // Remove invalid file path characters and spaces
            var invalidChars = Path.GetInvalidFileNameChars();
            string cleanName = string.Join("_", raw.Split(invalidChars, System.StringSplitOptions.RemoveEmptyEntries)).Replace(" ", "");

            return string.IsNullOrEmpty(cleanName) ? $"Zone{index}" : cleanName;
        }
    }
}
