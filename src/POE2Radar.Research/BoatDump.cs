using System.Text;
using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// <c>--boat-dump [--out file] [--btnvt 30F8918] [--nodevt 30F5718]</c> — READ-ONLY. Dumps every atlas
/// "region action button" (canvas children with vtable module+btnvt; the Expedition boat is the "Ocean" one)
/// with raw bytes, every qword that points at an atlas node element (direct or one indirection), int pairs
/// that look like grid coords, and the nearest nodes by RelativePos. Only ReadProcessMemory is used.
/// </summary>
internal static class BoatDump
{
    private static nint Ptr(MemoryReader r, nint a)
        => r.TryReadStruct<nint>(a, out var p) && p >= 0x10000 && p < 0x7FFFFFFFFFFF ? p : 0;

    private static List<nint> Children(MemoryReader r, nint el, int max)
    {
        var res = new List<nint>();
        var b = Ptr(r, el + Poe2.UiElement.Children); var e = Ptr(r, el + Poe2.UiElement.ChildrenEnd);
        if (b == 0 || e <= b) return res;
        var n = (int)((e - b) / 8); if (n > max) return res;
        for (var i = 0; i < n; i++) { var c = Ptr(r, b + i * 8); if (c != 0) res.Add(c); }
        return res;
    }

    private static string Str(MemoryReader r, nint p)
    {
        if (p == 0) return "";
        var s = r.ReadStringUtf16(p, 64);
        return s.Length >= 2 && s.All(c => c >= ' ' && c != '�' && c < 0x2000) ? s : "";
    }

    public static int Run(ProcessHandle process, MemoryReader reader, string outPath, nint btnVtOff, nint nodeVtOff)
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
        var mod = process.MainModuleBase;
        var nodeVt = mod + nodeVtOff; var btnVt = mod + btnVtOff;

