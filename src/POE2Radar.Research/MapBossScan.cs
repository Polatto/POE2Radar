using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// --map-boss-scan: can the map boss be located the moment an endgame map loads?
/// (1) every terrain tile whose .tdt path looks boss-related (boss/arena/checkpoint/...), clustered,
///     with grid centroid + distance from the player (tiles cover the WHOLE map at load);
/// (2) every entity in BOTH the Awake and Sleeping std::maps whose metadata looks boss/checkpoint/arena
///     related, plus all MinimapIcon (POI) entities, with distance — shows what exists outside the bubble.
/// Read-only.
/// </summary>
internal static class MapBossScan
{
    private static readonly string[] TileKeys = { "boss", "arena", "checkpoint", "throne", "summit", "altar" };
    private static readonly string[] EntKeys = { "boss", "checkpoint", "arena", "waypoint", "portal", "transition", "unique" };

    public static int Run(ProcessHandle process, MemoryReader reader, string? extraNeedle)
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
        live.TryResolve(out _, out var ai, out var lp);
        var code = live.AreaCode(ai);
        var pg = live.PlayerGrid(lp) ?? default;
        Console.WriteLine($"Area '{code}' ({live.AreaName(ai)}) lvl {live.AreaLevel(ai)}  player grid ({pg.X:F0},{pg.Y:F0})");

