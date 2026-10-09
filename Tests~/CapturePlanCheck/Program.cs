using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AreaCapture;

// Checks the engine-free planner (CapturePlan.cs) and JSON writer (CaptureMetadata.cs) without Unity, including
// that they reproduce the dashboard's shared demo dataset exactly. Exit code 0 = all passed.
int failures = 0, checks = 0;
void Check(bool ok, string what) { checks++; if (!ok) { failures++; Console.WriteLine("FAIL: " + what); } }
void Eq<T>(T actual, T expected, string what) => Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

// ── rotation classification ────────────────────────────────────────────
Eq(CapturePlanner.ClassifyRotation(0, 0, 0, 1), RotationAxis.None, "identity");
Eq(CapturePlanner.ClassifyRotation(0, 0, 0, -1), RotationAxis.None, "-identity");
Eq(CapturePlanner.ClassifyRotation(0, 0.2588f, 0, 0.9659f), RotationAxis.Y, "about y");
Eq(CapturePlanner.ClassifyRotation(0, 0, 0.2588f, 0.9659f), RotationAxis.Z, "about z");
Eq(CapturePlanner.ClassifyRotation(0.2f, 0.2f, 0, 0.9f), RotationAxis.Unsupported, "two axes");
Check(CapturePlanner.FaceSupportsRotation(CaptureFace.Front, RotationAxis.None), "front ok when unrotated");
Check(CapturePlanner.FaceSupportsRotation(CaptureFace.Front, RotationAxis.Z), "front ok for z rotation");
Check(!CapturePlanner.FaceSupportsRotation(CaptureFace.Front, RotationAxis.Y), "front not ok for y rotation");
Check(CapturePlanner.FaceSupportsRotation(CaptureFace.Bottom, RotationAxis.Y), "bottom ok for y rotation");
Check(CapturePlanner.FaceSupportsRotation(CaptureFace.Left, RotationAxis.X), "left ok for x rotation");
Check(!CapturePlanner.FaceSupportsRotation(CaptureFace.Top, RotationAxis.Unsupported), "nothing for unsupported");

// ── face extents (spec table) ───────────────────────────────────────────
CapturePlanner.FaceExtents(CaptureFace.Front, 25, 10, 20, out var w, out var h); Check(w == 25 && h == 10, "front x*y");
CapturePlanner.FaceExtents(CaptureFace.Top, 25, 10, 20, out w, out h); Check(w == 25 && h == 20, "top x*z");
CapturePlanner.FaceExtents(CaptureFace.Right, 25, 10, 20, out w, out h); Check(w == 20 && h == 10, "right z*y (the real Zone02 PNG is 2000x1000)");

// ── tile offsets ────────────────────────────────────────────────────────
var quad = CapturePlanner.PlanFace(CaptureFace.Front, 20, 10, 1, 10, 10, 100);   // 200x100 px, max 100 => 2x1
Eq(quad.Count, 2, "20x10 @10ppu, max 100 => 2 tiles");
Check(Math.Abs(quad[0].OffsetU - -5f) < 1e-5 && Math.Abs(quad[0].OffsetV) < 1e-5, "left tile centre at u=-5");
var grid22 = CapturePlanner.PlanFace(CaptureFace.Front, 20, 10, 1, 10, 10, 50);   // 200x100 px, max 50 => 4x2
Eq(grid22.Count, 8, "4x2 tiles");
var t = grid22.First(j => j.Col == 0 && j.Row == 0);
Check(Math.Abs(t.OffsetU - -7.5f) < 1e-5 && Math.Abs(t.OffsetV - 2.5f) < 1e-5, $"tile 0,0 is top-left (got {t.OffsetU},{t.OffsetV})");

