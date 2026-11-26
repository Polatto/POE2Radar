using System.Text;
using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// <c>--ui-strings-dump --out file</c> — READ-ONLY walk of the ENTIRE UI element tree from UiRoot, dumping every
/// readable string (incl. hidden/invisible elements). <c>--ui-strings-diff a b</c> compares two dumps.
/// Only ReadProcessMemory through the existing read-only handle.
/// </summary>
internal static class UiStringsDump
{
    private const int MaxDepth = 40, NodeCap = 200_000, ScanEnd = 0x400, MinLen = 2, MaxLen = 300;
    private const long AtlasNodeVtableRva = 0x30F5718;
    private static readonly string[] Keywords = { "expedition", "rumour", "rumor", "logbook", "island", "ocean", "sea", "ship", "boat", "voyage", "kingsmarch", "dannig", "olroth", "uncharted", "tide", "waters" };

    private static nint Ptr(MemoryReader r, nint a)
        => r.TryReadStruct<nint>(a, out var p) && p >= 0x10000 && p < 0x7FFFFFFFFFFF ? p : 0;

    private sealed record Hit(string Path, long VtRva, int Vis, string Route, string Text);

    // pointer -> string ("" when none); shared across elements so repeated pointers cost one read.
    private static readonly Dictionary<nint, string> StrCache = new();
    private static readonly Dictionary<nint, (string a, string b)> IndCache = new();

    private static bool Printable(string s)
    {
        if (s.Length < MinLen || s.Length > MaxLen) return false;
        var ascii = true;
        foreach (var c in s)
        {
            if (c < ' ' || c == 0x7F || (c >= 0x80 && c < 0xA0) || c == 0xFFFD || (c >= 0xD800 && c < 0xE000)) return false;
            if (c >= 0x80)
            {
                ascii = false;
                var okRange = c < 0x250 || (c >= 0x370 && c <= 0x4FF) || (c >= 0x2000 && c <= 0x206F) || (c >= 0x3000 && c < 0xFFF0);
                if (!okRange) return false;
            }
        }
        if (s.Length < 4 && !ascii) return false;
        return s.Any(char.IsLetterOrDigit);
    }

    /// <summary>Read a null-terminated UTF-16 string (max 300 chars + terminator), page-chunked.</summary>
    private static string ReadWStr(MemoryReader r, nint addr)
    {
        if (addr < 0x10000 || (addr & 1) != 0) return "";
        if (StrCache.TryGetValue(addr, out var cached)) return cached;
        var sb = new StringBuilder(); var cur = addr; var ok = false;
        var buf = new byte[0x1000];
        while (sb.Length <= MaxLen)
        {
            var toPage = 0x1000 - (int)(cur & 0xFFF);
            var n = Math.Min(toPage, (MaxLen + 1 - sb.Length) * 2);
            n &= ~1; if (n <= 0) break;
            if (r.TryReadBytes(cur, buf.AsSpan(0, n)) < n) break;
            var done = false;
            for (var i = 0; i + 1 < n; i += 2)
            {
                var c = (char)(buf[i] | buf[i + 1] << 8);
                if (c == 0) { done = true; ok = true; break; }
                sb.Append(c);
                if (sb.Length > MaxLen) break;
            }
            if (done || sb.Length > MaxLen) break;
            cur += n;
        }
        var s = ok && Printable(sb.ToString()) ? sb.ToString() : "";
        if (StrCache.Count > 2_000_000) StrCache.Clear();
        StrCache[addr] = s; return s;
    }

    /// <summary>Scan a byte window for all three string shapes.</summary>
    private static void ScanBuf(MemoryReader r, byte[] b, int len, string tag, List<(string route, string s)> outp)
    {
        for (var o = 0; o + 8 <= len; o += 8)
        {
            var q = (nint)BitConverter.ToInt64(b, o);
            var inRange = q >= 0x10000 && q < 0x7FFFFFFFFFFF;
            if (inRange)
            {
                // 1) pointer to UTF-16
                var s = ReadWStr(r, q);
                if (s.Length > 0) outp.Add(($"{tag}+{o:X3}", s));
                else
                {
                    // 3) one level of indirection: struct whose +0/+8 is a string pointer
                    if (!IndCache.TryGetValue(q, out var ind))
                    {
                        ind = (ReadWStr(r, Ptr(r, q)), ReadWStr(r, Ptr(r, q + 8)));
                        if (IndCache.Count > 2_000_000) IndCache.Clear();
                        IndCache[q] = ind;
                    }
                    if (ind.a.Length > 0) outp.Add(($"{tag}+{o:X3}>+00", ind.a));
                    if (ind.b.Length > 0) outp.Add(($"{tag}+{o:X3}>+08", ind.b));
                }
            }
            // 2) inline MSVC std::wstring: buf[8 wchar] size@+16 cap@+24
            if (o + 32 <= len)
            {
                var size = BitConverter.ToInt64(b, o + 16); var cap = BitConverter.ToInt64(b, o + 24);
                if (size >= MinLen && size <= MaxLen && cap >= size && cap <= 0x100000)
                {
                    var s2 = "";
                    if (cap < 8) s2 = Encoding.Unicode.GetString(b, o, (int)size * 2);
                    else if (inRange && (q & 1) == 0)
                    {
                        var heap = new byte[(int)size * 2 + 2];
                        if (r.TryReadBytes(q, heap) == heap.Length && heap[^1] == 0 && heap[^2] == 0)
                            s2 = Encoding.Unicode.GetString(heap, 0, (int)size * 2);
                    }
                    if (s2.Length == size && Printable(s2)) outp.Add(($"{tag}+{o:X3}:sso", s2));
                }
            }
        }
    }

