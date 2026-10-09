using System;
using System.Collections.Generic;

// Deliberately free of UnityEngine: this file is compiled and unit-tested outside Unity, and the
// same rules are implemented by the Godot addon and the dashboard (see "Capture Metadata v2").
namespace AreaCapture
{
    /// <summary>The six faces of a capture box. Named for the side the camera looks in from.</summary>
    public enum CaptureFace { Front, Back, Left, Right, Top, Bottom }

    /// <summary>Which single world axis a box is rotated about.</summary>
    public enum RotationAxis
    {
        /// <summary>No rotation.</summary>
        None,
        X,
        Y,
        Z,
        /// <summary>Rotated about more than one axis: cannot be projected to a rotated rectangle.</summary>
        Unsupported
    }

    /// <summary>One image to render: a tile of one LoD level of one face of a box.</summary>
    public readonly struct TileJob
    {
        public readonly CaptureFace Face;
        public readonly int Level;
        public readonly int Col;
        public readonly int Row;
        public readonly int Cols;
        public readonly int Rows;
        public readonly float PixelsPerUnit;
        public readonly float TileWidthUnits;
        public readonly float TileHeightUnits;
        /// <summary>Offset of the tile centre from the box centre, along the image's right axis.</summary>
        public readonly float OffsetU;
        /// <summary>Offset of the tile centre from the box centre, along the image's up axis.</summary>
        public readonly float OffsetV;
        public readonly int PixelWidth;
        public readonly int PixelHeight;
        /// <summary>Pixel size of a full tile at this level; edge tiles and levels smaller than one tile are cropped below it.</summary>
        public readonly int TilePixels;

        public TileJob(CaptureFace face, int level, int col, int row, int cols, int rows, float pixelsPerUnit,
            float tileWidthUnits, float tileHeightUnits, float offsetU, float offsetV, int pixelWidth, int pixelHeight, int tilePixels)
        {
            Face = face; Level = level; Col = col; Row = row; Cols = cols; Rows = rows;
            PixelsPerUnit = pixelsPerUnit; TileWidthUnits = tileWidthUnits; TileHeightUnits = tileHeightUnits;
            OffsetU = offsetU; OffsetV = offsetV; PixelWidth = pixelWidth; PixelHeight = pixelHeight; TilePixels = tilePixels;
        }
    }

    /// <summary>
    /// The rules that turn "a box, a maximum resolution and some LoD levels" into a list of tile renders.
    /// Pure functions, no Unity types.
    /// </summary>
    public static class CapturePlanner
    {
        /// <summary>Pixel size of a full tile unless the export settings say otherwise.</summary>
        public const int DefaultTilePixels = 1024;

        /// <summary>More levels than this (each halves the resolution) would be below any useful size.</summary>
        public const int MaxLodLevels = 32;

        /// <summary>Default lowest resolution (pixels per unit) a LoD level may have; 0 = no floor.</summary>
        public const int DefaultMinPixelsPerUnit = 0;

        private const double GridEpsilon = 1e-6;

        /// <summary>Quaternion component below which a rotation counts as "not about this axis".</summary>
        public const float RotationEpsilon = 1e-4f;


        public static RotationAxis ClassifyRotation(float x, float y, float z, float w)
        {
            bool hasX = Math.Abs(x) > RotationEpsilon;
            bool hasY = Math.Abs(y) > RotationEpsilon;
            bool hasZ = Math.Abs(z) > RotationEpsilon;
            int count = (hasX ? 1 : 0) + (hasY ? 1 : 0) + (hasZ ? 1 : 0);
            if (count == 0) return RotationAxis.None;
            if (count > 1) return RotationAxis.Unsupported;
            return hasX ? RotationAxis.X : hasY ? RotationAxis.Y : RotationAxis.Z;
        }

        /// <summary>
        /// A rotated box only projects to a rotated rectangle from the faces whose normal is the rotation axis.
        /// </summary>
        public static bool FaceSupportsRotation(CaptureFace face, RotationAxis rotation)
        {
            switch (rotation)
            {
                case RotationAxis.None: return true;
                case RotationAxis.X: return face == CaptureFace.Left || face == CaptureFace.Right;
                case RotationAxis.Y: return face == CaptureFace.Top || face == CaptureFace.Bottom;
                case RotationAxis.Z: return face == CaptureFace.Front || face == CaptureFace.Back;
                default: return false;
            }
        }

