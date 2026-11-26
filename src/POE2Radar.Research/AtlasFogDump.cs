using System.Text;
using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// <c>--atlas-fogdump [--out file] [--grid 0x320] [--hexlen 0x100]</c> — READ-ONLY dump of EVERY atlas node
/// element, to answer "are fogged / unrevealed nodes' map + content readable, and is there a fog/visibility
/// field?". Uses only ReadProcessMemory through the read-only handle (PROCESS_VM_READ).
///
/// <para>Per node: element addr, grid coord, hop distance from the player's current node (over the canvas
/// connection graph), element visible bit, the +0x300 map row (code/name), the rolled map id from the
/// node-data struct, the +0x310 content row (headline + "map_atlas_node_has_*" stats), content-badge
/// strings in the child tree, a child visibility signature, and hex dumps of element[0..hexlen) plus the
/// node tail and node-data windows. A trailing analysis ranks byte offsets whose value best separates
/// near (hop 0-1) from far (hop &gt;= 3) nodes — those are the fog/revealed-state candidates.</para>
///
/// <para>Offsets are probed both at the 2026-09-05 values and at the pre-09-05 / 2026-11-18 values
/// (the 11-18 patch moved the UiElement tail back +0x18), so the dump stays useful if either is live.</para>
/// </summary>
internal static class AtlasFogDump
{
    private static nint Ptr(MemoryReader r, nint a)
        => r.TryReadStruct<nint>(a, out var p) && p >= 0x10000 && p < 0x7FFFFFFFFFFF ? p : 0;

    // UiElement base fields come from Poe2Offsets (Flags +0x168 confirmed live 2026-11-25 on a non-node element
    // and by the node visible-bit split). +0x180 is kept only as a comparison column ("visAlt").
    private const int FlagsOff = Poe2.UiElement.Flags, FlagsOffOld = 0x180, VisBit = Poe2.UiElement.FlagVisibleBit;
    private const int RelPos = Poe2.UiElement.RelativePos, Scale = Poe2.UiElement.LocalScaleMul;

    private sealed class Node
    {
        public nint El; public int Idx; public int Gx, Gy; public bool GridOk;
        public bool Vis, VisOld; public int Hop = -1;
        public string RowCode = "", RowName = "", Rolled = "", Headline = "";
        public List<string> Stats = new(), Badges = new(), TreeStrings = new(), Rumours = new();
        public string ChildSig = "";
        public byte[] Elem = Array.Empty<byte>();
        public byte[] Data = Array.Empty<byte>();
        public nint DataAddr;
        public float X, Y, Sc;
    }