    private static void ScanStruct(MemoryReader r, nint addr, int len, string tag, List<(string, string)> outp)
    {
        if (addr == 0) return;
        var b = new byte[len];
        if (r.TryReadBytes(addr, b) < len) { len = Math.Min(len, 0x100); b = new byte[len]; if (r.TryReadBytes(addr, b) < len) return; }
        ScanBuf(r, b, len, tag, outp);
    }

    public static int Run(ProcessHandle process, MemoryReader reader, string outPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        nint gsSlot = 0;
        foreach (var pat in AobPatterns.GameStateRefs)
        {
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { gsSlot = s; break; }
            if (gsSlot != 0) break;
        }
        if (gsSlot == 0) { Console.WriteLine("Chain not resolved."); return 1; }
        new Poe2Live(reader, gsSlot).TryResolve(out var igs, out _, out _);
        var uiRoot = Ptr(reader, igs + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) { Console.WriteLine("UI root null."); return 1; }

        var mod = (long)process.MainModuleBase;
        var hits = new List<Hit>();
        var seen = new HashSet<nint>();
        var q = new Queue<(nint el, string path, int depth)>();
        q.Enqueue((uiRoot, "R", 0));
        int elements = 0, nodes = 0, capped = 0, deepCut = 0;
        var atlasPanelVis = "?";
        var panelIdx = Poe2.AtlasPanel.UiRootChildIndex;
        var buf = new byte[ScanEnd];
        while (q.Count > 0)
        {
            var (el, path, depth) = q.Dequeue();
            if (el == 0 || !seen.Add(el)) continue;
            if (seen.Count > NodeCap) { capped = 1; break; }
            elements++;
            var vt = Ptr(reader, el);
            var vtRva = vt == 0 ? 0 : (long)vt - mod;
            var vis = reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var fl) ? (int)((fl >> Poe2.UiElement.FlagVisibleBit) & 1) : -1;
            if (path == $"R.{panelIdx}") atlasPanelVis = vis.ToString();

            var found = new List<(string, string)>();
            if (reader.TryReadBytes(el, buf) < ScanEnd)
            {
                // partial region: read what we can in 0x100 chunks
                Array.Clear(buf);
                var sl = new byte[0x100];
                for (var o = 0; o < ScanEnd; o += 0x100)
                    if (reader.TryReadBytes(el + o, sl) == 0x100) Array.Copy(sl, 0, buf, o, 0x100);
            }
            ScanBuf(reader, buf, ScanEnd, "e", found);

            if (vtRva == AtlasNodeVtableRva)
            {
                nodes++;
                var nd = Ptr(reader, el + 0x60);
                ScanStruct(reader, nd, 0x300, "nd", found);              // node data
                var storage = Ptr(reader, el + Poe2.AtlasNode.DataStorage);
                var model = storage == 0 ? 0 : Ptr(reader, storage + Poe2.AtlasNode.DataModel);
                if (model != 0 && model != nd) ScanStruct(reader, model, 0x300, "ndm", found);
                var row = Ptr(reader, el + Poe2.AtlasNode.Content);
                if (row != 0)
                {
                    ScanStruct(reader, row, 0x100, "crow", found);
                    var cr = Ptr(reader, row + 0x38); if (cr != 0) ScanStruct(reader, cr, 0x80, "crow38", found);
                    var st = Ptr(reader, row + 0x50); if (st != 0) ScanStruct(reader, st, 0x100, "crow50", found);
                }
                var mrow = Ptr(reader, el + Poe2.AtlasNode.MapNodeId);
                if (mrow != 0) ScanStruct(reader, mrow, 0x80, "mrow", found);
            }
            foreach (var (route, s) in found.DistinctBy(f => f.Item1 + "\0" + f.Item2))
                hits.Add(new Hit(path, vtRva, vis, route, s));

            if (depth >= MaxDepth) { deepCut++; continue; }
            var cb = Ptr(reader, el + Poe2.UiElement.Children); var ce = Ptr(reader, el + Poe2.UiElement.ChildrenEnd);
            if (cb == 0 || ce <= cb) continue;
            var cnt = (int)((ce - cb) / 8); if (cnt > 20000) continue;
            var arr = new byte[cnt * 8];
            if (reader.TryReadBytes(cb, arr) < arr.Length) continue;
            for (var i = 0; i < cnt; i++)
            {
                var c = (nint)BitConverter.ToInt64(arr, i * 8);
                if (c >= 0x10000 && c < 0x7FFFFFFFFFFF) q.Enqueue((c, $"{path}.{i}", depth + 1));
            }
        }

