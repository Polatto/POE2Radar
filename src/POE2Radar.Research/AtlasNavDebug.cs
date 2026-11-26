using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// <c>--atlas-nav-debug "&lt;tag&gt;" [--max-hops N]</c> — READ-ONLY. Runs the overlay's exact Nav-routing path
/// (<see cref="AtlasNav"/>: same Match, same sources, same BFS) for one rule token and explains every goal.
/// Also prints the status-byte histogram and compares candidate "accessible frontier" decodings against the
/// graph so the source set can be validated live. Run with the Atlas open.
/// </summary>
internal static class AtlasNavDebug
{
    public static int Run(MemoryReader reader, nint igs, string tag, int maxHops)
    {
        var atlas = new Poe2Atlas(reader);
        var nodes = atlas.ReadNodes(igs);
        for (var w = 0; (nodes.Count == 0 || !atlas.AllTagsResolved) && w < 200; w++) { Thread.Sleep(30); nodes = atlas.ReadNodes(igs); }
        if (nodes.Count == 0) { Console.Error.WriteLine("no atlas nodes (is the Atlas open?)"); return 1; }
        // Graph growth watch: the edge vector is re-read whenever its bounds change (GraphVersion bumps).
        var growth = new List<string> { $"v{atlas.GraphVersion}:{atlas.GraphNodeCount}" };
        for (var i = 0; i < 5; i++) { Thread.Sleep(200); nodes = atlas.ReadNodes(igs); growth.Add($"v{atlas.GraphVersion}:{atlas.GraphNodeCount}"); }
        Console.WriteLine("graph (version:verts) over 1s: " + string.Join(" ", growth));
        var cur = atlas.CurrentNodeGrid();
        Console.WriteLine($"nodes={nodes.Count} graphVerts={atlas.GraphNodeCount} allTags={atlas.AllTagsResolved} current={cur?.ToString() ?? "?"}");

        // ── status byte histogram ──
        Console.WriteLine("status histogram (nodeData+0x2CF):");
        foreach (var g in nodes.GroupBy(n => n.Status).OrderBy(g => g.Key))
            Console.WriteLine($"  0x{g.Key:X2}: {g.Count(),5}  vis={g.Count(n => n.Visible),5} fog={g.Count(n => !n.Visible),5}");

        // ── candidate frontiers ──
        var byGrid = new Dictionary<(int, int), Poe2Atlas.AtlasNodeLive>();
        foreach (var n in nodes) byGrid.TryAdd(n.Grid, n);
        var completed = nodes.Where(n => n.Completed).Select(n => n.Grid).ToHashSet();
        var adjCompleted = new HashSet<(int, int)>();
        var nb = Neighbours(atlas);
        foreach (var c in completed)
            if (nb.TryGetValue(c, out var l)) foreach (var x in l) if (!completed.Contains(x)) adjCompleted.Add(x);
        var bit0 = nodes.Where(n => n.Accessible).Select(n => n.Grid).ToHashSet();
        var bit0NotDone = nodes.Where(n => n.Accessible && !n.Completed).Select(n => n.Grid).ToHashSet();
        var low1 = nodes.Where(n => (n.Status & 3) == 1).Select(n => n.Grid).ToHashSet();
        Console.WriteLine($"frontier candidates: bit0={bit0.Count} bit0&!done={bit0NotDone.Count} (status&3)==1={low1.Count} completed={completed.Count} adjToCompleted&!done={adjCompleted.Count}");
        Console.WriteLine($"  bit0&!done ∩ adjToCompleted = {bit0NotDone.Intersect(adjCompleted).Count()}  bit0&!done adjacent-to-nothing-completed = {bit0NotDone.Except(adjCompleted).Count()}  adjToCompleted without bit0 = {adjCompleted.Except(bit0).Count()}");
        Console.WriteLine("  adjToCompleted status mix: " + string.Join(" ", adjCompleted.Where(byGrid.ContainsKey).GroupBy(g => byGrid[g].Status).OrderBy(g => g.Key).Select(g => $"0x{g.Key:X2}:{g.Count()}")));
        Console.WriteLine("  bit0&!done status mix:    " + string.Join(" ", bit0NotDone.GroupBy(g => byGrid[g].Status).OrderBy(g => g.Key).Select(g => $"0x{g.Key:X2}:{g.Count()}")));
        // hop distance of bit0&!done nodes from the completed set (a true frontier is all at distance 1)
        var distFromDone = Bfs(nb, completed);
        Console.WriteLine("  bit0&!done dist-from-completed: " + string.Join(" ", bit0NotDone.GroupBy(g => distFromDone.TryGetValue(g, out var d) ? d : -1).OrderBy(g => g.Key).Select(g => $"d{g.Key}:{g.Count()}")));

        // ── matching nodes ──
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tag };
        var matches = nodes.Where(n => AtlasNav.Match(set, n) != null).ToList();
        Console.WriteLine($"\nnodes matching \"{tag}\": {matches.Count}");
        var distFromCur = cur is { } cg ? Bfs(nb, new HashSet<(int, int)> { cg }) : new();
        foreach (var n in matches.OrderBy(n => distFromDone.TryGetValue(n.Grid, out var d) ? d : 9999))
            Console.WriteLine($"  grid={n.Grid,-11} map='{n.MapName}' code={n.MapCode,-28} matchedBy='{AtlasNav.Match(set, n)}' vis={(n.Visible ? 1 : 0)} status=0x{n.Status:X2} acc={(n.Accessible ? 1 : 0)} done={(n.Completed ? 1 : 0)} inGraph={(atlas.GraphHas(n.Grid) ? 1 : 0)} " +
                $"distFromCompleted={(distFromDone.TryGetValue(n.Grid, out var dd) ? dd : -1)} distFromCurrent={(distFromCur.TryGetValue(n.Grid, out var dc) ? dc : -1)}");

