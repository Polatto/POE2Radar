using POE2Radar.Core;
using POE2Radar.Core.Game;
using Vector2 = System.Numerics.Vector2;

namespace POE2Radar.Research;

/// <summary>Read-only: samples RelativePos of visible-boat anchors/islands + visible reference nodes at high
/// rate while the user pans, and reports per-node deviation from the common pan delta, Self-check failures and
/// non-finite reads. Diagnoses flicker of boat lines near screen edges.</summary>
internal static class BoatJitter
{
    public static int Run(MemoryReader reader, nint igs, int secs)
    {
        var atlas = new Poe2Atlas(reader);
        var btns = new List<Poe2Atlas.AtlasBoatButton>();
        var nb = atlas.ReadNodes(igs, btns);
        for (var w = 0; (nb.Count == 0 || !atlas.AllTagsResolved) && w < 100; w++) { Thread.Sleep(50); nb = atlas.ReadNodes(igs, btns); }
        var linked = Poe2Atlas.LinkBoats(btns, nb, visibleOnly: true);
        var targets = new List<(int idx, string tag)>();
        foreach (var b in linked)
        {
            if (b.Anchor < 0) continue;
            targets.Add((b.Anchor, $"anchor ({b.Button.GridX},{b.Button.GridY})"));
            foreach (var i in b.Islands) if (i != b.Anchor && !nb[i].Revealed) targets.Add((i, $"island of ({b.Button.GridX},{b.Button.GridY})"));
        }
        var refs = Enumerable.Range(0, nb.Count).Where(i => nb[i].Visible && nb[i].Revealed).Take(40).ToList();
        Console.WriteLine($"nodes {nb.Count} boats {linked.Count} targets {targets.Count} refs {refs.Count}; sampling {secs}s — PAN the atlas now (incl. near screen edges)");
        var all = targets.Select(t => t.idx).Concat(refs).Distinct().ToArray();
        var p0 = new Dictionary<int, Vector2>();
        foreach (var i in all) if (Read(reader, nb[i].Element, out var v, out _)) p0[i] = v;
        var maxDev = new Dictionary<int, float>(); var selfFail = new Dictionary<int, int>(); var badVal = new Dictionary<int, int>();
        var stuck = new Dictionary<int, int>(); var gridChg = new Dictionary<int, int>(); int samples = 0, refGone = 0; float maxPan = 0, maxRefRes = 0, minSc = 99, maxSc = 0;
        var ocean = btns.Where(b => b.Kind == Poe2.AtlasRegionButton.KindOcean).ToList();
        var lastVis = new Dictionary<nint, bool>(); var toggles = new Dictionary<nint, int>(); var nodeVisTog = new Dictionary<int, int>(); var lastNodeVis = new Dictionary<int, bool>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < secs)
        {
            // Fit v = s*p0 + t over the reference nodes (pan + zoom), then measure each target's residual.
            var pr = new List<(Vector2 a, Vector2 v)>();
            foreach (var r in refs) if (p0.ContainsKey(r) && Read(reader, nb[r].Element, out var v, out _) && reader.TryReadStruct<int>(nb[r].Element + Poe2.AtlasNode.GridPos, out var rgx) && rgx == nb[r].GridX) pr.Add((p0[r], v)); else refGone++;
            if (pr.Count < 3) { Thread.Sleep(2); continue; }
            var ma = new Vector2(pr.Average(q => q.a.X), pr.Average(q => q.a.Y)); var mv = new Vector2(pr.Average(q => q.v.X), pr.Average(q => q.v.Y));
            double num = 0, den = 0; foreach (var q in pr) { var da = q.a - ma; var dv = q.v - mv; num += Vector2.Dot(da, dv); den += Vector2.Dot(da, da); }
            var sc = den > 1e-3 ? (float)(num / den) : 1f; var t = mv - sc * ma;
            float refRes = pr.Max(q => (q.v - (sc * q.a + t)).Length());
            maxRefRes = MathF.Max(maxRefRes, refRes); minSc = MathF.Min(minSc, sc); maxSc = MathF.Max(maxSc, sc);
            var pan = t; maxPan = MathF.Max(maxPan, pan.Length());
            foreach (var (i, _) in targets)
            {
                if (!Read(reader, nb[i].Element, out var v, out var why)) { if (why == 1) Inc(selfFail, i); else Inc(badVal, i); continue; }
                if (!p0.TryGetValue(i, out var a)) continue;
                if (reader.TryReadStruct<int>(nb[i].Element + Poe2.AtlasNode.GridPos, out var gx) && reader.TryReadStruct<int>(nb[i].Element + Poe2.AtlasNode.GridPos + 4, out var gy) && (gx != nb[i].GridX || gy != nb[i].GridY)) { Inc(gridChg, i); continue; }
                var dev = (v - (sc * a + t)).Length();
                if (dev > (maxDev.TryGetValue(i, out var m) ? m : 0)) maxDev[i] = dev;
                if (dev > 3f) Inc(stuck, i);
            }
            foreach (var b in ocean)
            {
                if (!reader.TryReadStruct<uint>(b.Element + Poe2.UiElement.Flags, out var fl)) continue;
                var v = ((fl >> Poe2.UiElement.FlagVisibleBit) & 1) != 0;
                if (lastVis.TryGetValue(b.Element, out var lv) && lv != v) toggles[b.Element] = (toggles.TryGetValue(b.Element, out var c) ? c : 0) + 1;
                lastVis[b.Element] = v;
            }
            samples++; Thread.Sleep(2);
        }
        Console.WriteLine($"samples {samples}, max pan {maxPan:0}, scale {minSc:0.000}..{maxSc:0.000}, max ref residual {maxRefRes:0.0}, recycled ref reads {refGone}");
        foreach (var b in ocean) if (toggles.TryGetValue(b.Element, out var tc) || b.Visible)
            Console.WriteLine($"  BUTTON ({b.GridX},{b.GridY}) vis0 {(b.Visible ? 1 : 0)} visToggles {(toggles.TryGetValue(b.Element, out var tc2) ? tc2 : 0)} visNow {(lastVis.TryGetValue(b.Element, out var vn) && vn ? 1 : 0)}");
        foreach (var (i, tag) in targets.DistinctBy(t => t.idx).Take(0))
        {
            var n = nb[i];
            Console.WriteLine($"  ({n.GridX},{n.GridY}) {n.MapName,-22} {tag,-24} vis0 {(n.Visible ? 1 : 0)} maxDev {(maxDev.TryGetValue(i, out var d) ? d : 0):0.0} devSamples {(stuck.TryGetValue(i, out var s) ? s : 0)} selfFail {(selfFail.TryGetValue(i, out var f) ? f : 0)} badVal {(badVal.TryGetValue(i, out var b) ? b : 0)} gridChanged {(gridChg.TryGetValue(i, out var g) ? g : 0)}");
        }
        return 0;
    }

    static void Inc(Dictionary<int, int> d, int k) => d[k] = (d.TryGetValue(k, out var v) ? v : 0) + 1;

    static bool Read(MemoryReader r, nint el, out Vector2 v, out int why)
    {
        v = default; why = 0;
        if (!r.TryReadStruct<nint>(el + Poe2.UiElement.Self, out var self) || self != el) { why = 1; return false; }
        if (!r.TryReadStruct<Vector2>(el + Poe2.UiElement.RelativePos, out v)) { why = 2; return false; }
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || MathF.Abs(v.X) > 200000f || MathF.Abs(v.Y) > 200000f) { why = 2; return false; }
        return true;
    }
}
