namespace POE2Radar.Core.Game;

/// <summary>
/// Atlas auto-routing ("Nav" rules) — the single implementation shared by the overlay (RadarApp.UpdateAtlas)
/// and the Research probe (<c>--atlas-nav-debug</c>), so what the probe reports is exactly what the overlay draws.
/// Pure logic over a <see cref="Poe2Atlas.ReadNodes"/> snapshot + the cached connection graph; no memory reads.
/// </summary>
public static class AtlasNav
{
    /// <summary>A node matches a rule set if its map name, one of its content tags, its map-archetype KIND
    /// (Citadel/Boss/Tower/Unique/Merchant), its maps.json type, or one of its maps.json tags is in the set.
    /// Returns the matched token (drives label + colour), or null.</summary>
    public static string? Match(ISet<string> set, in Poe2Atlas.AtlasNodeLive nd)
    {
        if (set.Count == 0) return null;
        if (!string.IsNullOrEmpty(nd.MapName) && set.Contains(nd.MapName)) return nd.MapName;
        // Internal MapId too (e.g. a rule saved as "MapMothersoul_Female"), so id-keyed rules still match.
        if (!string.IsNullOrEmpty(nd.MapCode) && set.Contains(nd.MapCode)) return nd.MapCode;
        if (nd.Tags is { Count: > 0 }) foreach (var t in nd.Tags) if (set.Contains(t)) return t;
        if (!string.IsNullOrEmpty(nd.Kind) && nd.Kind != "Normal" && set.Contains(nd.Kind)) return nd.Kind;
        if (!string.IsNullOrEmpty(nd.MapType) && set.Contains(nd.MapType)) return nd.MapType;
        if (nd.MapDataTags is { Count: > 0 }) foreach (var t in nd.MapDataTags) if (set.Contains(t)) return t;
        return null;
    }

    /// <summary>Route SOURCES: every status-bit0 ("accessible") node. bit0 is set on completed nodes too
    /// (0x03/0x13), so this is completed ∪ the runnable frontier; the BFS therefore starts each route at the
    /// nearest runnable map. Validated live 2026-11-25 (<c>--atlas-nav-debug</c>): of 291 bit0 non-completed
    /// nodes, 280 are graph-adjacent to a completed node (the rest are 2-3 hops away, e.g. the town/quest
    /// spurs), and (status&amp;3)==1 selects exactly the same 291. Falls back to the current-node marker when
    /// nothing is accessible (fresh atlas).</summary>
    public static List<(int X, int Y)> Sources(Poe2Atlas atlas, IReadOnlyList<Poe2Atlas.AtlasNodeLive> nodes, (int X, int Y)? curGrid)
    {
        var src = new List<(int X, int Y)>();
        foreach (var n in nodes) if (n.Accessible && atlas.GraphHas(n.Grid)) src.Add(n.Grid);
        if (src.Count == 0 && curGrid is { } c && atlas.GraphHas(c)) src.Add(c);
        return src;
    }

    public sealed record GoalResult((int X, int Y) Goal, List<(int X, int Y)>? Path, int Hops, string Status);

    public sealed record Plan(List<(int X, int Y)> Sources, List<(int X, int Y)> Goals, List<GoalResult> Results)
    {
        /// <summary>The goals that produce a drawable route (Status == "ok"), nearest first.</summary>
        public IEnumerable<GoalResult> Drawable => Results.Where(r => r.Status == "ok");
    }

    /// <summary>Solve routes from <see cref="Sources"/> to every non-completed node matching
    /// <paramref name="navSet"/>. Every goal gets a result with a reason (ok / not-in-graph / unreachable /
    /// too-many-hops / is-source). <paramref name="cap"/> limits the "ok" results (view readability).</summary>
    public static Plan Solve(Poe2Atlas atlas, IReadOnlyList<Poe2Atlas.AtlasNodeLive> nodes, ISet<string> navSet,
        int maxHops, (int X, int Y)? curGrid, int cap = 30)
    {
        var goals = new List<(int X, int Y)>();
        foreach (var n in nodes)
            if (!n.Completed && Match(navSet, n) != null) goals.Add(n.Grid);
        var sources = goals.Count > 0 ? Sources(atlas, nodes, curGrid) : new List<(int X, int Y)>();
        var results = new List<GoalResult>(goals.Count);
        if (goals.Count == 0) return new Plan(sources, goals, results);

        var routes = sources.Count > 0 ? atlas.RoutesFromSources(sources, goals) : new Dictionary<(int X, int Y), List<(int X, int Y)>>();
        var srcSet = new HashSet<(int X, int Y)>(sources);
        foreach (var g in goals)
        {
            if (!atlas.GraphHas(g)) { results.Add(new GoalResult(g, null, -1, "not-in-graph")); continue; }
            if (sources.Count == 0) { results.Add(new GoalResult(g, null, -1, "no-sources")); continue; }
            if (!routes.TryGetValue(g, out var path)) { results.Add(new GoalResult(g, null, -1, "unreachable")); continue; }
            var hops = path.Count - 1;
            if (hops <= 0 || srcSet.Contains(g)) { results.Add(new GoalResult(g, path, hops, "is-source")); continue; }
            if (maxHops > 0 && hops > maxHops) { results.Add(new GoalResult(g, path, hops, "too-many-hops")); continue; }
            results.Add(new GoalResult(g, path, hops, "ok"));
        }
        results.Sort((a, b) => (a.Status == "ok" ? 0 : 1).CompareTo(b.Status == "ok" ? 0 : 1) is var c && c != 0 ? c : a.Hops.CompareTo(b.Hops));
        var okSeen = 0;
        for (var i = 0; i < results.Count; i++)
            if (results[i].Status == "ok" && ++okSeen > cap) results[i] = results[i] with { Status = "capped" };
        return new Plan(sources, goals, results);
    }
}