        /// <summary>World extents (horizontal, vertical) of the image a face shows, from the box's local size.</summary>
        public static void FaceExtents(CaptureFace face, float sizeX, float sizeY, float sizeZ, out float width, out float height)
        {
            switch (face)
            {
                case CaptureFace.Top:
                case CaptureFace.Bottom: width = sizeX; height = sizeZ; break;
                case CaptureFace.Left:
                case CaptureFace.Right: width = sizeZ; height = sizeY; break;
                default: width = sizeX; height = sizeY; break;
            }
        }

        /// <summary>Pixels an extent takes at the finest level (at least 1), rounded up so the image covers the whole extent.</summary>
        public static int FinestPixels(float units, double maxPixelsPerUnit)
        {
            return (int)Math.Max(1L, (long)Math.Ceiling(units * maxPixelsPerUnit - GridEpsilon));
        }

        /// <summary>
        /// Pixels an extent takes at a level <paramref name="stepsBelowFinest"/> halvings below the finest one: the finest size
        /// halved and rounded up each time. Deriving every level from the finest size (rather than rounding each one on its
        /// own) makes the tile grids nest exactly: a tile of one level is covered by four tiles of the next.
        /// </summary>
        public static int LevelPixels(int finestPixels, int stepsBelowFinest)
        {
            return (int)(((long)finestPixels + (1L << stepsBelowFinest) - 1) >> stepsBelowFinest);
        }

        /// <summary>How many tiles of <paramref name="tilePixels"/> pixels cover an image of the given size in pixels.</summary>
        public static void GridFor(int totalWidth, int totalHeight, int tilePixels, out int cols, out int rows)
        {
            cols = (int)(((long)totalWidth + tilePixels - 1) / tilePixels);
            rows = (int)(((long)totalHeight + tilePixels - 1) / tilePixels);
        }

        /// <summary>
        /// Pixels per unit of every LoD level of an image <paramref name="widthUnits"/> × <paramref name="heightUnits"/>,
        /// coarsest first, so the list index is the level tag. The finest level is <paramref name="maxPixelsPerUnit"/>;
        /// each further level halves it until the whole image fits in a single <paramref name="tilePixels"/> tile (that
        /// level is the overview and the last one) or the next level would drop below <paramref name="minPixelsPerUnit"/>.
        /// The finest level is always kept, so the level count follows from the box size and the resolution range.
        /// </summary>
        public static List<double> LevelPixelsPerUnit(float widthUnits, float heightUnits, double maxPixelsPerUnit,
            double minPixelsPerUnit, int tilePixels)
        {
            tilePixels = Math.Max(1, tilePixels);
            int finestW = FinestPixels(widthUnits, maxPixelsPerUnit);
            int finestH = FinestPixels(heightUnits, maxPixelsPerUnit);

            var ppus = new List<double>();
            for (int k = 0; k < MaxLodLevels; k++)
            {
                double ppu = maxPixelsPerUnit / Math.Pow(2, k);
                if (k > 0 && ppu < minPixelsPerUnit * (1 - 1e-9)) break; // only gets lower from here
                ppus.Add(ppu);
                if (LevelPixels(finestW, k) <= tilePixels && LevelPixels(finestH, k) <= tilePixels) break; // whole image in one tile
            }
            ppus.Reverse();
            return ppus;
        }

        /// <summary><see cref="LevelPixelsPerUnit(float, float, double, double, int)"/> for a face of a box of the given local size.</summary>
        public static List<double> LevelPixelsPerUnit(CaptureFace face, float sizeX, float sizeY, float sizeZ,
            double maxPixelsPerUnit, double minPixelsPerUnit, int tilePixels)
        {
            FaceExtents(face, sizeX, sizeY, sizeZ, out float width, out float height);
            return LevelPixelsPerUnit(width, height, maxPixelsPerUnit, minPixelsPerUnit, tilePixels);
        }

        /// <summary>
        /// The level tags to export out of a ladder of <paramref name="levelCount"/> levels: all of them, or with
        /// <paramref name="extremesOnly"/> just the overview (0) and the finest, a quick look at the worst and best LoD.
        /// Tags keep their value from the full ladder, so a later full export fills in the gap.
        /// </summary>
        public static List<int> LevelTags(int levelCount, bool extremesOnly)
        {
            var tags = new List<int>();
            if (extremesOnly && levelCount > 2)
            {
                tags.Add(0);
                tags.Add(levelCount - 1);
            }
            else
            {
                for (int i = 0; i < levelCount; i++) tags.Add(i);
            }
            return tags;
        }

