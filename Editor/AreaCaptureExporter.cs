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
        internal const string PREF_KEY_MINPPU     = "AreaCapture_MinPPU";
        internal const string PREF_KEY_EXTREMES   = "AreaCapture_ExtremesOnly";
        internal const string PREF_KEY_SKIPEMPTY  = "AreaCapture_SkipEmptyTiles";
        internal const string PREF_KEY_BGENCODE   = "AreaCapture_BackgroundEncoding";

        /// <summary>The trailing "~" makes Unity's AssetDatabase skip the folder: no import, no .meta files.</summary>
        internal const string DEFAULT_OUTDIR = "Assets/Exports~/AreaCaptures";

        /// <summary>
        /// True when Unity would import files in <paramref name="dir"/>: it lies under Assets/ or Packages/
        /// and no path segment ends with "~" or starts with "." (the folders the AssetDatabase ignores).
        /// </summary>
        public static bool WouldBeImportedByUnity(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            string[] parts = dir.Replace('\\', '/').Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || (parts[0] != "Assets" && parts[0] != "Packages")) return false;
            foreach (string part in parts)
                if (part.EndsWith("~") || part.StartsWith(".")) return false;
            return true;
        }

        public static ExportSettings LoadSettingsFromPrefs()
        {
            var s = new ExportSettings
            {
                PixelPerUnit     = EditorPrefs.GetInt(PREF_KEY_PPU, 100),
                OutputDirectory  = EditorPrefs.GetString(PREF_KEY_OUTDIR, DEFAULT_OUTDIR),
                MetadataFilename = EditorPrefs.GetString(PREF_KEY_META, "capture_metadata.json"),
                ClearFlags       = (CameraClearFlags)EditorPrefs.GetInt(PREF_KEY_CLEARFLAG, (int)CameraClearFlags.SolidColor),
                CullingMask      = EditorPrefs.GetInt(PREF_KEY_CULLMASK, -1),
                TilePixels    = EditorPrefs.GetInt(PREF_KEY_TILE, CapturePlanner.DefaultTilePixels),
                MinPixelPerUnit  = EditorPrefs.GetFloat(PREF_KEY_MINPPU, CapturePlanner.DefaultMinPixelsPerUnit),
                ExtremesOnly     = EditorPrefs.GetInt(PREF_KEY_EXTREMES, 0) != 0,
                SkipEmptyTiles   = EditorPrefs.GetInt(PREF_KEY_SKIPEMPTY, 1) != 0,
                BackgroundEncoding = EditorPrefs.GetInt(PREF_KEY_BGENCODE, 1) != 0,
            };
            string html = EditorPrefs.GetString(PREF_KEY_BGCOLOR, "#00000000");
            if (ColorUtility.TryParseHtmlString(html, out Color c)) s.BackgroundColor = c;
            return s;
        }

        /// <summary>Most tiles whose pixels may wait for, or sit in, a writer thread at once.</summary>
        private const int MaxTilesInFlight = 3;

        /// <summary>
        /// Memory budget for those tiles. One in flight holds its pixels and the PNG writer's scanline copy
        /// (2 × tile_px² × 4 bytes) plus the compressed output, so large Tile Pixels allow fewer (always at least one).
        /// </summary>
        private const long InFlightBudgetBytes = 1L << 30;

        private static int TilesInFlight(int tilePixels)
        {
            long perTile = 2L * tilePixels * tilePixels * 4;
            return (int)System.Math.Max(1, System.Math.Min(MaxTilesInFlight, InFlightBudgetBytes / perTile));
        }

        /// <summary>Time spent rendering per editor frame before the UI gets a turn.</summary>
        private const long FrameBudgetMs = 50;

        /// <summary>One export at a time: they share the editor, the GPU and the progress bar.</summary>
        private static bool exportRunning;

        public class ExportSettings
        {
            /// <summary>Pixels per world unit of the finest (max quality) LoD level. A zone can override it.</summary>
            public int PixelPerUnit;
            public string OutputDirectory;
            public string MetadataFilename;

            /// <summary>Largest PNG edge in pixels. Bigger areas are split into tiles instead of failing.</summary>
            public int TilePixels = CapturePlanner.DefaultTilePixels;

            /// <summary>
            /// Lowest resolution a LoD level may have (0 = no floor). Levels start at <see cref="PixelPerUnit"/> and halve
            /// until a level's whole image fits in one tile or the next one would fall below this, so the number of
            /// levels follows from the box size and this range.
            /// </summary>
            public float MinPixelPerUnit = CapturePlanner.DefaultMinPixelsPerUnit;

            /// <summary>Export only the coarsest (overview) and the finest level, a fast preview of worst and best LoD.</summary>
            public bool ExtremesOnly;

            /// <summary>
            /// Do not write tiles that are fully transparent (nothing was rendered there) and leave them out of the
            /// metadata, so empty parts of a box cost no disk space. Has no effect with an opaque background.
            /// </summary>
            public bool SkipEmptyTiles = true;

            /// <summary>
            /// Encode and write PNGs on worker threads while the next tile renders, several tiles per editor frame.
            /// Off = the original behaviour (everything on the main thread, one tile per frame).
            /// </summary>
            public bool BackgroundEncoding = true;

            /// <summary>
            /// Do not write tiles that are fully transparent (nothing was rendered there) and leave them out of the
            /// metadata, so empty parts of a box cost no disk space. Has no effect with an opaque background.
            /// </summary>
            public bool SkipEmptyTiles = true;

            /// <summary>
            /// Encode and write PNGs on worker threads while the next tile renders, several tiles per editor frame.
            /// Off = the original behaviour (everything on the main thread, one tile per frame).
            /// </summary>
            public bool BackgroundEncoding = true;

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
                    EffectivePixelsPerUnit(zone, settings), settings.MinPixelPerUnit, EffectiveTilePixels(settings));
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
                total += CapturePlanner.CountTiles(face, size.x, size.y, size.z, maxPpu, settings.MinPixelPerUnit, tilePixels, settings.ExtremesOnly);
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
                    foreach (TileJob tile in CapturePlanner.PlanFace(face, size.x, size.y, size.z, maxPpu, settings.MinPixelPerUnit, tilePixels, settings.ExtremesOnly))
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

            if (exportRunning)
            {
                Debug.LogWarning("An area capture export is already running; wait for it to finish.");
                onComplete?.Invoke(false);
                return;
            }

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

            // Render the planned tiles with a state machine on EditorApplication.update. Rendering stays on the main
            // thread; with BackgroundEncoding the empty check, PNG encode and file write overlap with the next render.
            var capturer = new RuntimeAreaCapture();
            var queue = settings.BackgroundEncoding ? new TileWriteQueue(TilesInFlight(EffectiveTilePixels(settings))) : null;
            // Legacy mode keeps the original pace of exactly one tile per editor frame
            long frameBudgetMs = settings.BackgroundEncoding ? FrameBudgetMs : 0;
            int currentIndex = 0;
            int total = plan.Jobs.Count;
            var skippedEmpty = new HashSet<string>();
            bool isInitializing = true;

            var totalClock = System.Diagnostics.Stopwatch.StartNew();
            long renderTicks = 0, syncEncodeTicks = 0, queueWaitTicks = 0;
            exportRunning = true;

            EditorApplication.CallbackFunction updateAction = null;

            // Stops the loop and releases everything; the one place every exit path goes through. Runs once: an
            // exception thrown by onComplete must not end the export a second time (and report failure after success).
            bool ended = false;
            void End(bool success, string message)
            {
                if (ended) return;
                ended = true;
                EditorUtility.ClearProgressBar();
                EditorApplication.update -= updateAction;
                capturer.Cleanup();
                exportRunning = false;
                if (message != null) Debug.LogWarning(message);
                onComplete?.Invoke(success);
            }

            double Seconds(long ticks) => (double)ticks / System.Diagnostics.Stopwatch.Frequency;

            updateAction = () =>
            {
                if (isInitializing)
                {
                    // Let Unity draw the requested progress bar for exactly 1 frame before freezing the thread with a capture
                    isInitializing = false;
                    return;
                }

                try
                {
                    if (currentIndex >= total)
                    {
                        // Finished: let the workers write the last tiles before the metadata that references them
                        if (queue != null)
                        {
                            EditorUtility.DisplayProgressBar("Exporting Area Captures", "Writing the last images...", 1f);
                            queue.Drain();
                            if (queue.Error != null)
                            {
                                Debug.LogError($"Capture export failed while writing images: {queue.Error}");
                                End(false, null);
                                return;
                            }
                        }

                        // Save metadata JSON (without the tiles that were skipped as empty)
                        plan.Metadata.RemoveImages(skippedEmpty);
                        string jsonPath = Path.Combine(settings.OutputDirectory, settings.MetadataFilename);
                        File.WriteAllText(jsonPath, CaptureMetadataJson.Serialize(plan.Metadata));

                        // Only now that the new metadata no longer lists them: delete what an earlier export left under the
                        // names of tiles that are empty this time (with their .meta inside Assets/). Deleting them any earlier
                        // would leave a cancelled or failed export with old metadata pointing at missing files.
                        foreach (string skipped in skippedEmpty)
                        {
                            string stale = Path.Combine(settings.OutputDirectory, skipped);
                            if (File.Exists(stale)) File.Delete(stale);
                            if (File.Exists(stale + ".meta")) File.Delete(stale + ".meta");
                        }

                        string skipNote = skippedEmpty.Count > 0 ? $", {skippedEmpty.Count} empty tile(s) skipped" : "";
                        double elapsed = totalClock.Elapsed.TotalSeconds;
                        string stages = queue != null
                            ? $"render+readback {Seconds(renderTicks):F1}s, waiting for a free writer {Seconds(queueWaitTicks):F1}s, writers busy {queue.WorkerTime.TotalSeconds:F1}s (summed over threads)"
                            : $"render+readback {Seconds(renderTicks):F1}s, empty check+PNG+write {Seconds(syncEncodeTicks):F1}s";
                        Debug.Log($"Export complete! {total - skippedEmpty.Count} image(s) saved to: {settings.OutputDirectory}{skipNote}\n" +
                                  $"Timing: {elapsed:F1}s total, {total / Mathf.Max((float)elapsed, 0.001f):F1} tiles/s ({(queue != null ? "background encoding" : "legacy sequential")}); {stages}");
                        End(true, null);
                        return;
                    }

                    bool canceled = EditorUtility.DisplayCancelableProgressBar(
                        "Exporting Area Captures",
                        $"Capturing {plan.Jobs[currentIndex].Filename} ({currentIndex + 1}/{total})...",
                        (float)currentIndex / total);

                    if (canceled)
                    {
                        queue?.Drain();
                        End(false, "Capture export canceled by user.");
                        return;
                    }

                    var frameClock = System.Diagnostics.Stopwatch.StartNew();
                    do
                    {
                        ExportJob job = plan.Jobs[currentIndex];

                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        Texture2D texture = capturer.CaptureTile(
                            job.Zone,
                            job.Tile,
                            settings.ClearFlags,
                            settings.BackgroundColor,
                            settings.CullingMask,
                            uploadToGpu: false
                        );
                        renderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

                        if (texture == null)
                        {
                            // Failed capture (e.g. out of memory)
                            queue?.Drain();
                            End(false, $"Capture export aborted at '{job.Filename}' due to a rendering failure.");
                            return;
                        }

                        string path = Path.Combine(settings.OutputDirectory, job.Filename);
                        bool skipEmpty = settings.SkipEmptyTiles;
                        int width = texture.width, height = texture.height;

                        if (queue != null)
                        {
                            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                            queue.Acquire(); // bounds the pixel buffers held in memory
                            queueWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;

                            byte[] pixels = texture.GetRawTextureData<byte>().ToArray();
                            Object.DestroyImmediate(texture);

                            string filename = job.Filename;
                            queue.Run(() =>
                            {
                                if (skipEmpty && TileCoverage.IsEmpty(pixels))
                                {
                                    // Not written; a PNG an earlier export left under this name is deleted once the metadata is saved
                                    lock (skippedEmpty) skippedEmpty.Add(filename);
                                }
                                else
                                {
                                    File.WriteAllBytes(path, PngWriter.EncodeRgba(pixels, width, height, bottomUp: true));
                                }
                            });
                        }
                        else
                        {
                            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                            // GetRawTextureData() copies, but NativeArray.AsReadOnlySpan needs Unity 2022.2 and package.json says 2021.3
                            if (skipEmpty && TileCoverage.IsEmpty(texture.GetRawTextureData()))
                            {
                                skippedEmpty.Add(job.Filename);
                            }
                            else
                            {
                                File.WriteAllBytes(path, texture.EncodeToPNG());
                            }
                            Object.DestroyImmediate(texture);
                            syncEncodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
                        }

                        currentIndex++;

                        if (queue != null && queue.Error != null)
                        {
                            queue.Drain();
                            Debug.LogError($"Capture export failed while writing images: {queue.Error}");
                            End(false, null);
                            return;
                        }
                    } while (currentIndex < total && frameClock.ElapsedMilliseconds < frameBudgetMs);
                }
                catch (System.Exception e)
                {
                    // An exception inside an update callback would repeat every frame, so stop cleanly
                    try { queue?.Drain(); } catch { /* workers never throw out of their task */ }
                    Debug.LogException(e);
                    End(false, "Capture export aborted by an unexpected error.");
                }
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