// ── cross-check against the JS demo dataset ─────────────────────────────
// Run from this folder: dotnet run   (override with the first argument)
var repo = args.Length > 0 ? args[0] : "../../../Dashboard/oga-dashboard/tests/fixtures/capture-lod-demo/capture_metadata.json";
if (!File.Exists(repo))
{
    Console.WriteLine($"SKIP: cross-check against the JS demo dataset ('{repo}' not found)");
}
else
{
using var demoDoc = JsonDocument.Parse(File.ReadAllText(repo));
var demo = demoDoc.RootElement;
Eq(demo.GetProperty("schema_version").GetInt32(), 2, "demo is v2");

// Same inputs the JS generator used (see generate-demo.mjs)
var specs = new Dictionary<string, (float[] pos, float[] size, MetaQuat rot, float minPpu, int tagOffset, float maxPpu)>
{
    ["Overview"] = (new[] { 0f, 0f, 0f }, new[] { 100f, 50f, 10f }, MetaQuat.Identity, 0f, 0, 8f),
    ["Detail"] = (new[] { 20f, 10f, 0f }, new[] { 30f, 15f, 10f }, new MetaQuat(0, 0, 0.258819f, 0.965926f), 16f, 3, 16f),
    ["Corner"] = (new[] { -30f, -10f, 0f }, new[] { 20f, 10f, 10f }, MetaQuat.Identity, 8f, 2, 8f),
};

var built = new CaptureMetadata { Scenario = "lod-demo" };
foreach (var boxJson in demo.GetProperty("boxes").EnumerateArray())
{
    string id = boxJson.GetProperty("id").GetString();
    var s = specs[id];
    var box = new BoxMetadata
    {
        Id = id,
        Position = new MetaVec3(s.pos[0], s.pos[1], s.pos[2]),
        Rotation = s.rot,
        Size = new MetaVec3(s.size[0], s.size[1], s.size[2]),
    };
    var face = box.GetOrAddFace(CaptureFace.Front);
    var jobs = CapturePlanner.PlanFace(CaptureFace.Front, s.size[0], s.size[1], s.size[2], s.maxPpu, s.minPpu, 200);
    foreach (var lodGroup in jobs.GroupBy(j => j.Level))
    {
        var first = lodGroup.First();
        // The planner tags levels per box from 0; the fixture gives hand-placed detail boxes a higher global tag,
        // which the exporters no longer write, so it is added here to keep comparing grids/pixel sizes exactly.
        int tag = first.Level + s.tagOffset;
        var lod = new LodMetadata { Level = tag, PixelsPerUnit = first.PixelsPerUnit, Cols = first.Cols, Rows = first.Rows, TilePixels = first.TilePixels };
        foreach (var j in lodGroup)
            lod.Images.Add(new ImageMetadata { Col = j.Col, Row = j.Row, PixelWidth = j.PixelWidth, PixelHeight = j.PixelHeight,
                Filename = $"{id}_Front_L{tag}_{j.Col}x{j.Row}.png" });
        face.Lods.Add(lod);
    }
    built.Boxes.Add(box);
}

// Serialize under a decimal-comma culture to prove the output is culture-invariant
var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
comma.NumberFormat = new NumberFormatInfo { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };
CultureInfo.CurrentCulture = comma;
string json = CaptureMetadataJson.Serialize(built);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "csharp-output.json"), json);

using var outDoc = JsonDocument.Parse(json);   // also proves it is valid JSON

// deep structural comparison: numbers compared with tolerance, everything else exactly
bool Same(JsonElement a, JsonElement b, string path, List<string> diffs)
{
    if (a.ValueKind != b.ValueKind) { diffs.Add($"{path}: kind {a.ValueKind} vs {b.ValueKind}"); return false; }
    switch (a.ValueKind)
    {
        case JsonValueKind.Object:
            var an = a.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
            var bn = b.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
            if (!an.SequenceEqual(bn)) { diffs.Add($"{path}: keys [{string.Join(",", an)}] vs [{string.Join(",", bn)}]"); return false; }
            foreach (var n in an) Same(a.GetProperty(n), b.GetProperty(n), path + "." + n, diffs);
            return diffs.Count == 0;
        case JsonValueKind.Array:
            if (a.GetArrayLength() != b.GetArrayLength()) { diffs.Add($"{path}: length {a.GetArrayLength()} vs {b.GetArrayLength()}"); return false; }
            for (int i = 0; i < a.GetArrayLength(); i++) Same(a[i], b[i], $"{path}[{i}]", diffs);
            return diffs.Count == 0;
        case JsonValueKind.Number:
            if (Math.Abs(a.GetDouble() - b.GetDouble()) > 1e-4) diffs.Add($"{path}: {a.GetDouble()} vs {b.GetDouble()}");
            return diffs.Count == 0;
        default:
            if (a.ToString() != b.ToString()) diffs.Add($"{path}: '{a}' vs '{b}'");
            return diffs.Count == 0;
    }
}
var diffs = new List<string>();
Same(demo, outDoc.RootElement, "$", diffs);
Check(diffs.Count == 0, "C# writer/planner output equals the JS demo dataset:\n  " + string.Join("\n  ", diffs.Take(15)));
}

