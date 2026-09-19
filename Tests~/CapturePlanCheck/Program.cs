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
var quad = CapturePlanner.PlanFace(CaptureFace.Front, 20, 10, 1, 10, 1, 0, 100);   // 200x100 px, max 100 => 2x1
Eq(quad.Count, 2, "20x10 @10ppu, max 100 => 2 tiles");
Check(Math.Abs(quad[0].OffsetU - -5f) < 1e-5 && Math.Abs(quad[0].OffsetV) < 1e-5, "left tile centre at u=-5");
var grid22 = CapturePlanner.PlanFace(CaptureFace.Front, 20, 10, 1, 10, 1, 0, 50);   // 200x100 px, max 50 => 4x2
Eq(grid22.Count, 8, "4x2 tiles");
var t = grid22.First(j => j.Col == 0 && j.Row == 0);
Check(Math.Abs(t.OffsetU - -7.5f) < 1e-5 && Math.Abs(t.OffsetV - 2.5f) < 1e-5, $"tile 0,0 is top-left (got {t.OffsetU},{t.OffsetV})");

// ── cross-check against the JS demo dataset ─────────────────────────────
// Run from this folder: dotnet run   (override with the first argument)
var repo = args.Length > 0 ? args[0] : "../../../Dashboard/oga-dashboard/tests/fixtures/capture-lod-demo/capture_metadata.json";
using var demoDoc = JsonDocument.Parse(File.ReadAllText(repo));
var demo = demoDoc.RootElement;
Eq(demo.GetProperty("schema_version").GetInt32(), 2, "demo is v2");

// Same inputs the JS generator used (see generate-demo.mjs)
var specs = new Dictionary<string, (float[] pos, float[] size, MetaQuat rot, int levels, int tagOffset, float maxPpu)>
{
    ["Overview"] = (new[] { 0f, 0f, 0f }, new[] { 100f, 50f, 10f }, MetaQuat.Identity, 3, 0, 8f),
    ["Detail"] = (new[] { 20f, 10f, 0f }, new[] { 30f, 15f, 10f }, new MetaQuat(0, 0, 0.258819f, 0.965926f), 1, 3, 16f),
    ["Corner"] = (new[] { -30f, -10f, 0f }, new[] { 20f, 10f, 10f }, MetaQuat.Identity, 1, 2, 8f),
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
    var jobs = CapturePlanner.PlanFace(CaptureFace.Front, s.size[0], s.size[1], s.size[2], s.maxPpu, s.levels, 0, 200);
    foreach (var lodGroup in jobs.GroupBy(j => j.Level))
    {
        var first = lodGroup.First();
        // The planner tags levels per box from 0; the fixture gives hand-placed detail boxes a higher global tag,
        // which the exporters no longer write, so it is added here to keep comparing grids/pixel sizes exactly.
        int tag = first.Level + s.tagOffset;
        var lod = new LodMetadata { Level = tag, PixelsPerUnit = first.PixelsPerUnit, Cols = first.Cols, Rows = first.Rows };
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

// ── number formatting / escaping ────────────────────────────────────────
Eq(CaptureMetadataJson.Num(-0.00001, 4), "0", "no negative zero");
Eq(CaptureMetadataJson.Num(1.5, 4), "1.5", "trim zeros");
Eq(CaptureMetadataJson.Num(18.48, 4), "18.48", "keep decimals");
Eq(CaptureMetadataJson.Num(0.965926, 6), "0.965926", "quaternion precision");
Eq(CaptureMetadataJson.Quote("a\"b\\c\n"), "\"a\\\"b\\\\c\\n\"", "escapes");

// ── planner guards ──────────────────────────────────────────────────────
try { CapturePlanner.PlanFace(CaptureFace.Front, 1, 1, 1, 0, 1, 0, 4096); Check(false, "ppu 0 must throw"); } catch (ArgumentOutOfRangeException) { Check(true, ""); }
Eq(CapturePlanner.PlanFace(CaptureFace.Front, 1, 1, 1, 100, 99, 0, 4096).Select(j => j.Level).Max(), CapturePlanner.MaxLodLevels - 1, "level count clamped");
var big = CapturePlanner.PlanFace(CaptureFace.Front, 1000, 1000, 1, 100, 1, 0, 4096);  // 100000 px per side
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
    int levels = rng.Next(1, 6);
    int max = new[] { 64, 200, 512, 4096, 8192 }[rng.Next(5)];
    int minPx = new[] { 0, 64, 256, 1000 }[rng.Next(4)];
    if (CapturePlanner.CountTiles(face, sx, sy, sz, ppu, levels, minPx, max) != CapturePlanner.PlanFace(face, sx, sy, sz, ppu, levels, minPx, max).Count) mismatches++;
}
Eq(mismatches, 0, "CountTiles == PlanFace count over 2000 random inputs");
Eq(CapturePlanner.CountTiles(CaptureFace.Front, 1, 1, 1, 0, 1, 0, 4096), 0, "CountTiles guards ppu <= 0");

// ── LoD ladder: ppu is the max quality, levels degrade down from it ─────
var ladder = CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 4, 0);
Check(ladder.SequenceEqual(new[] { 12.5, 25, 50, 100 }), $"4 levels @100 => 12.5/25/50/100 coarsest first (got {string.Join("/", ladder)})");
var ladderJobs = CapturePlanner.PlanFace(CaptureFace.Front, 20, 10, 1, 100, 4, 0, 4096);
Eq(ladderJobs.Select(j => j.Level).Distinct().OrderBy(l => l).ToList().Count, 4, "4 level tags");
Eq(ladderJobs.Where(j => j.Level == 3).Select(j => j.PixelsPerUnit).Distinct().Single(), 100f, "top tag is the requested max ppu");
Eq(ladderJobs.Where(j => j.Level == 0).Select(j => j.PixelsPerUnit).Distinct().Single(), 12.5f, "L0 is the coarsest");
Check(ladderJobs.All(j => j.PixelsPerUnit <= 100f), "no level is rendered above the requested ppu");
Eq(CapturePlanner.LevelPixelsPerUnit(20, 10, 1, 1, 0).Count, 1, "single level = only the max");

// min pixels: longest face edge 20 units => 2000/1000/500/250 px at 100/50/25/12.5 ppu
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 4, 256).SequenceEqual(new[] { 25.0, 50, 100 }), "250 px level dropped at min 256");
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 4, 250).SequenceEqual(new[] { 12.5, 25, 50, 100 }), "level exactly at the min is kept");
Check(CapturePlanner.LevelPixelsPerUnit(0.5f, 0.5f, 100, 4, 256).SequenceEqual(new[] { 100.0 }), "max level kept even when below the min");
Eq(CapturePlanner.PlanFace(CaptureFace.Front, 20, 10, 1, 100, 4, 256, 4096).Max(j => j.Level), 2, "tags renumbered from 0 after dropping");
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 99, 0).Count == CapturePlanner.MaxLodLevels, "level count clamped to MaxLodLevels");
Check(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 0, 0).SequenceEqual(new[] { 100.0 }), "level count < 1 treated as 1");
Check(CapturePlanner.LevelPixelsPerUnit(CaptureFace.Right, 25, 10, 20, 100, 4, 0).SequenceEqual(CapturePlanner.LevelPixelsPerUnit(20, 10, 100, 4, 0)), "face overload uses the face extents");
Console.WriteLine($"{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;