        static int[] Key(string p) => p.Split('.').Skip(1).Select(int.Parse).ToArray();
        var sorted = hits.OrderBy(h => Key(h.Path), Comparer<int[]>.Create((x, y) =>
        {
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++) { var c = x[i].CompareTo(y[i]); if (c != 0) return c; }
            return x.Length.CompareTo(y.Length);
        })).ThenBy(h => h.Route, StringComparer.Ordinal).ToList();

        var sb = new StringBuilder();
        foreach (var h in sorted)
            sb.AppendLine($"{(h.Path == "R" ? "R" : h.Path[2..])} | 0x{h.VtRva:X} | {h.Vis} | {h.Route} | \"{h.Text}\"");
        var distinct = sorted.Select(h => h.Text).Distinct().ToList();
        sb.AppendLine();
        sb.AppendLine("=== SUMMARY ===");
        sb.AppendLine($"time {DateTime.Now:yyyy-MM-dd HH:mm:ss}  runtime {sw.Elapsed.TotalSeconds:0.0}s");
        sb.AppendLine($"elements {elements}  atlasNodeElements {nodes}  nodeCapHit {capped}  depthCut {deepCut}");
        sb.AppendLine($"UiRoot child [{panelIdx}] visible bit = {atlasPanelVis}");
        sb.AppendLine($"string lines {sorted.Count}  distinct strings {distinct.Count}  hidden(vis=0) lines {sorted.Count(h => h.Vis == 0)}");
        sb.AppendLine("--- keyword hits (distinct strings) ---");
        var kh = distinct.Where(s => Keywords.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase))).OrderBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var s in kh) sb.AppendLine($"  \"{s}\"  [{string.Join(",", Keywords.Where(k => s.Contains(k, StringComparison.OrdinalIgnoreCase)))}]");
        sb.AppendLine($"keyword distinct hits: {kh.Count}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, sb.ToString());
        var summary = sb.ToString(); var si = summary.IndexOf("=== SUMMARY", StringComparison.Ordinal);
        Console.WriteLine(summary[si..]);
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    private static Dictionary<string, List<string>> Load(string file)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(file))
        {
            if (line.StartsWith("=== SUMMARY")) break;
            var i = line.IndexOf(" | \"", StringComparison.Ordinal);
            if (i < 0 || !line.EndsWith('"')) continue;
            var s = line[(i + 4)..^1];
            var path = line[..line.IndexOf(' ')];
            if (!d.TryGetValue(s, out var l)) d[s] = l = new();
            l.Add(path);
        }
        return d;
    }

    public static int Diff(string a, string b)
    {
        var A = Load(a); var B = Load(b);
        bool Kw(string s) => Keywords.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));
        void Side(string title, Dictionary<string, List<string>> x, Dictionary<string, List<string>> y)
        {
            var only = x.Keys.Where(k => !y.ContainsKey(k)).OrderByDescending(Kw).ThenBy(k => k, StringComparer.Ordinal).ToList();
            Console.WriteLine($"=== {title}: {only.Count} strings ({only.Count(Kw)} keyword hits) ===");
            foreach (var s in only) Console.WriteLine($"{(Kw(s) ? "** " : "   ")}\"{s}\"  @ {string.Join(",", x[s].Take(3))}{(x[s].Count > 3 ? $" (+{x[s].Count - 3})" : "")}");
        }
        Console.WriteLine($"A={a} ({A.Count} distinct)  B={b} ({B.Count} distinct)");
        Side("only in A", A, B);
        Side("only in B", B, A);
        return 0;
    }
}