// ── number formatting / escaping ────────────────────────────────────────
Eq(CaptureMetadataJson.Num(-0.00001, 4), "0", "no negative zero");
Eq(CaptureMetadataJson.Num(1.5, 4), "1.5", "trim zeros");
Eq(CaptureMetadataJson.Num(18.48, 4), "18.48", "keep decimals");
Eq(CaptureMetadataJson.Num(0.965926, 6), "0.965926", "quaternion precision");
Eq(CaptureMetadataJson.Quote("a\"b\\c\n"), "\"a\\\"b\\\\c\\n\"", "escapes");

// ── planner guards ──────────────────────────────────────────────────────
try { CapturePlanner.PlanFace(CaptureFace.Front, 1, 1, 1, 0, 0, 4096); Check(false, "ppu 0 must throw"); } catch (ArgumentOutOfRangeException) { Check(true, ""); }
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 0, 1).Count is > 1 and <= CapturePlanner.MaxLodLevels, "1 px tiles: the ladder still terminates within MaxLodLevels");
var big = CapturePlanner.PlanFace(CaptureFace.Front, 1000, 1000, 1, 100, 100, 4096);  // 100000 px per side
Eq(big.Count, 25 * 25, "huge box tiles instead of aborting");
Check(big.All(j => j.PixelWidth <= 4096 && j.PixelHeight <= 4096), "no tile exceeds the limit");


// ── CountTiles must always agree with PlanFace ──────────────────────────
var rng = new Random(42);
int mismatches = 0;
for (int n = 0; n < 2000; n++)
{
    var face = (CaptureFace)rng.Next(0, 6);
    float sx = (float)(rng.NextDouble() * 200 + 0.1), sy = (float)(rng.NextDouble() * 200 + 0.1), sz = (float)(rng.NextDouble() * 200 + 0.1);
    float ppu = (float)(rng.NextDouble() * 60 + 0.5);
    int max = new[] { 64, 200, 512, 4096, 8192 }[rng.Next(5)];
    double minPpu = new[] { 0, ppu / 8, ppu / 2, ppu, ppu * 2 }[rng.Next(5)];
    bool extremes = rng.Next(2) == 0;
    if (CapturePlanner.CountTiles(face, sx, sy, sz, ppu, minPpu, max, extremes) != CapturePlanner.PlanFace(face, sx, sy, sz, ppu, minPpu, max, extremes).Count) mismatches++;
}
Eq(mismatches, 0, "CountTiles == PlanFace count over 2000 random inputs");
Eq(CapturePlanner.CountTiles(CaptureFace.Front, 1, 1, 1, 0, 0, 4096), 0, "CountTiles guards ppu <= 0");

// ── LoD ladder: ppu is the max quality, levels degrade down from it ─────
// 20x10 units @100 ppu = 2000x1000 px; with 512 px tiles the longest edge is 2000/1000/500 px at 100/50/25 ppu
var ladder = CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 0, 512);
Check(ladder.SequenceEqual(new[] { 25.0, 50, 100 }), $"halves until the whole box fits one tile => 25/50/100 coarsest first (got {string.Join("/", ladder)})");
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 0, 500).SequenceEqual(new[] { 25.0, 50, 100 }), "a level exactly the tile size fits and is the overview");
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 0, 499).SequenceEqual(new[] { 12.5, 25, 50, 100 }), "one px too big => one more level");
Check(CapturePlanner.LevelPixelsPerUnit(0.5f, 0.5f, 100, 0, 1024).SequenceEqual(new[] { 100.0 }), "a box that fits one tile at max ppu has a single level");
var ladderJobs = CapturePlanner.PlanFace(CaptureFace.Front, 20, 10, 1, 100, 0, 512);
Eq(ladderJobs.Select(j => j.Level).Distinct().OrderBy(l => l).ToList().Count, 3, "3 level tags");
Eq(ladderJobs.Where(j => j.Level == 2).Select(j => j.PixelsPerUnit).Distinct().Single(), 100f, "top tag is the requested max ppu");
Eq(ladderJobs.Where(j => j.Level == 0).Select(j => j.PixelsPerUnit).Distinct().Single(), 25f, "L0 is the coarsest");
Eq(ladderJobs.Count(j => j.Level == 0), 1, "the overview is a single tile");
Check(ladderJobs.All(j => j.PixelsPerUnit <= 100f), "no level is rendered above the requested ppu");

