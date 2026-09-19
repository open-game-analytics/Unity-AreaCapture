using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// Metadata schema v2 ("Capture Metadata v2"): box -> face -> LoD level -> tiles.
// Plain data + a hand-written JSON writer, free of UnityEngine so it is testable outside Unity.
// (JsonUtility cannot serialize dictionaries, and the writer controls numeric precision and culture.)
namespace AreaCapture
{
    public struct MetaVec3
    {
        public float X, Y, Z;
        public MetaVec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    public struct MetaQuat
    {
        public float X, Y, Z, W;
        public MetaQuat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static MetaQuat Identity => new MetaQuat(0f, 0f, 0f, 1f);
    }

    public class ImageMetadata
    {
        public int Col;
        public int Row;
        public string Filename;
        public int PixelWidth;
        public int PixelHeight;
    }

    public class LodMetadata
    {
        public int Level;
        public float PixelsPerUnit;
        public int Cols = 1;
        public int Rows = 1;
        /// <summary>Pixel size of a full tile (written as `tile_size`); 0 = not fixed, the box is divided evenly into Cols x Rows.</summary>
        public int TilePixels;
        public List<ImageMetadata> Images = new List<ImageMetadata>();
    }

    public class FaceMetadata
    {
        public CaptureFace Face;
        public List<LodMetadata> Lods = new List<LodMetadata>();
    }

    /// <summary>One capture box: an oriented volume with its faces.</summary>
    public class BoxMetadata
    {
        public string Id;
        /// <summary>World centre of the oriented box.</summary>
        public MetaVec3 Position;
        /// <summary>Rotation about a single world axis (or identity).</summary>
        public MetaQuat Rotation = MetaQuat.Identity;
        /// <summary>Oriented local size in world units (not the world AABB).</summary>
        public MetaVec3 Size;
        public List<FaceMetadata> Faces = new List<FaceMetadata>();

        public FaceMetadata GetOrAddFace(CaptureFace face)
        {
            foreach (var f in Faces) if (f.Face == face) return f;
            var created = new FaceMetadata { Face = face };
            Faces.Add(created);
            return created;
        }
    }

    public class CaptureMetadata
    {
        public const int SchemaVersion = 2;

        public string Scenario;
        public List<BoxMetadata> Boxes = new List<BoxMetadata>();

        public void Clear() => Boxes.Clear();
    }

    /// <summary>Writes <see cref="CaptureMetadata"/> as schema v2 JSON. Always culture-invariant.</summary>
    public static class CaptureMetadataJson
    {
        private const int VectorDecimals = 4;
        private const int QuaternionDecimals = 6;

        public static string Serialize(CaptureMetadata metadata)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("\t\"schema_version\": ").Append(CaptureMetadata.SchemaVersion);
            if (!string.IsNullOrEmpty(metadata.Scenario))
                sb.Append(",\n\t\"scenario\": ").Append(Quote(metadata.Scenario));
            sb.Append(",\n\t\"boxes\": [");

            for (int b = 0; b < metadata.Boxes.Count; b++)
            {
                WriteBox(sb, metadata.Boxes[b]);
                if (b < metadata.Boxes.Count - 1) sb.Append(',');
            }

            sb.Append(metadata.Boxes.Count > 0 ? "\n\t]\n}\n" : "]\n}\n");
            return sb.ToString();
        }

        private static void WriteBox(StringBuilder sb, BoxMetadata box)
        {
            sb.Append("\n\t\t{\n");
            sb.Append("\t\t\t\"id\": ").Append(Quote(box.Id)).Append(",\n");
            sb.Append("\t\t\t\"transform\": {\n");
            sb.Append("\t\t\t\t\"position\": ").Append(Vec(box.Position)).Append(",\n");
            sb.Append("\t\t\t\t\"rotation\": ").Append(Quat(box.Rotation)).Append(",\n");
            sb.Append("\t\t\t\t\"size\": ").Append(Vec(box.Size)).Append('\n');
            sb.Append("\t\t\t},\n");
            sb.Append("\t\t\t\"faces\": {");

            for (int f = 0; f < box.Faces.Count; f++)
            {
                WriteFace(sb, box.Faces[f]);
                if (f < box.Faces.Count - 1) sb.Append(',');
            }

            sb.Append(box.Faces.Count > 0 ? "\n\t\t\t}\n" : "}\n");
            sb.Append("\t\t}");
        }

        private static void WriteFace(StringBuilder sb, FaceMetadata face)
        {
            sb.Append("\n\t\t\t\t").Append(Quote(face.Face.ToString())).Append(": { \"lods\": [");
            for (int l = 0; l < face.Lods.Count; l++)
            {
                WriteLod(sb, face.Lods[l]);
                if (l < face.Lods.Count - 1) sb.Append(',');
            }
            sb.Append(face.Lods.Count > 0 ? "\n\t\t\t\t] }" : "] }");
        }

        private static void WriteLod(StringBuilder sb, LodMetadata lod)
        {
            sb.Append("\n\t\t\t\t\t{ \"level\": ").Append(lod.Level.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"pixels_per_unit\": ").Append(Num(lod.PixelsPerUnit, VectorDecimals));
            sb.Append(", \"grid\": [").Append(lod.Cols.ToString(CultureInfo.InvariantCulture)).Append(", ")
              .Append(lod.Rows.ToString(CultureInfo.InvariantCulture)).Append("]");
            if (lod.TilePixels > 0)
            {
                string tile = lod.TilePixels.ToString(CultureInfo.InvariantCulture);
                sb.Append(", \"tile_size\": [").Append(tile).Append(", ").Append(tile).Append("]");
            }
            sb.Append(",\n");
            sb.Append("\t\t\t\t\t  \"images\": [");
            for (int i = 0; i < lod.Images.Count; i++)
            {
                var image = lod.Images[i];
                sb.Append("\n\t\t\t\t\t\t{ \"col\": ").Append(image.Col.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"row\": ").Append(image.Row.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"filename\": ").Append(Quote(image.Filename));
                sb.Append(", \"pixel_size\": [").Append(image.PixelWidth.ToString(CultureInfo.InvariantCulture)).Append(", ")
                  .Append(image.PixelHeight.ToString(CultureInfo.InvariantCulture)).Append("] }");
                if (i < lod.Images.Count - 1) sb.Append(',');
            }
            sb.Append(lod.Images.Count > 0 ? "\n\t\t\t\t\t  ] }" : "] }");
        }

        private static string Vec(MetaVec3 v) =>
            $"{{ \"x\": {Num(v.X, VectorDecimals)}, \"y\": {Num(v.Y, VectorDecimals)}, \"z\": {Num(v.Z, VectorDecimals)} }}";

        private static string Quat(MetaQuat q) =>
            $"{{ \"x\": {Num(q.X, QuaternionDecimals)}, \"y\": {Num(q.Y, QuaternionDecimals)}, \"z\": {Num(q.Z, QuaternionDecimals)}, \"w\": {Num(q.W, QuaternionDecimals)} }}";

        /// <summary>Rounded, culture-invariant, trailing zeros trimmed, never "-0".</summary>
        internal static string Num(double value, int decimals)
        {
            double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            if (rounded == 0d) return "0";
            return rounded.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
        }

        internal static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