    public static int Run(ProcessHandle process, MemoryReader reader, string outPath, int gridOff, int hexLen)
    {
        nint gsSlot = 0;
        foreach (var pat in AobPatterns.GameStateRefs)
        {
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { gsSlot = s; break; }
            if (gsSlot != 0) break;
        }
        if (gsSlot == 0) { Console.WriteLine("Chain not resolved."); return 1; }
        var live = new Poe2Live(reader, gsSlot);
        live.TryResolve(out var igs, out _, out _);
        var uiRoot = Ptr(reader, igs + Poe2.InGameState.UiRoot);
        var root = Ptr(reader, uiRoot + Poe2.UiElement.Parent) is var t && t != 0 ? t : uiRoot;
        if (root == 0) { Console.WriteLine("UI root null."); return 1; }

        // BFS whole UI tree, group by vtable.
        var byVt = new Dictionary<nint, List<nint>>();
        var q = new Queue<nint>(); q.Enqueue(root);
        var seen = new HashSet<nint>();
        while (q.Count > 0 && seen.Count < 300000)
        {
            var el = q.Dequeue();
            if (el == 0 || !seen.Add(el)) continue;
            if (Ptr(reader, el + Poe2.UiElement.Self) != el) continue;
            var vt = Ptr(reader, el);
            if (vt != 0) { if (!byVt.TryGetValue(vt, out var l)) byVt[vt] = l = new(); l.Add(el); }
            foreach (var c in Children(reader, el, 16384)) q.Enqueue(c);
        }

        // Node class = vtable with the most distinct in-range grid coords at gridOff.
        nint bestVt = 0; var bestDistinct = 0;
        foreach (var (vt, els) in byVt)
        {
            if (els.Count < 50) continue;
            var set = new HashSet<(int, int)>();
            foreach (var el in els)
                if (reader.TryReadStruct<int>(el + gridOff, out var x) && reader.TryReadStruct<int>(el + gridOff + 4, out var y)
                    && x is >= -256 and <= 256 && y is >= -256 and <= 512) set.Add((x, y));
            if (set.Count <= bestDistinct) continue;
            // Real atlas nodes carry a +0x300 EndgameMaps row whose first field → UTF-16 "MapXxx".
            var maps = els.Take(60).Count(el => Str(reader, Ptr(reader, Ptr(reader, el + 0x300))).StartsWith("Map", StringComparison.Ordinal));
            if (maps < 20) continue;
            bestDistinct = set.Count; bestVt = vt;
        }
        if (bestVt == 0 || bestDistinct < 20) { Console.WriteLine($"No atlas node elements found (grid +0x{gridOff:X}, +0x300 map row). Open the Atlas in game, then re-run."); return 1; }

        var parentCount = new Dictionary<nint, int>();
        foreach (var el in byVt[bestVt]) { var p = Ptr(reader, el + Poe2.UiElement.Parent); if (p != 0) parentCount[p] = parentCount.GetValueOrDefault(p) + 1; }
        var canvas = parentCount.OrderByDescending(k => k.Value).First().Key;
        var nodeEls = Children(reader, canvas, 20000).Where(c => Ptr(reader, c) == bestVt).ToList();
        var nodeSet = new HashSet<nint>(nodeEls);

        // Atlas open? (canvas + ancestors visible at either Flags offset)
        bool HierVis(nint el, int fo)
        {
            var cur = el; var g = 0;
            while (cur != 0 && g++ < 16)
            {
                if (!reader.TryReadStruct<uint>(cur + fo, out var fl) || ((fl >> VisBit) & 1) == 0) return false;
                var par = Ptr(reader, cur + Poe2.UiElement.Parent); if (par == cur) break; cur = par;
            }
            return true;
        }
        var atlasOpen = HierVis(canvas, FlagsOff);

        // Current-location marker: lone non-node element whose +off → a node (0x300 pre-09-05, 0x2E8 09-05).
        nint curNode = 0; var markerOff = 0;
        foreach (var off in new[] { 0x300, 0x2E8, 0x2F0, 0x2F8, 0x308 })
        {
            var hits = seen.Where(e => !nodeSet.Contains(e) && nodeSet.Contains(Ptr(reader, e + off))).Take(3).ToList();
            if (hits.Count == 1) { curNode = Ptr(reader, hits[0] + off); markerOff = off; break; }
        }

        // Connection graph: try both canvas offsets, keep the one with most valid endpoints.
        var nodesData = new List<Node>();
        var idx = 0;
        foreach (var el in nodeEls)
        {
            var n = new Node { El = el, Idx = idx++ };
            n.GridOk = reader.TryReadStruct<int>(el + gridOff, out n.Gx) & reader.TryReadStruct<int>(el + gridOff + 4, out n.Gy);
            reader.TryReadStruct<uint>(el + FlagsOff, out var fl); n.Vis = ((fl >> VisBit) & 1) != 0;
            reader.TryReadStruct<uint>(el + FlagsOffOld, out var flo); n.VisOld = ((flo >> VisBit) & 1) != 0;
            reader.TryReadStruct<float>(el + RelPos, out n.X); reader.TryReadStruct<float>(el + RelPos + 4, out n.Y);
            reader.TryReadStruct<float>(el + Scale, out n.Sc);
            n.Elem = new byte[Math.Max(hexLen, 0x380)];
            reader.TryReadBytes(el, n.Elem);

            var row = Ptr(reader, el + 0x300);
            if (row != 0)
            {
                n.RowCode = Str(reader, Ptr(reader, row));
                n.RowName = Str(reader, Ptr(reader, row + 0x08));
            }
            var storage = Ptr(reader, el + Poe2.AtlasNode.DataStorage);
            var nd = storage == 0 ? 0 : Ptr(reader, storage + Poe2.AtlasNode.DataModel);
            n.DataAddr = nd;
            if (nd != 0)
            {
                n.Data = new byte[0x300]; reader.TryReadBytes(nd, n.Data);
                var cur = nd + Poe2.AtlasNode.DataMapId;
                for (var hop = 0; hop < 4 && cur != 0; hop++)
                {
                    var s = reader.ReadStringUtf16(cur, 64);
                    if (s.StartsWith("Map", StringComparison.Ordinal)) { n.Rolled = s; break; }
                    cur = Ptr(reader, cur);
                }
            }
            // +0x310 content row (pre-09-05 layout): +0x38 → content row +0x30 name; +0x50 stats.
            var crow = Ptr(reader, el + 0x310);
            if (crow != 0)
            {
                var cr = Ptr(reader, crow + 0x38);
                if (cr != 0) n.Headline = Str(reader, Ptr(reader, cr + 0x30));
                var stats = Ptr(reader, crow + 0x50);
                if (stats != 0)
                {
                    var buf = new byte[0x100]; var got = reader.TryReadBytes(stats, buf);
                    for (var o = 0; o + 8 <= got; o += 8)
                    {
                        var p = (nint)BitConverter.ToInt64(buf, o);
                        if (p < 0x10000 || p >= 0x7FFFFFFFFFFF) continue;
                        var s = Ascii(reader, p);
                        if (!s.StartsWith("map_atlas_node", StringComparison.Ordinal)) { var pp = Ptr(reader, p); s = pp == 0 ? "" : Ascii(reader, pp); }
                        if (s.StartsWith("map_atlas_node", StringComparison.Ordinal) && !n.Stats.Contains(s)) n.Stats.Add(s);
                    }
                }
            }
            // Child tree: visibility signature + any "[Code|Display]"-style or name strings (depth <= 3).
            var sig = new StringBuilder();
            foreach (var c in Children(reader, el, 64))
            {
                reader.TryReadStruct<uint>(c + FlagsOff, out var cf);
                sig.Append(((cf >> VisBit) & 1) != 0 ? 'V' : 'h').Append(Children(reader, c, 64).Count).Append(' ');
            }
            n.ChildSig = sig.ToString().Trim();
            WalkStrings(reader, el, 0, "n", n);
            for (var off = 0x340; off < 0x3C0; off += 8)
                if (RumourAt(reader, el, off) is { } rs) n.Rumours.Add($"n{rs}");
            nodesData.Add(n);
        }

        var graph = new Dictionary<(int, int), List<(int, int)>>();
        var gridSet = new HashSet<(int, int)>(nodesData.Where(n => n.GridOk).Select(n => (n.Gx, n.Gy)));
        var bestEdgeOff = 0; var bestValid = -1; List<((int, int), (int, int))> bestEdges = new();
        foreach (var off in new[] { 0x5A8, 0x590, 0x5B0, 0x5A0, 0x598 })
        {
            var b = Ptr(reader, canvas + off); var e = Ptr(reader, canvas + off + 8);
            if (b == 0 || e <= b || (e - b) % 20 != 0 || (e - b) / 20 > 200000) continue;
            var buf = new byte[(int)(e - b)]; if (reader.TryReadBytes(b, buf) < buf.Length) continue;
            var edges = new List<((int, int), (int, int))>(); var valid = 0;
            for (var i = 0; i < buf.Length; i += 20)
            {
                var s = (BitConverter.ToInt32(buf, i + 4), BitConverter.ToInt32(buf, i + 8));
                var d = (BitConverter.ToInt32(buf, i + 12), BitConverter.ToInt32(buf, i + 16));
                edges.Add((s, d)); if (gridSet.Contains(s) && gridSet.Contains(d)) valid++;
            }
            if (valid > bestValid) { bestValid = valid; bestEdgeOff = off; bestEdges = edges; }
        }
        foreach (var (s, d) in bestEdges)
        {
            if (s == d) continue;
            (graph.TryGetValue(s, out var l1) ? l1 : graph[s] = new()).Add(d);
            (graph.TryGetValue(d, out var l2) ? l2 : graph[d] = new()).Add(s);
        }
        var curN = nodesData.FirstOrDefault(n => n.El == curNode);
        if (curN != null && curN.GridOk)
        {
            var dist = new Dictionary<(int, int), int> { [(curN.Gx, curN.Gy)] = 0 };
            var bq = new Queue<(int, int)>(); bq.Enqueue((curN.Gx, curN.Gy));
            while (bq.Count > 0)
            {
                var c = bq.Dequeue();
                if (!graph.TryGetValue(c, out var nb)) continue;
                foreach (var x in nb) if (!dist.ContainsKey(x)) { dist[x] = dist[c] + 1; bq.Enqueue(x); }
            }
            foreach (var n in nodesData) if (n.GridOk && dist.TryGetValue((n.Gx, n.Gy), out var h)) n.Hop = h;
        }

        // ── write ──
        var sb = new StringBuilder();
        sb.AppendLine($"ATLAS FOG DUMP  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  (read-only)");
        sb.AppendLine($"node vtable 0x{bestVt:X} (module +0x{(long)bestVt - (long)process.MainModuleBase:X})  canvas 0x{canvas:X}  nodes on canvas {nodesData.Count}  gridOff +0x{gridOff:X}");
        sb.AppendLine($"atlas open (canvas hier-visible @Flags+0x{FlagsOff:X}) = {atlasOpen}");
        sb.AppendLine($"current marker off +0x{markerOff:X} → node 0x{curNode:X} grid {(curN != null ? $"({curN.Gx},{curN.Gy}) {curN.RowName}" : "?")}");
        sb.AppendLine($"graph: canvas +0x{bestEdgeOff:X}  {bestEdges.Count} edges, {bestValid} with both endpoints on nodes, {graph.Count} vertices");
        int Cnt(Func<Node, bool> f) => nodesData.Count(f);
        sb.AppendLine($"rowCode {Cnt(n => n.RowCode.Length > 0)}  rolled {Cnt(n => n.Rolled.Length > 0)}  headline {Cnt(n => n.Headline.Length > 0)}  stats {Cnt(n => n.Stats.Count > 0)}  badges {Cnt(n => n.Badges.Count > 0)}  vis {Cnt(n => n.Vis)}");
        sb.AppendLine("\nPer hop bucket: count | with rolled map | with headline | with stats | with badges | elem-visible");
        foreach (var g in nodesData.GroupBy(n => Bucket(n.Hop)).OrderBy(g => g.Key))
            sb.AppendLine($"  {g.Key,-8} {g.Count(),4} | {g.Count(n => n.Rolled.Length > 0),4} | {g.Count(n => n.Headline.Length > 0),4} | {g.Count(n => n.Stats.Count > 0),4} | {g.Count(n => n.Badges.Count > 0),4} | {g.Count(n => n.Vis),4}");

        // Fog view: element local-visible bit × state byte (+0x32C) × status low bits (+0x32F & 7), with content.
        bool HasContent(Node n) => n.Headline.Length > 0 || n.Badges.Count > 0 || n.TreeStrings.Any(s => s.Contains("+2E8="));
        sb.AppendLine("\nvis | state+0x32C | status+0x32F&7 : count (with map, with content)");
        foreach (var g in nodesData.GroupBy(n => (n.Vis, n.Elem[0x32C], n.Elem[0x32F] & 7)).OrderBy(g => g.Key))
            sb.AppendLine($"  {(g.Key.Vis ? 1 : 0)} | 0x{g.Key.Item2:X2} | {g.Key.Item3} : {g.Count()} ({g.Count(n => n.Rolled.Length > 0)}, {g.Count(HasContent)})");

        // Discriminator analysis: byte offsets whose value separates near (hop<=1) from far (hop>=3).
        sb.AppendLine("\nFOG CANDIDATES — byte offsets separating NEAR (hop 0-1) vs FAR (hop>=3); score = |P(v|near)-P(v|far)| for the best value v");
        var near = nodesData.Where(n => n.Hop is 0 or 1).ToList();
        var far = nodesData.Where(n => n.Hop >= 3).ToList();
        void Rank(string label, Func<Node, byte[]> src, int lo, int hi)
        {
            if (near.Count < 3 || far.Count < 3) { sb.AppendLine($"  {label}: not enough near/far nodes (near {near.Count}, far {far.Count})"); return; }
            var res = new List<(int Off, double Score, string Desc)>();
            for (var o = lo; o < hi; o++)
            {
                var nv = near.Where(n => src(n).Length > o).Select(n => src(n)[o]).ToList();
                var fv = far.Where(n => src(n).Length > o).Select(n => src(n)[o]).ToList();
                if (nv.Count == 0 || fv.Count == 0) continue;
                var vals = nv.Concat(fv).Distinct().ToList();
                if (vals.Count > 16) continue; // pointers / floats — not a flag
                double best = 0; byte bv = 0;
                foreach (var v in vals)
                {
                    var d = Math.Abs(nv.Count(x => x == v) / (double)nv.Count - fv.Count(x => x == v) / (double)fv.Count);
                    if (d > best) { best = d; bv = v; }
                }
                string Hist(List<byte> l) => string.Join(",", l.GroupBy(x => x).OrderByDescending(x => x.Count()).Take(4).Select(x => $"{x.Key:X2}x{x.Count()}"));
                res.Add((o, best, $"near[{Hist(nv)}] far[{Hist(fv)}] key=0x{bv:X2}"));
            }
            sb.AppendLine($"  {label}:");
            foreach (var r in res.OrderByDescending(r => r.Score).Take(15))
                sb.AppendLine($"    +0x{r.Off:X3}  {r.Score:0.00}  {r.Desc}");
        }
        Rank("element 0x000..0x380", n => n.Elem, 0, 0x380);
        Rank("nodeData 0x000..0x300", n => n.Data, 0, 0x300);

        sb.AppendLine("\nNODES (sorted by hop):");
        foreach (var n in nodesData.OrderBy(n => n.Hop < 0 ? int.MaxValue : n.Hop).ThenBy(n => n.Idx))
        {
            sb.AppendLine($"--- #{n.Idx} el 0x{n.El:X} grid ({n.Gx},{n.Gy}) hop {n.Hop} vis {(n.Vis ? 1 : 0)} visAlt {(n.VisOld ? 1 : 0)} state 0x{n.Elem[0x32C]:X2} status 0x{n.Elem[0x32F]:X2} relPos ({n.X.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)};{n.Y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}) sc {n.Sc.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"    row: {n.RowCode} / {n.RowName}   rolled: {n.Rolled}   e[32C..33F]: {Hex(n.Elem, 0x32C, 0x14)}   data 0x{n.DataAddr:X}");
            sb.AppendLine($"    headline: {n.Headline}   stats: {string.Join(", ", n.Stats)}");
            sb.AppendLine($"    badges: {string.Join(" | ", n.Badges)}");
            sb.AppendLine($"    rumours: {string.Join(" | ", n.Rumours)}");
            sb.AppendLine($"    childSig: {n.ChildSig}   treeStrings: {string.Join(" | ", n.TreeStrings.Take(12))}");
            for (var o = 0; o < hexLen && o < n.Elem.Length; o += 0x20) sb.AppendLine($"    e+{o:X3}: {Hex(n.Elem, o, 0x20)}");
            for (var o = 0x2E0; o < 0x380; o += 0x20) sb.AppendLine($"    e+{o:X3}: {Hex(n.Elem, o, 0x20)}");
            if (n.Data.Length > 0) for (var o = 0x280; o < 0x300; o += 0x20) sb.AppendLine($"    d+{o:X3}: {Hex(n.Data, o, 0x20)}");
        }
        // ── Expedition / rumour summary ──
        string[] keys = { "Uncharted", "Expedition", "Waters", "drink", "fishy", "cold", "ice", "rumour", "boat" };
        sb.AppendLine("\n=== EXPEDITION / RUMOUR NODES ===");
        bool RumKey(Node n) => keys.Any(k => n.Rumours.Any(r => r.Contains(k, StringComparison.OrdinalIgnoreCase)));
        bool RowKey(Node n) => keys.Any(k => n.RowCode.Contains(k, StringComparison.OrdinalIgnoreCase) || n.RowName.Contains(k, StringComparison.OrdinalIgnoreCase));
        var rumourHits = nodesData.Where(RumKey).OrderBy(n => n.Gx).ThenBy(n => n.Gy).ToList();
        var rowHits = nodesData.Where(n => !RumKey(n) && RowKey(n)).OrderBy(n => n.Gx).ThenBy(n => n.Gy).ToList();
        sb.AppendLine($"{rumourHits.Count} node(s) with keyword in rumour strings; {rowHits.Count} more with keyword only in row code/name; {nodesData.Count(n => n.Rumours.Count > 0)} node(s) with any rumour string");
        sb.AppendLine("--- keyword in rumour strings ---");
        foreach (var n in rumourHits)
            sb.AppendLine($"  grid ({n.Gx},{n.Gy}) vis {(n.Vis ? 1 : 0)} hop {n.Hop} row {n.RowCode}/{n.RowName}  rumours: {string.Join(" | ", n.Rumours)}");
        sb.AppendLine("--- keyword only in row code/name (row | grid | vis | hop) ---");
        foreach (var n in rowHits)
            sb.AppendLine($"  grid ({n.Gx},{n.Gy}) vis {(n.Vis ? 1 : 0)} hop {n.Hop} row {n.RowCode}");
        sb.AppendLine("--- all nodes with rumour strings ---");
        foreach (var n in nodesData.Where(n => n.Rumours.Count > 0).OrderBy(n => n.Gx).ThenBy(n => n.Gy))
            sb.AppendLine($"  grid ({n.Gx},{n.Gy}) vis {(n.Vis ? 1 : 0)} hop {n.Hop} row {n.RowCode}  rumours: {string.Join(" | ", n.Rumours)}");
        if (!rumourHits.Any(n => n.Rumours.Any(r => r.Contains("Uncharted") || r.Contains("drink") || r.Contains("Cold as"))))
        {
            sb.AppendLine("--- NO rumour strings found: el+0x000..0x600 pointer->(+8|+0)->string scan, non-row strings ---");
            var perOff = new SortedDictionary<int, Dictionary<string, int>>();
            foreach (var n in nodesData)
                for (var off = 0; off < 0x600; off += 8)
                    if (RumourAt(reader, n.El, off) is { } rs)
                    {
                        var qi = rs.IndexOf('"'); var str = rs[(qi + 1)..^1];
                        if (str == n.RowCode || str == n.RowName || str == n.Rolled) continue;
                        if (!perOff.TryGetValue(off, out var d)) perOff[off] = d = new();
                        d[rs] = d.GetValueOrDefault(rs) + 1;
                    }
            foreach (var (off, d) in perOff)
                sb.AppendLine($"  +0x{off:X3}: {string.Join(" ; ", d.OrderByDescending(x => x.Value).Take(5).Select(x => $"{x.Key} x{x.Value}"))}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, sb.ToString());
        // Console: header + buckets + candidates only.
        var head = sb.ToString(); var cut = head.IndexOf("\nNODES", StringComparison.Ordinal);
        Console.WriteLine(cut > 0 ? head[..cut] : head);
        Console.WriteLine(head[head.IndexOf("\n=== EXPEDITION", StringComparison.Ordinal)..]);
        Console.WriteLine($"\nwrote {outPath}");
        return 0;
    }

    private static string Bucket(int hop) => hop switch { < 0 => "z-unreach", 0 => "0-cur", 1 => "1-adj", 2 => "2", <= 5 => "3-5", <= 10 => "6-10", _ => "11+" };

    private static List<nint> Children(MemoryReader r, nint el, int max)
    {
        var res = new List<nint>();
        var b = Ptr(r, el + Poe2.UiElement.Children); var e = Ptr(r, el + Poe2.UiElement.ChildrenEnd);
        if (b == 0 || e <= b) return res;
        var n = (int)((e - b) / 8); if (n > max) return res;
        for (var i = 0; i < n; i++) { var c = Ptr(r, b + i * 8); if (c != 0) res.Add(c); }
        return res;
    }

    private static void WalkStrings(MemoryReader r, nint el, int depth, string path, Node n)
    {
        if (depth > 0)
            for (var off = 0x280; off <= 0x3A0; off += 8)
            {
                var p = Ptr(r, el + off); if (p == 0) continue;
                var s = r.ReadStringUtf16(p, 96);
                if (s.Length is < 3 or > 96 || !s.All(c => c is >= ' ' and <= '~')) continue;
                if (s.StartsWith('[') && s.Contains('|') && !n.Badges.Contains(s)) n.Badges.Add(s);
                else if (n.TreeStrings.Count < 40) n.TreeStrings.Add($"{path}+{off:X}='{s}'");
            }
        if (depth > 0)
            for (var off = 0x2B0; off <= 0x2E0; off += 8)
                if (RumourAt(r, el, off) is { } rs && n.Rumours.Count < 60) n.Rumours.Add($"{path}{rs}");
        if (depth >= 3) return;
        var kids = Children(r, el, 64);
        for (var i = 0; i < kids.Count && i < 32; i++) WalkStrings(r, kids[i], depth + 1, $"{path}.{i}", n);
    }

    /// <summary>Slot at el+off is a pointer whose +8 (or +0) is a pointer to a printable UTF-16 string (2..200). Returns +off>+sub="..".</summary>
    private static string? RumourAt(MemoryReader r, nint el, int off)
    {
        var p = Ptr(r, el + off); if (p == 0) return null;
        foreach (var sub in new[] { 0x08, 0x00 })
        {
            var q = Ptr(r, p + sub); if (q == 0) continue;
            var s = r.ReadStringUtf16(q, 201);
            if (s.Length is < 2 or > 200 || !s.All(c => c is >= ' ' and <= '~' or '‘' or '’' or '“' or '”' or '…')) continue;
            return $"+{off:X}>+{sub:X2}=\"{s}\"";
        }
        return null;
    }

    private static string Str(MemoryReader r, nint p)
    {
        if (p == 0) return "";
        var s = r.ReadStringUtf16(p, 64);
        return s.Length >= 2 && s.All(c => c >= ' ' && c != '�') ? s : "";
    }

    private static string Ascii(MemoryReader r, nint p)
    {
        var b = new byte[80]; var n = r.TryReadBytes(p, b); var sb = new StringBuilder();
        for (var i = 0; i < n && b[i] is >= 0x20 and < 0x7F; i++) sb.Append((char)b[i]);
        return sb.ToString();
    }

    private static string Hex(byte[] b, int off, int len)
    {
        var sb = new StringBuilder(len * 3);
        for (var i = off; i < off + len && i < b.Length; i++) { sb.Append(b[i].ToString("X2")); sb.Append(((i - off) & 7) == 7 ? "  " : " "); }
        return sb.ToString().TrimEnd();
    }
}