        // ── (1) tiles ───────────────────────────────────────────────────────────
        var terrain = ai + Poe2.AreaInstance.TerrainMetadata;
        reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX);
        var first = Ptr(reader, terrain + Poe2.Terrain.TileDetailsPtr);
        reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var last);
        var count = first == 0 ? 0 : ((long)last - (long)first) / Poe2.TileStructureSize;
        Console.WriteLine($"\n[1] tiles: tilesX={tilesX} count={count}");
        var pathCache = new Dictionary<nint, string>();
        var cells = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        for (long i = 0; i < count && tilesX > 0; i++)
        {
            var tgt = Ptr(reader, first + (nint)(i * Poe2.TileStructureSize) + Poe2.TileStructure.TgtFilePtr);
            if (tgt == 0) continue;
            if (!pathCache.TryGetValue(tgt, out var p)) pathCache[tgt] = p = ReadStdWString(reader, tgt + Poe2.TgtFileStruct.TgtPath);
            if (p.Length == 0) continue;
            var lower = p.ToLowerInvariant();
            var hit = TileKeys.Any(lower.Contains) || lower.Contains("/maps/")
                      || (extraNeedle != null && lower.Contains(extraNeedle.ToLowerInvariant()));
            if (!hit) continue;
            (cells.TryGetValue(p, out var l) ? l : cells[p] = new()).Add(((int)(i % tilesX), (int)(i / tilesX)));
        }
        var cell = Poe2.Terrain.TileGridCells;
        foreach (var (p, cs) in cells.OrderBy(k => k.Key))
        {
            var clusters = Cluster(cs, 2);
            Console.WriteLine($"  {cs.Count,5} tiles  {clusters.Count} cluster(s)  {p}");
            foreach (var c in clusters.Take(6))
            {
                var cx = (float)c.Average(t => t.Item1) * cell; var cy = (float)c.Average(t => t.Item2) * cell;
                var d = MathF.Sqrt((cx - pg.X) * (cx - pg.X) + (cy - pg.Y) * (cy - pg.Y));
                Console.WriteLine($"         @ grid ({cx:F0},{cy:F0})  n={c.Count}  dist={d:F0}");
            }
        }

        // ── (2) entities (Awake + Sleeping) ─────────────────────────────────────
        foreach (var (label, off) in new[] { ("Awake", Poe2.AreaInstance.AwakeEntities), ("Sleeping", Poe2.AreaInstance.SleepingEntities) })
        {
            var head = Ptr(reader, ai + off);
            reader.TryReadStruct<int>(ai + off + 8, out var size);
            Console.WriteLine($"\n[2] {label} entities: size={size}");
            if (head == 0 || size <= 0 || size > 200000) continue;
            var q = new Queue<nint>(); q.Enqueue(Ptr(reader, head + Poe2.StdMapNode.Parent));
            var seen = new HashSet<nint>();
            var metaCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            float maxD = 0; int real = 0;
            while (q.Count > 0 && seen.Count < 300000)
            {
                var n = q.Dequeue();
                if (n == 0 || n == head || !seen.Add(n)) continue;
                if (!reader.TryReadStruct<byte>(n + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
                reader.TryReadStruct<uint>(n + Poe2.StdMapNode.KeyId, out var id);
                var e = Ptr(reader, n + Poe2.StdMapNode.ValueEntityPtr);
                q.Enqueue(Ptr(reader, n + Poe2.StdMapNode.Left)); q.Enqueue(Ptr(reader, n + Poe2.StdMapNode.Right));
                if (e == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;
                real++;
                var details = Ptr(reader, e + Poe2.Entity.EntityDetailsPtr);
                var meta = details == 0 ? "" : ReadStdWString(reader, details + Poe2.EntityDetails.Name);
                var render = Component(reader, e, "Render");
                float gx = float.NaN, gy = float.NaN, d = float.NaN;
                if (render != 0 && reader.TryReadStruct<System.Numerics.Vector3>(render + Poe2.Render.CurrentWorldPosition, out var w))
                {
                    gx = w.X / Poe2.WorldToGridRatio; gy = w.Y / Poe2.WorldToGridRatio;
                    d = MathF.Sqrt((gx - pg.X) * (gx - pg.X) + (gy - pg.Y) * (gy - pg.Y));
                    if (d > maxD && d < 100000) maxD = d;
                }
                var shortMeta = meta.Contains('@') ? meta[..meta.IndexOf('@')] : meta;
                metaCounts[shortMeta] = metaCounts.GetValueOrDefault(shortMeta) + 1;
                var lower = meta.ToLowerInvariant();
                var icon = Component(reader, e, "MinimapIcon") != 0;
                if (icon || EntKeys.Any(lower.Contains) || (extraNeedle != null && lower.Contains(extraNeedle.ToLowerInvariant())))
                    Console.WriteLine($"  id={id,-7} {(icon ? "POI " : "    ")} grid=({gx:F0},{gy:F0}) dist={d:F0}  {meta}");
            }
            Console.WriteLine($"  real entities={real}  max dist from player={maxD:F0}");
            if (HasArg("--all"))
                foreach (var (m, c) in metaCounts.OrderBy(k => k.Key)) Console.WriteLine($"    {c,4}  {m}");
        }
        return 0;
    }

    private static bool HasArg(string a) => Environment.GetCommandLineArgs().Contains(a);

    private static List<List<(int, int)>> Cluster(List<(int, int)> cells, int gap)
    {
        var set = new HashSet<(int, int)>(cells); var vis = new HashSet<(int, int)>(); var res = new List<List<(int, int)>>();
        foreach (var s in cells)
        {
            if (!vis.Add(s)) continue;
            var c = new List<(int, int)>(); var q = new Queue<(int, int)>(); q.Enqueue(s);
            while (q.Count > 0)
            {
                var (x, y) = q.Dequeue(); c.Add((x, y));
                for (var dx = -gap; dx <= gap; dx++) for (var dy = -gap; dy <= gap; dy++)
                { var nb = (x + dx, y + dy); if (set.Contains(nb) && vis.Add(nb)) q.Enqueue(nb); }
            }
            res.Add(c);
        }
        return res.OrderByDescending(c => c.Count).ToList();
    }

    private static nint Component(MemoryReader r, nint entity, string name)
    {
        var details = Ptr(r, entity + Poe2.Entity.EntityDetailsPtr);
        var lookup = details == 0 ? 0 : Ptr(r, details + Poe2.EntityDetails.ComponentLookUpPtr);
        if (lookup == 0) return 0;
        var cFirst = Ptr(r, entity + Poe2.Entity.ComponentList);
        r.TryReadStruct<nint>(entity + Poe2.Entity.ComponentList + 8, out var cLast);
        var compCount = ((long)cLast - (long)cFirst) / 8;
        if (cFirst == 0 || compCount is <= 0 or > 256) return 0;
        var bFirst = Ptr(r, lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
        r.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast);
        var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
        if (bFirst == 0 || entries is <= 0 or > 256) return 0;
        for (long i = 0; i < entries; i++)
        {
            var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
            if (!r.TryReadStruct<int>(e + 8, out var idx) || idx < 0 || idx >= compCount) continue;
            if (r.ReadStringUtf8(Ptr(r, e), 32) != name) continue;
            return Ptr(r, cFirst + (nint)(idx * 8));
        }
        return 0;
    }

    private static string ReadStdWString(MemoryReader r, nint addr)
    {
        if (!r.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return string.Empty;
        if (len < 8) return r.ReadStringUtf16(addr, len);
        var p = Ptr(r, addr);
        return p == 0 ? string.Empty : r.ReadStringUtf16(p, len);
    }

    private static nint Ptr(MemoryReader r, nint addr)
    {
        if (!r.TryReadStruct<nint>(addr, out var p)) return 0;
        var u = (ulong)p;
        return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? 0 : p;
    }
}