        var q = new Queue<nint>(); q.Enqueue(root); var seen = new HashSet<nint>();
        nint canvas = 0;
        while (q.Count > 0 && seen.Count < 300000 && canvas == 0)
        {
            var el = q.Dequeue();
            if (el == 0 || !seen.Add(el)) continue;
            if (Ptr(reader, el + Poe2.UiElement.Self) != el) continue;
            if (Ptr(reader, el) == nodeVt) { canvas = Ptr(reader, el + Poe2.UiElement.Parent); break; }
            foreach (var c in Children(reader, el, 16384)) q.Enqueue(c);
        }
        if (canvas == 0) { Console.WriteLine("No atlas node element found (open the atlas)."); return 1; }
        var kids = Children(reader, canvas, 30000);
        var nodes = new Dictionary<nint, (int x, int y, string name, float px, float py, byte st, byte ss, byte oc)>();
        var buttons = new List<(int idx, nint el)>();
        for (var i = 0; i < kids.Count; i++)
        {
            var el = kids[i]; var vt = Ptr(reader, el);
            if (vt == nodeVt)
            {
                reader.TryReadStruct<int>(el + 0x320, out var x); reader.TryReadStruct<int>(el + 0x324, out var y);
                reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos, out var px); reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos + 4, out var py);
                var b = new byte[4]; reader.TryReadBytes(el + 0x32C, b);
                var row = Ptr(reader, el + 0x300);
                nodes[el] = (x, y, row == 0 ? "" : Str(reader, Ptr(reader, row + 8)), px, py, b[0], b[3], b[2]);
            }
            else if (vt == btnVt) buttons.Add((i, el));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"BOAT DUMP {DateTime.Now:yyyy-MM-dd HH:mm:ss} canvas 0x{canvas:X} children {kids.Count} nodes {nodes.Count} buttons {buttons.Count} btnVt mod+0x{btnVtOff:X}");
        var nodeKeys = nodes.Keys.ToHashSet();
        foreach (var (idx, el) in buttons)
        {
            reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var fl);
            var vis = ((fl >> Poe2.UiElement.FlagVisibleBit) & 1) != 0;
            reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos, out var px); reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos + 4, out var py);
            reader.TryReadStruct<float>(el + Poe2.UiElement.LocalScaleMul, out var sc);
            var row = Ptr(reader, el + 0x308);
            var kind = row == 0 ? "?" : Str(reader, Ptr(reader, row));
            var buf = new byte[0x500]; reader.TryReadBytes(el, buf);
            sb.AppendLine($"=== btn #{idx} el 0x{el:X} kind {kind} vis {(vis ? 1 : 0)} flags 0x{fl:X8} relPos ({px:0.#};{py:0.#}) sc {sc:0.##} parent 0x{Ptr(reader, el + Poe2.UiElement.Parent):X} row 0x{row:X}");
            // node pointers: direct / one indirection
            for (var o = 0; o + 8 <= buf.Length; o += 8)
            {
                var p = (nint)BitConverter.ToInt64(buf, o);
                if (p < 0x10000 || p >= 0x7FFFFFFFFFFF) continue;
                if (nodes.TryGetValue(p, out var nd)) { sb.AppendLine($"   +{o:X3} -> NODE ({nd.x},{nd.y}) {nd.name} st {nd.st:X2} oc {nd.oc:X2} ss {nd.ss:X2}"); continue; }
                if (p == el || o < 0x10) continue;
                var sub = new byte[0x80]; var got = reader.TryReadBytes(p, sub);
                for (var so = 0; so + 8 <= got; so += 8)
                {
                    var pp = (nint)BitConverter.ToInt64(sub, so);
                    if (nodes.TryGetValue(pp, out var nd2)) sb.AppendLine($"   +{o:X3}>+{so:X2} -> NODE ({nd2.x},{nd2.y}) {nd2.name} st {nd2.st:X2}");
                }
                // vector [begin,end) of node pointers?
                if (o + 16 <= buf.Length)
                {
                    var e = (nint)BitConverter.ToInt64(buf, o + 8);
                    if (e > p && e - p <= 0x800 && (e - p) % 8 == 0)
                    {
                        var arr = new byte[(int)(e - p)]; reader.TryReadBytes(p, arr);
                        var hits = new List<string>();
                        for (var ai = 0; ai + 8 <= arr.Length; ai += 8)
                        { var ap = (nint)BitConverter.ToInt64(arr, ai); if (nodes.TryGetValue(ap, out var n3)) hits.Add($"({n3.x},{n3.y}){n3.name}"); }
                        if (hits.Count > 0) sb.AppendLine($"   +{o:X3} VEC[{(e - p) / 8}] nodes: {string.Join(" ", hits)}");
                        else
                        {
                            // vector of int pairs (grid coords)?
                            var pairs = new List<string>();
                            for (var ai = 0; ai + 8 <= arr.Length && pairs.Count < 64; ai += 4)
                            {
                                var gx = BitConverter.ToInt32(arr, ai); var gy = BitConverter.ToInt32(arr, ai + 4);
                                if (gx is >= -200 and <= 300 && gy is >= -200 and <= 300 && (gx != 0 || gy != 0)) pairs.Add($"@{ai:X}({gx},{gy})");
                            }
                            if (pairs.Count > 0) sb.AppendLine($"   +{o:X3} VEC[{(e - p)}B] ints: {string.Join(" ", pairs)}");
                        }
                    }
                }
            }
            // in-element int pairs in grid range (0x200..0x500)
            var gp = new List<string>();
            for (var o = 0x200; o + 8 <= buf.Length; o += 4)
            {
                var gx = BitConverter.ToInt32(buf, o); var gy = BitConverter.ToInt32(buf, o + 4);
                if (gx is >= -200 and <= 300 && gy is >= -200 and <= 300 && gx != 0 && gy != 0 && Math.Abs(gx) > 1 && Math.Abs(gy) > 1) gp.Add($"+{o:X}({gx},{gy})");
            }
            if (gp.Count > 0) sb.AppendLine($"   gridlike: {string.Join(" ", gp)}");
            var near = nodes.OrderBy(n => (n.Value.px - px) * (n.Value.px - px) + (n.Value.py - py) * (n.Value.py - py)).Take(4)
                .Select(n => $"({n.Value.x},{n.Value.y}){n.Value.name}[st{n.Value.st:X2} oc{n.Value.oc:X2} d{MathF.Sqrt((n.Value.px - px) * (n.Value.px - px) + (n.Value.py - py) * (n.Value.py - py)):0}]");
            sb.AppendLine($"   nearest: {string.Join(" ", near)}");
            for (var o = 0x280; o < 0x400; o += 0x20)
            {
                sb.Append($"   e+{o:X3}: ");
                for (var i = 0; i < 0x20; i++) { sb.Append(buf[o + i].ToString("X2")); sb.Append((i & 7) == 7 ? "  " : " "); }
                sb.AppendLine();
            }
        }
        foreach (var (nel, n) in nodes)
        {
            var row2 = Ptr(reader, nel + 0x2F0); var rum = row2 == 0 ? "" : Str(reader, Ptr(reader, row2 + 0xE3));
            reader.TryReadStruct<uint>(nel + Poe2.UiElement.Flags, out var nfl);
            var nb = new byte[0x14]; reader.TryReadBytes(nel + 0x32C, nb);
            sb.AppendLine($"NODE	0x{nel:X}	{n.x}	{n.y}	{n.px:0.#}	{n.py:0.#}	{(nfl >> Poe2.UiElement.FlagVisibleBit) & 1}	{Convert.ToHexString(nb)}	{n.name}	{rum}");
        }
        for (var i = 0; i < kids.Count; i++)
        {
            var el = kids[i]; var vt = Ptr(reader, el);
            var gb = new byte[0x40]; reader.TryReadBytes(el + 0x300, gb);
            var tag = vt == nodeVt ? "N" : vt == btnVt ? "B" : $"v{(long)(vt - mod):X}";
            reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var sfl);
            var extra = vt == nodeVt ? nodes[el].name : vt == btnVt ? Str(reader, Ptr(reader, Ptr(reader, el + 0x308))) : "";
            sb.AppendLine($"SEQ	{i}	{tag}	0x{el:X}	{(sfl >> Poe2.UiElement.FlagVisibleBit) & 1}	{(vt == nodeVt ? BitConverter.ToInt32(gb, 0x20) : BitConverter.ToInt32(gb, 0x18))}	{(vt == nodeVt ? BitConverter.ToInt32(gb, 0x24) : BitConverter.ToInt32(gb, 0x1C))}	{Convert.ToHexString(gb, 0x2C, 0x14)}	{extra}");
        }
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"wrote {outPath}: {buttons.Count} buttons");
        return 0;
    }
}
