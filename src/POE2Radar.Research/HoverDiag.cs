using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>--hover-diag [--watch N] [--slot 0x4E0]: diagnose the hover-price / ritual-chip item-slot read.
/// Static pass (no hover needed): walks the WHOLE UI tree from GameUi and body-scans every element for a
/// pointer to a Metadata/Items entity, histogramming the field offset — the live "item-slot → item" field
/// (committed <see cref="Poe2.Ritual.TileSlotItem"/>). Open the inventory / stash / ritual window first so
/// their slot elements are built + visible.
/// --watch N: then polls for N seconds: (a) the committed <see cref="Poe2Live.ReadHoveredItem"/> and (b) the
/// same cursor-in-rect walk using the CANDIDATE offset --slot (default = committed).</summary>
internal static class HoverDiag
{
    private static nint Ptr(MemoryReader r, nint a) => r.TryReadStruct<nint>(a, out var v) ? v : 0;
    private static bool Plaus(nint p) => (ulong)p is >= 0x10000 and <= 0x7FFFFFFFFFFF;

    private static string Meta(MemoryReader r, nint entity)
    {
        var d = Ptr(r, entity + Poe2.Entity.EntityDetailsPtr);
        if (!Plaus(d)) return "";
        var a = d + Poe2.EntityDetails.Name;
        if (!r.TryReadStruct<int>(a + 0x10, out var len) || len <= 0 || len > 512) return "";
        if (len < 8) return r.ReadStringUtf16(a, len);
        var p = Ptr(r, a);
        return Plaus(p) ? r.ReadStringUtf16(p, len) : "";
    }

    public static int Run(ProcessHandle process, MemoryReader reader, int watchSecs, int slotOff)
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
        Console.WriteLine($"InGameState 0x{igs:X}  GameUi 0x{uiRoot:X}");
        if (uiRoot == 0) return 1;

        float winW = 2560, winH = 1600; var hwnd = Win.FindMainWindowForPid((uint)process.ProcessId);
        if (hwnd != 0 && Win.GetClientRect(hwnd, out var rc) && rc.right > 0) { winW = rc.right; winH = rc.bottom; }
        Console.WriteLine($"window {winW}x{winH}");