// min ppu: the ladder never goes below it, even if the box does not fit one tile yet; the max level is always kept
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 50, 512).SequenceEqual(new[] { 50.0, 100 }), "min 50 stops the ladder at 50");
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 40, 512).SequenceEqual(new[] { 50.0, 100 }), "min between two levels drops the lower one");
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 25, 512).SequenceEqual(new[] { 25.0, 50, 100 }), "level exactly at the min is kept");
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 100, 512).SequenceEqual(new[] { 100.0 }), "min = max => only the max level");
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 150, 512).SequenceEqual(new[] { 100.0 }), "min above max still keeps the max level");
Check(CapturePlanner.LevelPixelsPerUnit(CaptureFace.Right, 25, 10, 20, 100, 0, 512).SequenceEqual(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 0, 512)), "face overload uses the face extents");

// overview + best only: the coarsest and finest level, with their real tags
Eq(string.Join(",", CapturePlanner.LevelTags(4, true)), "0,3", "extremes of 4 levels");
Eq(string.Join(",", CapturePlanner.LevelTags(3, true)), "0,2", "extremes of 3 levels");
Eq(string.Join(",", CapturePlanner.LevelTags(2, true)), "0,1", "2 levels are already the extremes");
Eq(string.Join(",", CapturePlanner.LevelTags(1, true)), "0", "1 level stays");
Eq(string.Join(",", CapturePlanner.LevelTags(4, false)), "0,1,2,3", "all levels");
var fullJobs = CapturePlanner.PlanFace(CaptureFace.Front, 20, 10, 1, 100, 0, 250);          // 4 levels
var previewJobs = CapturePlanner.PlanFace(CaptureFace.Front, 20, 10, 1, 100, 0, 250, extremesOnly: true);
Eq(string.Join(",", fullJobs.Select(j => j.Level).Distinct()), "0,1,2,3", "full plan has 4 levels");
Eq(string.Join(",", previewJobs.Select(j => j.Level).Distinct()), "0,3", "preview keeps L0 and the finest, tags unchanged");
Check(previewJobs.All(p => fullJobs.Any(f => f.Level == p.Level && f.Col == p.Col && f.Row == p.Row && f.PixelsPerUnit == p.PixelsPerUnit
    && f.OffsetU == p.OffsetU && f.OffsetV == p.OffsetV)), "every preview tile is identical to the same tile of the full plan");
Eq(previewJobs.Count, fullJobs.Count(j => j.Level == 0 || j.Level == 3), "preview = exactly the L0 and finest tiles");
// ── constant tile size: anchored at the top-left, cropped only at the right/bottom, a quadtree across levels ──
var cropped = CapturePlanner.PlanFace(CaptureFace.Front, 30, 15, 1, 16, 16, 200);   // 480x240 px => 3x2 tiles of 200 px
Eq(cropped.Count, 6, "480x240 px in 200 px tiles => 3x2");
Check(cropped.All(j => j.TilePixels == 200), "tile size recorded on every job");
Eq(string.Join(",", cropped.Where(j => j.Row == 0).Select(j => j.PixelWidth)), "200,200,80", "columns are 200,200 and a cropped 80");
Eq(string.Join(",", cropped.Where(j => j.Col == 0).Select(j => j.PixelHeight)), "200,40", "rows are 200 and a cropped 40");
var corner = cropped.First(j => j.Col == 2 && j.Row == 1);
Check(Math.Abs(corner.TileWidthUnits - 5f) < 1e-5 && Math.Abs(corner.TileHeightUnits - 2.5f) < 1e-5, "cropped tile covers 5 x 2.5 units");
Check(Math.Abs(corner.OffsetU - 12.5f) < 1e-4 && Math.Abs(corner.OffsetV - -6.25f) < 1e-4, $"cropped corner tile centre (got {corner.OffsetU},{corner.OffsetV})");
var small = CapturePlanner.PlanFace(CaptureFace.Front, 20, 10, 1, 4, 4, 1024);       // 80x40 px: one tile smaller than the tile size
Eq(small.Count, 1, "a level smaller than one tile is a single tile");
Check(small[0].PixelWidth == 80 && small[0].PixelHeight == 40 && small[0].TilePixels == 1024, "...cropped to the face, tile size still nominal");