        /// <summary>Number of tiles <see cref="PlanFace"/> would produce, without building them. Cheap enough for UI repaints.</summary>
        public static int CountTiles(CaptureFace face, float sizeX, float sizeY, float sizeZ,
            float maxPixelsPerUnit, double minPixelsPerUnit, int tilePixels, bool extremesOnly = false)
        {
            if (maxPixelsPerUnit <= 0f || tilePixels < 1) return 0;

            FaceExtents(face, sizeX, sizeY, sizeZ, out float width, out float height);

            List<double> levelPpus = LevelPixelsPerUnit(width, height, maxPixelsPerUnit, minPixelsPerUnit, tilePixels);
            int finestW = FinestPixels(width, maxPixelsPerUnit);
            int finestH = FinestPixels(height, maxPixelsPerUnit);

            int total = 0;
            foreach (int i in LevelTags(levelPpus.Count, extremesOnly))
            {
                int steps = levelPpus.Count - 1 - i;
                GridFor(LevelPixels(finestW, steps), LevelPixels(finestH, steps), tilePixels, out int cols, out int rows);
                total += cols * rows;
            }
            return total;
        }

        /// <summary>
        /// Tiles of every LoD level of one face. <paramref name="maxPixelsPerUnit"/> is the finest level's resolution;
        /// see <see cref="LevelPixelsPerUnit(float, float, double, double, int)"/> for the coarser ones. Levels are tagged
        /// from 0 (coarsest) upward, so a higher tag is always more detail; with <paramref name="extremesOnly"/> only the
        /// coarsest and finest levels are planned (see <see cref="LevelTags"/>). Order: level, then row, then column.
        /// <para>
        /// Every tile is <paramref name="tilePixels"/> square, anchored at the face's top-left corner; only the last
        /// column/row (and a level smaller than one tile) is cropped to the face. Adjacent levels differ by a factor
        /// of two in resolution and their image sizes are derived from the finest one (<see cref="LevelPixels"/>), so a
        /// finer level replaces each tile with four: children of (col, row) are (2col..2col+1, 2row..2row+1), and every
        /// child lies inside its parent.
        /// </para>
        /// </summary>
        public static List<TileJob> PlanFace(CaptureFace face, float sizeX, float sizeY, float sizeZ,
            float maxPixelsPerUnit, double minPixelsPerUnit, int tilePixels, bool extremesOnly = false)
        {
            if (maxPixelsPerUnit <= 0f) throw new ArgumentOutOfRangeException(nameof(maxPixelsPerUnit), "Pixels per unit must be positive.");
            if (tilePixels < 1) throw new ArgumentOutOfRangeException(nameof(tilePixels));

            FaceExtents(face, sizeX, sizeY, sizeZ, out float width, out float height);
            List<double> levelPpus = LevelPixelsPerUnit(width, height, maxPixelsPerUnit, minPixelsPerUnit, tilePixels);

            int finestW = FinestPixels(width, maxPixelsPerUnit);
            int finestH = FinestPixels(height, maxPixelsPerUnit);

            var jobs = new List<TileJob>();
            foreach (int i in LevelTags(levelPpus.Count, extremesOnly))
            {
                double ppu = levelPpus[i];
                int steps = levelPpus.Count - 1 - i;
                int totalW = LevelPixels(finestW, steps);
                int totalH = LevelPixels(finestH, steps);
                GridFor(totalW, totalH, tilePixels, out int cols, out int rows);

                for (int row = 0; row < rows; row++)
                {
                    int pxH = (int)Math.Min(tilePixels, totalH - (long)row * tilePixels);
                    float tileH = (float)(pxH / ppu);
                    float originY = (float)((long)row * tilePixels / ppu); // from the top edge
                    float v = height / 2f - (originY + tileH / 2f);

                    for (int col = 0; col < cols; col++)
                    {
                        int pxW = (int)Math.Min(tilePixels, totalW - (long)col * tilePixels);
                        float tileW = (float)(pxW / ppu);
                        float originX = (float)((long)col * tilePixels / ppu); // from the left edge
                        float u = originX + tileW / 2f - width / 2f;
                        jobs.Add(new TileJob(face, i, col, row, cols, rows, (float)ppu, tileW, tileH, u, v, pxW, pxH, tilePixels));
                    }
                }
            }
            return jobs;
        }
    }
}