        var visBit = 1u << Poe2.UiElement.FlagVisibleBit;
        var hist = new Dictionary<int, int>();
        var visHist = new Dictionary<int, int>();
        var samples = new Dictionary<int, List<(nint el, nint item, string meta, bool chainVis)>>();
        var cache = new Dictionary<nint, string>();
        var q = new Queue<(nint el, bool chainVis)>(); q.Enqueue((uiRoot, true));
        var seen = new HashSet<nint>();
        int total = 0, visCount = 0;
        var body = new byte[0x800];
        while (q.Count > 0 && seen.Count < 250000)
        {
            var (el, cv) = q.Dequeue();
            if (el == 0 || !seen.Add(el)) continue;
            if (Ptr(reader, el + Poe2.UiElement.Self) != el) continue;
            total++;
            var vis = reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var fl) && (fl & visBit) != 0;
            var chainVis = cv && (vis || el == uiRoot);
            if (chainVis) visCount++;
            if (reader.TryReadStruct<StdVector>(el + Poe2.UiElement.Children, out var ch))
            {
                var n = ((long)ch.Last - (long)ch.First) / 8;
                if (n is > 0 and < 5000)
                    for (long k = 0; k < n; k++) q.Enqueue((Ptr(reader, ch.First + (nint)(k * 8)), chainVis));
            }
            if (reader.TryReadBytes(el, body) < body.Length) continue;
            for (var o = 0x20; o + 8 <= body.Length; o += 8)
            {
                var c = (nint)BitConverter.ToInt64(body, o);
                if (!Plaus(c)) continue;
                if (!cache.TryGetValue(c, out var m)) { m = Meta(reader, c); cache[c] = m; }
                if (!m.StartsWith("Metadata/Items", StringComparison.Ordinal)) continue;
                hist[o] = hist.GetValueOrDefault(o) + 1;
                if (chainVis) visHist[o] = visHist.GetValueOrDefault(o) + 1;
                if (!samples.TryGetValue(o, out var l)) samples[o] = l = new();
                if (l.Count < 6 || (chainVis && l.Count < 10)) l.Add((el, c, m, chainVis));
            }
        }
        Console.WriteLine($"UI elements walked: {total} (visible-chain: {visCount}); Flags@0x{Poe2.UiElement.Flags:X} bit {Poe2.UiElement.FlagVisibleBit}");
        Console.WriteLine($"Committed slot-item offset: 0x{Poe2.Ritual.TileSlotItem:X}; candidate --slot 0x{slotOff:X}");
        Console.WriteLine("Offsets in UiElements holding a Metadata/Items entity pointer (count / on-screen count):");
        foreach (var kv in hist.OrderByDescending(k => visHist.GetValueOrDefault(k.Key)).ThenByDescending(k => k.Value).Take(15))
        {
            Console.WriteLine($"  +0x{kv.Key:X3}  x{kv.Value} / vis {visHist.GetValueOrDefault(kv.Key)}{(kv.Key == Poe2.Ritual.TileSlotItem ? "   <== committed" : "")}");
            foreach (var s in samples[kv.Key])
            {
                var ok = live.TryUiElementRect(s.el, winW, winH, out var x, out var y, out var w, out var h);
                reader.TryReadStruct<float>(s.el + Poe2.UiElement.SizeW, out var sW); reader.TryReadStruct<float>(s.el + Poe2.UiElement.SizeH, out var sH); var par = Ptr(reader, s.el + Poe2.UiElement.Parent);                Console.Write($"      size {sW:F0}x{sH:F0} parent 0x{par:X} vt 0x{Ptr(reader, s.el) - process.MainModuleBase:X} ");                Console.WriteLine($"      el 0x{s.el:X} item 0x{s.item:X} vis={s.chainVis} rect={(ok ? $"{x:F0},{y:F0} {w:F0}x{h:F0}" : "-")} {s.meta}");
            }
        }

        if (watchSecs <= 0) return 0;
        Console.WriteLine($"\nWATCH {watchSecs}s: hover items (inventory / stash / ritual) with PoE2 focused...");
        var sw = System.Diagnostics.Stopwatch.StartNew(); var last = "";
        while (sw.Elapsed.TotalSeconds < watchSecs)
        {
            System.Threading.Thread.Sleep(200);
            if (!Win.GetCursorPos(out var cp) || hwnd == 0 || !Win.ScreenToClient(hwnd, ref cp)) continue;
            var hov = live.ReadHoveredItem(igs, winW, winH, cp.X, cp.Y);

            nint bEl = 0, bIt = 0; var bA = float.MaxValue;
            var wq = new Queue<nint>(); wq.Enqueue(uiRoot); var ws = new HashSet<nint>();
            while (wq.Count > 0 && ws.Count < 40000)
            {
                var e = wq.Dequeue();
                if (e == 0 || !ws.Add(e)) continue;
                var v = reader.TryReadStruct<uint>(e + Poe2.UiElement.Flags, out var f2) && (f2 & visBit) != 0;
                if (!v && e != uiRoot) continue;
                if (reader.TryReadStruct<StdVector>(e + Poe2.UiElement.Children, out var c2))
                {
                    var n2 = ((long)c2.Last - (long)c2.First) / 8;
                    if (n2 is > 0 and < 5000) for (long k = 0; k < n2; k++) wq.Enqueue(Ptr(reader, c2.First + (nint)(k * 8)));
                }
                if (e == uiRoot) continue;
                var it = Ptr(reader, e + slotOff);
                if (!Plaus(it) || !Meta(reader, it).StartsWith("Metadata/Items", StringComparison.Ordinal)) continue;
                if (live.TryUiElementRect(e, winW, winH, out var rx, out var ry, out var rw, out var rh)
                    && cp.X >= rx && cp.X <= rx + rw && cp.Y >= ry && cp.Y <= ry + rh && rw * rh < bA)
                { bA = rw * rh; bEl = e; bIt = it; }
            }
            var cand = bIt == 0 ? "candidate: (none)" : $"candidate: el 0x{bEl:X} item 0x{bIt:X} {Meta(reader, bIt)}";
            var committed = hov is { } h2
                ? $"committed: rarity={h2.Rarity} art={h2.Art} name=\"{h2.Name}\" stack={h2.Stack} box={h2.BoxX:F0},{h2.BoxY:F0} {h2.BoxW:F0}x{h2.BoxH:F0}"
                : "committed: (none)";
            var line = $"cursor {cp.X},{cp.Y} | {cand} | {committed}";
            if (line != last) { Console.WriteLine(line); last = line; }
        }
        return 0;
    }
}
