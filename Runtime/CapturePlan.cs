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

        public TileJob(CaptureFace face, int level, int col, int row, int cols, int rows, float pixelsPerUnit,
            float tileWidthUnits, float tileHeightUnits, float offsetU, float offsetV, int pixelWidth, int pixelHeight)
        {
            Face = face; Level = level; Col = col; Row = row; Cols = cols; Rows = rows;
            PixelsPerUnit = pixelsPerUnit; TileWidthUnits = tileWidthUnits; TileHeightUnits = tileHeightUnits;
            OffsetU = offsetU; OffsetV = offsetV; PixelWidth = pixelWidth; PixelHeight = pixelHeight;
        }
    }

    /// <summary>
    /// The rules that turn "a box, a maximum resolution and some LoD levels" into a list of tile renders.
    /// Pure functions, no Unity types.
    /// </summary>
    public static class CapturePlanner
    {
        /// <summary>Largest PNG edge a tile may have unless the export settings say otherwise.</summary>
        public const int DefaultMaxTilePixels = 4096;

        /// <summary>More levels than this (each halves the resolution) would be below any useful size.</summary>
        public const int MaxLodLevels = 8;

        /// <summary>Default for the smallest image edge a degraded LoD level may have; smaller levels are dropped.</summary>
        public const int DefaultMinLevelPixels = 256;

        /// <summary>Default number of LoD levels per face.</summary>
        public const int DefaultLodLevels = 4;

        /// <summary>Quaternion component below which a rotation counts as "not about this axis".</summary>
        public const float RotationEpsilon = 1e-4f;

        private const double GridEpsilon = 1e-6;

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

        /// <summary>How many tiles cover an extent at a resolution so that no PNG exceeds <paramref name="maxTilePixels"/>.</summary>
        public static void GridFor(float widthUnits, float heightUnits, double pixelsPerUnit, int maxTilePixels, out int cols, out int rows)
        {
            cols = Math.Max(1, (int)Math.Ceiling(widthUnits * pixelsPerUnit / maxTilePixels - GridEpsilon));
            rows = Math.Max(1, (int)Math.Ceiling(heightUnits * pixelsPerUnit / maxTilePixels - GridEpsilon));
        }

        /// <summary>
        /// Pixels per unit of every LoD level of an image <paramref name="widthUnits"/> × <paramref name="heightUnits"/>,
        /// coarsest first, so the list index is the level tag. The finest level is <paramref name="maxPixelsPerUnit"/>;
        /// each further level halves it. A degraded level whose whole image would be shorter than
        /// <paramref name="minLevelPixels"/> on its longest edge is dropped; the finest level is always kept.
        /// </summary>
        public static List<double> LevelPixelsPerUnit(float widthUnits, float heightUnits, double maxPixelsPerUnit,
            int levelCount, int minLevelPixels)
        {
            levelCount = Math.Max(1, Math.Min(levelCount, MaxLodLevels));
            double longestUnits = Math.Max(widthUnits, heightUnits);

            var ppus = new List<double>();
            for (int i = 0; i < levelCount; i++)
            {
                double ppu = maxPixelsPerUnit / Math.Pow(2, i);
                if (i > 0 && longestUnits * ppu < minLevelPixels) break; // only gets smaller from here
                ppus.Add(ppu);
            }
            ppus.Reverse();
            return ppus;
        }

        /// <summary><see cref="LevelPixelsPerUnit(float, float, double, int, int)"/> for a face of a box of the given local size.</summary>
        public static List<double> LevelPixelsPerUnit(CaptureFace face, float sizeX, float sizeY, float sizeZ,
            double maxPixelsPerUnit, int levelCount, int minLevelPixels)
        {
            FaceExtents(face, sizeX, sizeY, sizeZ, out float width, out float height);
            return LevelPixelsPerUnit(width, height, maxPixelsPerUnit, levelCount, minLevelPixels);
        }

        /// <summary>Number of tiles <see cref="PlanFace"/> would produce, without building them. Cheap enough for UI repaints.</summary>
        public static int CountTiles(CaptureFace face, float sizeX, float sizeY, float sizeZ,
            float maxPixelsPerUnit, int levelCount, int minLevelPixels, int maxTilePixels)
        {
            if (maxPixelsPerUnit <= 0f || maxTilePixels < 1) return 0;

            FaceExtents(face, sizeX, sizeY, sizeZ, out float width, out float height);

            int total = 0;
            foreach (double ppu in LevelPixelsPerUnit(width, height, maxPixelsPerUnit, levelCount, minLevelPixels))
            {
                GridFor(width, height, ppu, maxTilePixels, out int cols, out int rows);
                total += cols * rows;
            }
            return total;
        }

        /// <summary>
        /// Tiles of every LoD level of one face. <paramref name="maxPixelsPerUnit"/> is the finest level's resolution;
        /// see <see cref="LevelPixelsPerUnit(float, float, double, int, int)"/> for the coarser ones. Levels are tagged
        /// from 0 (coarsest) upward, so a higher tag is always more detail. Order: level, then row, then column.
        /// </summary>
        public static List<TileJob> PlanFace(CaptureFace face, float sizeX, float sizeY, float sizeZ,
            float maxPixelsPerUnit, int levelCount, int minLevelPixels, int maxTilePixels)
        {
            if (maxPixelsPerUnit <= 0f) throw new ArgumentOutOfRangeException(nameof(maxPixelsPerUnit), "Pixels per unit must be positive.");
            if (maxTilePixels < 1) throw new ArgumentOutOfRangeException(nameof(maxTilePixels));

            FaceExtents(face, sizeX, sizeY, sizeZ, out float width, out float height);
            List<double> levelPpus = LevelPixelsPerUnit(width, height, maxPixelsPerUnit, levelCount, minLevelPixels);

            var jobs = new List<TileJob>();
            for (int i = 0; i < levelPpus.Count; i++)
            {
                double ppu = levelPpus[i];
                GridFor(width, height, ppu, maxTilePixels, out int cols, out int rows);

                float tileW = width / cols;
                float tileH = height / rows;
                int pxW = Math.Max(1, (int)Math.Round(tileW * ppu, MidpointRounding.AwayFromZero));
                int pxH = Math.Max(1, (int)Math.Round(tileH * ppu, MidpointRounding.AwayFromZero));

                for (int row = 0; row < rows; row++)
                {
                    for (int col = 0; col < cols; col++)
                    {
                        float u = (col + 0.5f) / cols * width - width / 2f;
                        float v = height / 2f - (row + 0.5f) / rows * height;
                        jobs.Add(new TileJob(face, i, col, row, cols, rows, (float)ppu, tileW, tileH, u, v, pxW, pxH));
                    }
                }
            }
            return jobs;
        }
    }
}
