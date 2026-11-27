using POE2Radar.Core.Game;
using POE2Radar.Core;
namespace POE2Radar.Research;
// Read-only: mirrors Poe2Atlas.DetectNodeClass and prints why detection succeeds/fails.
internal static class AtlasDetectDebug
{
    public static int Run(MemoryReader r, nint igs, nint moduleBase)
    {
        nint P(nint a) => r.TryReadStruct<nint>(a, out var v) ? v : 0;
        var uiRoot = P(igs + Poe2.InGameState.UiRoot);
        var root = P(uiRoot + Poe2.UiElement.Parent) is var tr && tr != 0 ? tr : uiRoot;
        var q = new Queue<nint>(); q.Enqueue(root);
        var visited = new HashSet<nint>(); var byVt = new Dictionary<nint, List<nint>>();
        while (q.Count > 0 && visited.Count < 200000)
        {
            var el = q.Dequeue();
            if (el == 0 || !visited.Add(el) || P(el + Poe2.UiElement.Self) != el) continue;
            var vt = P(el); if (vt != 0) (byVt.TryGetValue(vt, out var l) ? l : byVt[vt] = new()).Add(el);
            var first = P(el + Poe2.UiElement.Children);
            if (first != 0 && r.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last))
            { var n = ((long)last - (long)first) / 8; if (n is > 0 and <= 16384) for (long k = 0; k < n; k++) q.Enqueue(P(first + (nint)(k * 8))); }
        }
        Console.WriteLine($"uiRoot 0x{uiRoot:X} root 0x{root:X} visited {visited.Count} queueLeft {q.Count} vtables {byVt.Count}");
        var nodeVt = moduleBase + 0x30F5718;
        foreach (var (vt, list) in byVt.Where(kv => kv.Value.Count >= 50).OrderByDescending(kv => kv.Value.Count).Take(25))
        {
            var coords = new HashSet<(int, int)>(); var inRange = 0;
            foreach (var el in list)
            {
                if (!r.TryReadStruct<int>(el + Poe2.AtlasNode.GridPos, out var gx) || !r.TryReadStruct<int>(el + Poe2.AtlasNode.GridPos + 4, out var gy)) continue;
                if (gx is < -200 or > 200 || gy is < -200 or > 200) continue; inRange++; coords.Add((gx, gy));
            }
            Console.WriteLine($"  vt mod+0x{(long)vt - (long)moduleBase:X}{(vt == nodeVt ? " <== NODE" : "")} count {list.Count} inRange {inRange} distinct {coords.Count}");
        }
        if (byVt.TryGetValue(nodeVt, out var nodes))
        {
            foreach (var g in nodes.GroupBy(e => P(e + Poe2.UiElement.Parent)).OrderByDescending(g => g.Count()).Take(6))
            {
                r.TryReadStruct<uint>(g.Key + Poe2.UiElement.Flags, out var fl);
                Console.WriteLine($"  parent 0x{g.Key:X} children-of-node-class {g.Count()} visibleBit {(fl >> Poe2.UiElement.FlagVisibleBit) & 1}");
                var cur = g.Key; for (var i = 0; i < 16 && cur != 0; i++) { r.TryReadStruct<uint>(cur + Poe2.UiElement.Flags, out var f2); var par = P(cur + Poe2.UiElement.Parent); Console.WriteLine($"     anc{i} 0x{cur:X} vt mod+0x{(long)P(cur) - (long)moduleBase:X} vis {(f2 >> Poe2.UiElement.FlagVisibleBit) & 1}"); if (par == cur) break; cur = par; }
            }
        }
        else Console.WriteLine("  node vtable NOT found in BFS");
        return 0;
    }
}