int nesting = 0, notConstant = 0;
var qrng = new Random(7);
for (int n = 0; n < 500; n++)
{
    float sx = (float)(qrng.NextDouble() * 150 + 1), sy = (float)(qrng.NextDouble() * 150 + 1);
    float ppu = (float)(qrng.NextDouble() * 30 + 1);
    int tile = new[] { 64, 200, 512 }[qrng.Next(3)];
    var jobsQ = CapturePlanner.PlanFace(CaptureFace.Front, sx, sy, 1, ppu, 0, tile);
    foreach (var j in jobsQ)
    {
        if ((j.Col < j.Cols - 1 && j.PixelWidth != tile) || (j.Row < j.Rows - 1 && j.PixelHeight != tile) || j.PixelWidth > tile || j.PixelHeight > tile) notConstant++;
        if (j.Level == 0) continue;
        // The tile of the level below that contains this one
        var parent = jobsQ.FirstOrDefault(p => p.Level == j.Level - 1 && p.Col == j.Col / 2 && p.Row == j.Row / 2);
        if (parent.TileWidthUnits == 0) { nesting++; continue; }
        float tol = 1e-3f; // floating-point slack only: levels are derived from the finest size, so the nesting is exact
        bool inside = j.OffsetU - j.TileWidthUnits / 2 >= parent.OffsetU - parent.TileWidthUnits / 2 - tol
            && j.OffsetU + j.TileWidthUnits / 2 <= parent.OffsetU + parent.TileWidthUnits / 2 + tol
            && j.OffsetV - j.TileHeightUnits / 2 >= parent.OffsetV - parent.TileHeightUnits / 2 - tol
            && j.OffsetV + j.TileHeightUnits / 2 <= parent.OffsetV + parent.TileHeightUnits / 2 + tol;
        if (!inside) nesting++;
    }
}
Eq(notConstant, 0, "every tile but the last column/row is exactly the tile size (500 random inputs)");
Eq(nesting, 0, "every tile lies inside the tile (col/2, row/2) of the level below it (500 random inputs)");

// ── Empty tiles ──

// A tile is empty when every alpha byte is 0, whatever the colour bytes hold (a 4x4 RGBA32 tile)
var emptyTile = new byte[4 * 4 * 4];
Check(TileCoverage.IsEmpty(emptyTile), "all-zero tile is empty");
Array.Fill(emptyTile, (byte)200);
for (int i = 3; i < emptyTile.Length; i += 4) emptyTile[i] = 0;
Check(TileCoverage.IsEmpty(emptyTile), "colour with alpha 0 everywhere is still empty");
emptyTile[emptyTile.Length - 1] = 1;
Check(!TileCoverage.IsEmpty(emptyTile), "one pixel with alpha > 0 (the last byte) makes the tile non-empty");
Check(TileCoverage.IsEmpty(ReadOnlySpan<byte>.Empty), "no pixels is empty");

// Leaving skipped tiles out of the metadata drops their levels, faces and boxes when nothing is left
static LodMetadata MakeLod(int level, params (int col, int row, string file)[] tiles)
{
    var lod = new LodMetadata { Level = level, PixelsPerUnit = 1 << level, Cols = 2, Rows = 1, TilePixels = 100 };
    foreach (var (col, row, file) in tiles) lod.Images.Add(new ImageMetadata { Col = col, Row = row, Filename = file, PixelWidth = 100, PixelHeight = 100 });
    return lod;
}
BoxMetadata MakeBox(string id, params LodMetadata[] lods)
{
    var box = new BoxMetadata { Id = id };
    var face = box.GetOrAddFace(CaptureFace.Top);
    face.Lods.AddRange(lods);
    return box;
}
var pruned = new CaptureMetadata();
pruned.Boxes.Add(MakeBox("A", MakeLod(0, (0, 0, "a0")), MakeLod(1, (0, 0, "a10"), (1, 0, "a11"))));
pruned.Boxes.Add(MakeBox("Empty", MakeLod(0, (0, 0, "e0")), MakeLod(1, (0, 0, "e10"), (1, 0, "e11"))));
pruned.RemoveImages(new HashSet<string> { "a10", "e0", "e10", "e11" });
Eq(string.Join(",", pruned.Boxes.Select(b => b.Id)), "A", "a box whose tiles are all skipped is dropped");
var prunedFace = pruned.Boxes[0].Faces[0];
Eq(prunedFace.Lods.Count, 2, "levels that keep a tile stay");
Eq(string.Join(",", prunedFace.Lods[1].Images.Select(i => i.Filename)), "a11", "only the skipped tile leaves its level");
Eq(prunedFace.Lods[1].Cols, 2, "the grid of a level that stays is untouched");
pruned.RemoveImages(new HashSet<string> { "a0" });
Eq(prunedFace.Lods.Count, 1, "a level left without tiles is dropped");
var untouched = new CaptureMetadata();
untouched.Boxes.Add(MakeBox("A", MakeLod(0, (0, 0, "a0"))));
untouched.RemoveImages(new HashSet<string>());
Eq(untouched.Boxes.Count, 1, "nothing skipped changes nothing");