        // ── what the OLD overlay logic did (sources = every status-bit0 node) ──
        var goalsOld = matches.Where(n => !n.Completed).Select(n => n.Grid).ToList();
        var oldRoutes = atlas.RoutesFromSources(bit0.ToList(), goalsOld);
        Console.WriteLine($"\nraw BFS (sources = bit0 accessible, {bit0.Count}): goals={goalsOld.Count} routed={oldRoutes.Count} " +
            $"drawn(hops>0)={oldRoutes.Count(r => r.Value.Count > 1)} hop-histogram: " +
            string.Join(" ", oldRoutes.GroupBy(r => r.Value.Count - 1).OrderBy(g => g.Key).Select(g => $"h{g.Key}:{g.Count()}")));

        // ── the shared (overlay) path ──
        var plan = AtlasNav.Solve(atlas, nodes, set, maxHops, cur);
        Console.WriteLine($"\noverlay path (AtlasNav.Solve, maxHops={maxHops}): sources={plan.Sources.Count} goals={plan.Goals.Count} drawable={plan.Drawable.Count()}");
        foreach (var r in plan.Results)
            Console.WriteLine($"  goal {r.Goal,-11} {r.Status,-14} hops={r.Hops,3}" + (r.Path is { Count: > 0 } p ? $"  from {p[0]} via {string.Join("→", p.Skip(1).Take(8))}{(p.Count > 9 ? "→…" : "")}" : ""));
        return 0;
    }

    private static Dictionary<(int, int), List<(int, int)>> Neighbours(Poe2Atlas atlas) => atlas.GraphSnapshot();

    private static Dictionary<(int, int), int> Bfs(Dictionary<(int, int), List<(int, int)>> g, HashSet<(int, int)> seeds)
    {
        var dist = new Dictionary<(int, int), int>();
        var q = new Queue<(int, int)>();
        foreach (var s in seeds) if (dist.TryAdd(s, 0)) q.Enqueue(s);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            if (!g.TryGetValue(c, out var l)) continue;
            foreach (var n in l) if (dist.TryAdd(n, dist[c] + 1)) q.Enqueue(n);
        }
        return dist;
    }
}