// --- PngWriter: decode our own output (chunk CRCs, zlib/Adler32, row order) and compare with the input ---
static byte[] DecodePng(byte[] png, out int width, out int height)
{
    var sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    if (!png.Take(8).SequenceEqual(sig)) throw new Exception("bad signature");
    int pos = 8; width = height = 0;
    var idat = new MemoryStream();
    while (pos < png.Length)
    {
        int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
        string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
        uint crc = 0xFFFFFFFFu;
        for (int i = pos + 4; i < pos + 8 + len; i++) { crc ^= png[i]; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1; }
        crc ^= 0xFFFFFFFFu;
        uint expected = (uint)((png[pos + 8 + len] << 24) | (png[pos + 9 + len] << 16) | (png[pos + 10 + len] << 8) | png[pos + 11 + len]);
        if (crc != expected) throw new Exception("bad CRC in " + type);
        if (type == "IHDR") { width = (png[pos + 8] << 24) | (png[pos + 9] << 16) | (png[pos + 10] << 8) | png[pos + 11]; height = (png[pos + 12] << 24) | (png[pos + 13] << 16) | (png[pos + 14] << 8) | png[pos + 15]; }
        if (type == "IDAT") idat.Write(png, pos + 8, len);
        pos += 12 + len;
    }
    idat.Position = 0;
    var raw = new MemoryStream();
    using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress)) z.CopyTo(raw); // verifies Adler32
    var rawBytes = raw.ToArray();
    var pixels = new byte[width * height * 4];
    for (int y = 0; y < height; y++)
    {
        if (rawBytes[y * (width * 4 + 1)] != 0) throw new Exception("unexpected filter");
        Buffer.BlockCopy(rawBytes, y * (width * 4 + 1) + 1, pixels, y * width * 4, width * 4);
    }
    return pixels;
}

{
    const int W = 37, H = 23; // odd sizes catch stride bugs
    var pngRng = new Random(1);
    var src = new byte[W * H * 4];
    pngRng.NextBytes(src);
    var top = DecodePng(PngWriter.EncodeRgba(src, W, H, bottomUp: false), out int pw, out int ph);
    Check(pw == W && ph == H && top.SequenceEqual(src), "PNG round-trip, top-down");
    var bottom = DecodePng(PngWriter.EncodeRgba(src, W, H, bottomUp: true), out _, out _);
    bool flipped = true;
    for (int y = 0; y < H && flipped; y++)
        flipped = bottom.AsSpan(y * W * 4, W * 4).SequenceEqual(src.AsSpan((H - 1 - y) * W * 4, W * 4));
    Check(flipped, "PNG round-trip, bottom-up input is flipped to top-down rows");
    var bigPng = new byte[3000 * 2000 * 4]; // > one Adler32 block, mostly transparent like a real tile
    for (int i = 0; i < bigPng.Length; i += 97) bigPng[i] = (byte)i;
    Check(DecodePng(PngWriter.EncodeRgba(bigPng, 3000, 2000, false), out _, out _).SequenceEqual(bigPng), "PNG round-trip, large image");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var sized = PngWriter.EncodeRgba(new byte[2048 * 2048 * 4], 2048, 2048, true);
    Console.WriteLine($"(info) 2048x2048 empty tile: {sw.ElapsedMilliseconds} ms, {sized.Length} bytes");
}

Console.WriteLine($"{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;
