using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// Expedition "Saga" omen research — READ-ONLY (ReadProcessMemory only).
/// <c>--saga-snap --tag NAME [--out DIR] [--budget SECS]</c> writes DIR/NAME.txt (readable) + DIR/NAME.json (raw
/// bytes for diffing): player inventories (+ raw entity/component bytes for Expedition/Saga/Omen/Logbook items),
/// local-player Buffs, ServerData raw, every atlas node (if the atlas is open: grid/block/RevealId/map/rumour/tags +
/// raw 0x400 element bytes + 1-hop pointer blobs + UTF-16/UTF-8 strings reachable 1–3 hops), boat buttons, and
/// in-map Expedition / Saga-boss entities (+ runeshape monolith reads).
/// <c>--saga-diff A B [--out DIR]</c> compares two snapshots.
/// The Program.cs helper functions (top-level local functions) are injected through <see cref="Hooks"/>.
/// </summary>
internal static class SagaSnap
{
    // ── injected Program.cs helpers ─────────────────────────────────────────────────────────────
    internal static class Hooks
    {
        public static Func<MemoryReader, nint, nint> ResolveServerDataStruct = null!;
        public static Func<MemoryReader, nint, int, (int off, StdVector vec, int count)> FindPlayerInventoriesVec = null!;
        public static Func<MemoryReader, nint, (int boxX, int boxY, int itemListOff, StdVector vec, int count)> ProbeInventoryStruct = null!;
        public static Func<MemoryReader, nint, List<string>> ReadItemModIds = null!;
        public static Func<int, string> InvName = null!;
        public static Func<MemoryReader, nint, List<(string name, nint addr)>> Components = null!;
        public static Func<MemoryReader, nint, string> Metadata = null!;
        public static Func<MemoryReader, nint, IEnumerable<(uint id, nint ent, string meta)>> WalkEntities = null!;
        public static Func<MemoryReader, nint, System.Numerics.Vector2> EntityGrid = null!;
    }

    // ── snapshot model (System.Text.Json; byte[] → base64) ──────────────────────────────────────
    public sealed class Snap
    {
        public string Tag { get; set; } = "";
        public string Time { get; set; } = "";
        public string Area { get; set; } = "";
        public string AreaCode { get; set; } = "";
        public int AreaLevel { get; set; }
        public bool AtlasOpen { get; set; }
        public List<string> Notes { get; set; } = new();
        public List<ItemRec> Items { get; set; } = new();
        public List<NodeRec> Nodes { get; set; } = new();
        public List<BtnRec> Buttons { get; set; } = new();
        public List<EntRec> Entities { get; set; } = new();
        public Dictionary<string, byte[]> Raw { get; set; } = new();     // named raw regions (ServerData, canvas, buffs…)
        public Dictionary<string, List<string>> RawStrings { get; set; } = new();
        public Dictionary<string, byte[]> Blobs { get; set; } = new();   // hex addr → 0x100 bytes (node pointer targets, deduped)
    }
    public sealed class CompRec { public string Name { get; set; } = ""; public string Addr { get; set; } = ""; public byte[]? Bytes { get; set; } public List<string>? Strings { get; set; } }
    public sealed class ItemRec
    {
        public int Inv { get; set; }
        public string InvName { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public string Addr { get; set; } = "";
        public string Meta { get; set; } = "";
        public string Name { get; set; } = "";
        public int Stack { get; set; }
        public int Rarity { get; set; }
        public List<string> Mods { get; set; } = new();
        public List<string> CompNames { get; set; } = new();
        public byte[]? EntityBytes { get; set; }
        public List<CompRec>? Comps { get; set; }
        public string Key => $"{Inv}:{X},{Y}:{Meta}";
    }
    public sealed class NodeRec
    {
        public string El { get; set; } = "";
        public int GX { get; set; }
        public int GY { get; set; }
        public int BX { get; set; }
        public int BY { get; set; }
        public int RevealId { get; set; }
        public bool Revealed { get; set; }
        public bool Visible { get; set; }
        public int Status { get; set; }
        public uint Content { get; set; }
        public string Map { get; set; } = "";
        public string Code { get; set; } = "";
        public string Rumour { get; set; } = "";
        public string Kind { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public byte[]? Bytes { get; set; }
        public Dictionary<string, string> Ptrs { get; set; } = new();   // "+off" → target hex (key into Blobs) or "node(x,y)" / "ui"
        public List<string> Strings { get; set; } = new();              // "+off[>+sub[>+sub]]: text"
    }
    public sealed class BtnRec { public string El { get; set; } = ""; public int GX { get; set; } public int GY { get; set; } public string Kind { get; set; } = ""; public bool Visible { get; set; } public byte[]? Bytes { get; set; } public List<string> Strings { get; set; } = new(); }
    public sealed class EntRec
    {
        public uint Id { get; set; }
        public string Addr { get; set; } = "";
        public string Meta { get; set; } = "";
        public float GX { get; set; }
        public float GY { get; set; }
        public List<string> CompNames { get; set; } = new();
        public List<string> Info { get; set; } = new();
        public List<CompRec>? Comps { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly Regex BossRx = new("Medved|Vorana|Uhtred|Olroth|Aldur", RegexOptions.IgnoreCase);
    private static bool InterestingItem(string meta, string name) =>
        meta.Contains("Expedition", StringComparison.OrdinalIgnoreCase) || name.Contains("Saga", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Omen", StringComparison.OrdinalIgnoreCase) || name.Contains("Logbook", StringComparison.OrdinalIgnoreCase) ||
        meta.Contains("Omen", StringComparison.OrdinalIgnoreCase);

    // ── reading helpers ─────────────────────────────────────────────────────────────────────────
    private sealed class Ctx
    {
        public required MemoryReader R;
        public required nint ModLo, ModHi;
        public readonly Dictionary<nint, string?> StrCache = new();
        public readonly Dictionary<nint, byte[]?> BlobCache = new();
        public Stopwatch Sw = Stopwatch.StartNew();
        public double BudgetSec = 90;
        public bool OverBudget => Sw.Elapsed.TotalSeconds > BudgetSec;
    }

    private static nint Ptr(MemoryReader r, nint a)
        => r.TryReadStruct<nint>(a, out var p) && p >= 0x10000 && p < 0x7FFFFFFFFFFF ? p : 0;
    private static bool IsPtr(long p) => p >= 0x10000 && p < 0x7FFFFFFFFFFF;
    private static string H(nint a) => $"0x{(long)a:X}";

    private static byte[]? Read(MemoryReader r, nint a, int len)
    {
        if (a == 0) return null;
        try { var b = new byte[len]; var n = r.TryReadBytes(a, b); if (n <= 0) return null; if (n < len) Array.Resize(ref b, n); return b; }
        catch { return null; }
    }

    private static byte[]? Blob(Ctx c, nint a, int len)
    {
        if (c.BlobCache.TryGetValue(a, out var b) && (b == null || b.Length >= len)) return b;
        b = Read(c.R, a, len); c.BlobCache[a] = b; return b;
    }

    /// <summary>A plausible game string at <paramref name="a"/> (UTF-16 first, then ASCII/UTF-8), or null.</summary>
    private static string? Str(Ctx c, nint a)
    {
        if (a == 0) return null;
        if (c.StrCache.TryGetValue(a, out var s)) return s;
        s = null;
        try
        {
            var u = c.R.ReadStringUtf16(a, 96);
            if (GoodStr(u)) s = u;
            else
            {
                var u8 = c.R.ReadStringUtf8(a, 96);
                if (u8.Length >= 4 && GoodStr(u8)) s = "u8:" + u8;
            }
        }
        catch { s = null; }
        c.StrCache[a] = s; return s;
    }

    private static bool GoodStr(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 3) return false;
        if (!(char.IsLetterOrDigit(s[0]) || s[0] is '<' or '{' or '[' or '+' or '-' or '%')) return false;
        var ok = 0;
        foreach (var ch in s) { if (ch < ' ' || ch == '�') return false; if (ch < 0x7F) ok++; }
        return ok * 10 >= s.Length * 9;
    }

    /// <summary>Strings reachable from the qwords of <paramref name="buf"/>[lo,hi): string at ptr; otherwise (depth&gt;1)
    /// read the target and recurse. UI elements (Self==addr) and main-module addresses are not followed.</summary>
    private static void Strings(Ctx c, byte[] buf, int lo, int hi, int depth, string prefix, List<string> outp, int maxOut = 200)
    {
        for (var o = lo & ~7; o + 8 <= Math.Min(hi, buf.Length) && outp.Count < maxOut; o += 8)
        {
            var p = (nint)BitConverter.ToInt64(buf, o);
            if (!IsPtr(p) || (p >= c.ModLo && p < c.ModHi)) continue;
            var s = Str(c, p);
            if (s != null) { outp.Add($"{prefix}+{o:X}: {s}"); continue; }
            if (depth <= 1 || c.OverBudget) continue;
            if (Ptr(c.R, p + 8) == p) continue; // a UiElement (Self pointer) — not data
            var sub = Blob(c, p, depth >= 3 ? 0x100 : 0x60);
            if (sub == null) continue;
            Strings(c, sub, 0, sub.Length, depth - 1, $"{prefix}+{o:X}>", outp, maxOut);
        }
    }

    private static List<CompRec> DumpComponents(Ctx c, nint entity, int len, bool withStrings)
    {
        var res = new List<CompRec>();
        List<(string name, nint addr)> comps;
        try { comps = Hooks.Components(c.R, entity); } catch { return res; }
        foreach (var (name, addr) in comps)
        {
            var b = Read(c.R, addr, len);
            var rec = new CompRec { Name = name, Addr = H(addr), Bytes = b };
            if (withStrings && b != null) { var l = new List<string>(); Strings(c, b, 0, b.Length, 2, "", l, 60); rec.Strings = l; }
            res.Add(rec);
        }
        return res;
    }

    private static string ItemName(MemoryReader r, List<(string name, nint addr)> comps)
    {
        var b = comps.FirstOrDefault(x => x.name == "Base").addr;
        if (b == 0) return "";
        var row = Ptr(r, b + Poe2.BaseComponent.NameRow);
        var np = row == 0 ? 0 : Ptr(r, row + Poe2.BaseComponent.RowDisplayName);
        return np == 0 ? "" : r.ReadStringUtf16(np, 64).Trim();
    }

    // ── --saga-snap ─────────────────────────────────────────────────────────────────────────────
    public static int Snapshot(ProcessHandle process, MemoryReader reader, string tag, string dir, double budgetSec)
    {
        Directory.CreateDirectory(dir);
        var c = new Ctx { R = reader, ModLo = process.MainModuleBase, ModHi = process.MainModuleBase + (nint)process.MainModuleSize, BudgetSec = budgetSec };
        var snap = new Snap { Tag = tag, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };

        nint slot = 0;
        try
        {
            foreach (var pat in AobPatterns.GameStateRefs)
            {
                foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                    if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { slot = s; break; }
                if (slot != 0) break;
            }
        }
        catch (Exception ex) { snap.Notes.Add("AOB scan failed: " + ex.Message); }
        if (slot == 0) { Console.Error.WriteLine("Chain not resolved (in game?)."); return 1; }
        var live = new Poe2Live(reader, slot);
        if (!live.TryResolve(out var igs, out var ai, out var lp)) { Console.Error.WriteLine("Chain not resolved."); return 1; }
        try { snap.Area = live.AreaName(ai); snap.AreaCode = live.AreaCode(ai); snap.AreaLevel = live.AreaLevel(ai); } catch { }
        Console.WriteLine($"area {snap.Area} ({snap.AreaCode}) lvl {snap.AreaLevel}");

        Section("inventory", () => SnapInventory(c, ai, snap), snap);
        Section("player", () => SnapPlayer(c, lp, snap), snap);
        Section("atlas", () => SnapAtlas(c, reader, igs, snap), snap);
        Section("entities", () => SnapEntities(c, live, ai, snap), snap);

        var jsonPath = Path.Combine(dir, tag + ".json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(snap, Json));
        var txtPath = Path.Combine(dir, tag + ".txt");
        File.WriteAllText(txtPath, Render(snap));
        Console.WriteLine($"items {snap.Items.Count} ({snap.Items.Count(i => i.Comps != null)} raw)  atlasOpen {snap.AtlasOpen} nodes {snap.Nodes.Count} (revealed {snap.Nodes.Count(n => n.Revealed)}) buttons {snap.Buttons.Count}  entities {snap.Entities.Count}  blobs {snap.Blobs.Count}  {c.Sw.Elapsed.TotalSeconds:0.0}s{(c.OverBudget ? " (deep-decode budget hit)" : "")}");
        foreach (var n in snap.Notes) Console.WriteLine("  note: " + n);
        Console.WriteLine($"wrote {txtPath}\n      {jsonPath}");
        return 0;
    }

    private static void Section(string name, Action a, Snap snap)
    {
        try { a(); }
        catch (Exception ex) { snap.Notes.Add($"{name}: n/a ({ex.GetType().Name}: {ex.Message})"); }
    }

    private static void SnapInventory(Ctx c, nint ai, Snap snap)
    {
        var r = c.R;
        var serverData = Ptr(r, ai + Poe2.AreaInstance.ServerDataPtr);
        if (serverData == 0) { snap.Notes.Add("inventory: n/a (ServerData null)"); return; }
        var sdRaw = Read(r, serverData, 0x2400); if (sdRaw != null) snap.Raw["ServerData"] = sdRaw;
        var sds = Hooks.ResolveServerDataStruct(r, serverData);
        if (sds == 0) { snap.Notes.Add("inventory: n/a (ServerDataStructure)"); return; }
        var sdsRaw = Read(r, sds, 0x1000); if (sdsRaw != null) snap.Raw["ServerDataStructure"] = sdsRaw;
        var (off, vec, count) = Hooks.FindPlayerInventoriesVec(r, sds, 0x320);
        if (off < 0) { snap.Notes.Add("inventory: n/a (PlayerInventories vec)"); return; }
        for (var i = 0; i < count; i++)
        {
            var rec = vec.First + (nint)(i * 0x18);
            if (!r.TryReadStruct<int>(rec, out var invId)) continue;
            var inv = Ptr(r, rec + 8);
            if (inv == 0) continue;
            var (_, _, listOff, items, n) = Hooks.ProbeInventoryStruct(r, inv);
            if (listOff < 0 || n <= 0) continue;
            var seen = new HashSet<nint>();
            for (var k = 0; k < n; k++)
            {
                var ii = Ptr(r, items.First + (nint)(k * 8));
                if (ii == 0) continue;
                var item = Ptr(r, ii);
                if (item == 0 || !seen.Add(item)) continue;
                var meta = Hooks.Metadata(r, item);
                if (!meta.StartsWith("Metadata/Items", StringComparison.Ordinal)) continue;
                r.TryReadStruct<int>(ii + 8, out var sx); r.TryReadStruct<int>(ii + 0xC, out var sy);
                var comps = Hooks.Components(r, item);
                var it = new ItemRec { Inv = invId, InvName = Hooks.InvName(invId), X = sx, Y = sy, Addr = H(item), Meta = meta, Rarity = -1, Stack = -1 };
                it.CompNames = comps.Select(x => x.name).ToList();
                it.Name = ItemName(r, comps);
                var stack = comps.FirstOrDefault(x => x.name == "Stack").addr;
                if (stack != 0 && r.TryReadStruct<int>(stack + 0x18, out var st)) it.Stack = st;
                var mods = comps.FirstOrDefault(x => x.name == "Mods").addr;
                if (mods != 0)
                {
                    if (r.TryReadStruct<int>(mods + Poe2.ModsComponent.Rarity, out var rar)) it.Rarity = rar;
                    foreach (var (kind, mo) in new[] { ("implicit", 0xA0), ("explicit", 0xB8), ("enchant", 0xD0) })
                        try { foreach (var m in Hooks.ReadItemModIds(r, mods + mo)) it.Mods.Add($"{kind}: {m}"); } catch { }
                }
                if (InterestingItem(meta, it.Name))
                {
                    it.EntityBytes = Read(r, item, 0x100);
                    it.Comps = DumpComponents(c, item, 0x300, true);
                }
                snap.Items.Add(it);
            }
        }
    }

    private static void SnapPlayer(Ctx c, nint lp, Snap snap)
    {
        if (lp == 0) { snap.Notes.Add("player: n/a"); return; }
        foreach (var comp in DumpComponents(c, lp, 0x400, false))
        {
            if (comp.Name is not ("Buffs" or "Player" or "Stats") || comp.Bytes == null) continue;
            snap.Raw["Player." + comp.Name] = comp.Bytes;
            var l = new List<string>();
            Strings(c, comp.Bytes, 0, comp.Bytes.Length, 3, "", l, 150);
            snap.RawStrings["Player." + comp.Name] = l;
        }
        // Buff names: Buffs component holds a StdVector<Buff*>; Buff+0x08 → BuffDefinitions row → +0x00 UTF-16 id.
        // Not offset-validated here: we scan the component for vectors and decode strings 2 hops from each element.
        var buffs = Hooks.Components(c.R, lp).FirstOrDefault(x => x.name == "Buffs").addr;
        if (buffs == 0) return;
        var bb = Read(c.R, buffs, 0x200);
        if (bb == null) return;
        var names = new List<string>();
        for (var o = 0x08; o + 16 <= bb.Length; o += 8)
        {
            var b = BitConverter.ToInt64(bb, o); var e = BitConverter.ToInt64(bb, o + 8);
            if (!IsPtr(b) || e <= b || e - b > 0x2000 || (e - b) % 8 != 0) continue;
            var arr = Read(c.R, (nint)b, (int)(e - b));
            if (arr == null) continue;
            for (var i = 0; i + 8 <= arr.Length; i += 8)
            {
                var el = (nint)BitConverter.ToInt64(arr, i);
                var eb = IsPtr(el) ? Blob(c, el, 0x40) : null;
                if (eb == null) continue;
                var l = new List<string>(); Strings(c, eb, 0, eb.Length, 2, $"vec+{o:X}[{i / 8}]", l, 6);
                names.AddRange(l);
            }
        }
        snap.RawStrings["Player.BuffVecStrings"] = names;
    }

    private static void SnapAtlas(Ctx c, MemoryReader reader, nint igs, Snap snap)
    {
        var atlas = new Poe2Atlas(reader);
        snap.AtlasOpen = atlas.IsAtlasOpen(igs);
        var btns = new List<Poe2Atlas.AtlasBoatButton>();
        var nodes = atlas.ReadNodes(igs, btns);
        for (var w = 0; (nodes.Count == 0 || !atlas.AllTagsResolved) && w < 60; w++) { Thread.Sleep(50); nodes = atlas.ReadNodes(igs, btns); }
        if (nodes.Count == 0) { snap.Notes.Add("atlas: n/a (atlas not open / no nodes)"); return; }
        var byEl = nodes.ToDictionary(n => n.Element, n => n);
        nint canvas = Ptr(reader, nodes[0].Element + Poe2.UiElement.Parent);
        var cv = Read(reader, canvas, 0x800); if (cv != null) snap.Raw["AtlasCanvas"] = cv;

        foreach (var n in nodes)
        {
            var nr = new NodeRec
            {
                El = H(n.Element), GX = n.GridX, GY = n.GridY, BX = n.Block.X, BY = n.Block.Y, RevealId = n.RevealId, Revealed = n.Revealed,
                Visible = n.Visible, Status = n.Status, Content = n.Content, Map = n.MapName, Code = n.MapCode, Rumour = n.Rumour, Kind = n.Kind,
                Tags = n.Tags.ToList(),
            };
            var b = Read(reader, n.Element, 0x400);
            nr.Bytes = b;
            if (b != null)
            {
                for (var o = 0x180; o + 8 <= b.Length; o += 8)
                {
                    var p = (nint)BitConverter.ToInt64(b, o);
                    if (!IsPtr(p) || p == n.Element) continue;
                    if (byEl.TryGetValue(p, out var other)) { nr.Ptrs[$"+{o:X}"] = $"node({other.GridX},{other.GridY})"; continue; }
                    if (p >= c.ModLo && p < c.ModHi) continue;
                    if (Ptr(reader, p + 8) == p) { nr.Ptrs[$"+{o:X}"] = "ui"; continue; }
                    var blob = Blob(c, p, 0x100);
                    if (blob == null) continue;
                    var key = H(p);
                    nr.Ptrs[$"+{o:X}"] = key;
                    snap.Blobs.TryAdd(key, blob);
                }
                Strings(c, b, 0x180, b.Length, 3, "", nr.Strings, 120);
            }
            snap.Nodes.Add(nr);
        }
        foreach (var bt in btns)
        {
            var br = new BtnRec { El = H(bt.Element), GX = bt.GridX, GY = bt.GridY, Kind = bt.KindName, Visible = bt.Visible, Bytes = Read(reader, bt.Element, 0x500) };
            if (br.Bytes != null) Strings(c, br.Bytes, 0x180, br.Bytes.Length, 2, "", br.Strings, 60);
            snap.Buttons.Add(br);
        }
    }

    private static void SnapEntities(Ctx c, Poe2Live live, nint ai, Snap snap)
    {
        var r = c.R;
        var catalog = RuneMonolithCatalog.Instance;
        var lvl = snap.AreaLevel;
        foreach (var (id, ent, meta) in Hooks.WalkEntities(r, ai))
        {
            var expo = meta.Contains("Expedition", StringComparison.OrdinalIgnoreCase) || meta.Contains("Verisium", StringComparison.OrdinalIgnoreCase)
                       || meta.Contains("Remnant", StringComparison.OrdinalIgnoreCase);
            var boss = BossRx.IsMatch(meta);
            if (!expo && !boss) continue;
            var g = Hooks.EntityGrid(r, ent);
            var er = new EntRec { Id = id, Addr = H(ent), Meta = meta, GX = g.X, GY = g.Y };
            var comps = Hooks.Components(r, ent);
            er.CompNames = comps.Select(x => x.name).ToList();
            // Render name (if any): Render +? is not validated; use the Base/ObjectMagicProperties-free approach: look for strings in Render.
            if (meta.Contains("Expedition2Encounter", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var m = live.ReadMonolith(ent);
                    er.Info.Add($"monolith resolved={m.Resolved} holes={m.HoleCount} anchor={(m.AnchorIdx >= 0 ? catalog.RuneName(m.AnchorIdx) : "-")}@{m.AnchorPos + 1} unique={m.IsUnique} collected={m.Collected}");
                    if (m.Resolved)
                        foreach (var o in catalog.Offers(m.AnchorIdx, m.AnchorPos, m.HoleCount, m.IsUnique, lvl).Take(20))
                            er.Info.Add($"  offer size{o.Size} x{o.Count} {(string.IsNullOrEmpty(o.Name) ? o.Description : o.Name)} [{o.Runes}]");
                }
                catch (Exception ex) { er.Info.Add("monolith read failed: " + ex.Message); }
            }
            if (boss || er.Info.Count > 0 || snap.Entities.Count < 150)
                er.Comps = DumpComponents(c, ent, 0x200, boss);
            snap.Entities.Add(er);
        }
        if (snap.Entities.Count == 0) snap.Notes.Add("entities: none matching Expedition/Saga-boss in this area");
        snap.Notes.Add("area map mods: n/a (no validated reader yet)");
    }

    // ── text rendering ──────────────────────────────────────────────────────────────────────────
    private static string Render(Snap s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SAGA SNAP '{s.Tag}' {s.Time}  area {s.Area} ({s.AreaCode}) lvl {s.AreaLevel}  atlasOpen {s.AtlasOpen}");
        foreach (var n in s.Notes) sb.AppendLine("note: " + n);
        sb.AppendLine($"\n== ITEMS ({s.Items.Count}) ==");
        foreach (var it in s.Items.OrderBy(i => i.Inv).ThenBy(i => i.Y).ThenBy(i => i.X))
        {
            sb.AppendLine($"[{it.Inv} {it.InvName}] ({it.X},{it.Y}) {it.Name} x{it.Stack} rar{it.Rarity} {it.Addr}  {it.Meta}");
            foreach (var m in it.Mods) sb.AppendLine("     " + m);
            if (it.Comps == null) continue;
            sb.AppendLine($"     RAW components: {string.Join(", ", it.Comps.Select(x => $"{x.Name}@{x.Addr}"))}");
            foreach (var cp in it.Comps.Where(x => x.Strings is { Count: > 0 }))
                sb.AppendLine($"       {cp.Name}: {string.Join(" | ", cp.Strings!.Take(12))}");
        }
        foreach (var (k, v) in s.RawStrings)
            sb.AppendLine($"\n== {k} strings ({v.Count}) ==\n   {string.Join("\n   ", v.Take(80))}");
        sb.AppendLine($"\n== ATLAS NODES ({s.Nodes.Count}, revealed {s.Nodes.Count(n => n.Revealed)}) ==");
        foreach (var n in s.Nodes.OrderBy(n => n.BY).ThenBy(n => n.BX).ThenBy(n => n.GY).ThenBy(n => n.GX))
        {
            sb.AppendLine($"({n.GX},{n.GY}) blk({n.BX},{n.BY}) rev {(n.Revealed ? n.RevealId.ToString() : "-")} vis {(n.Visible ? 1 : 0)} st {n.Status:X2} {n.Map} [{n.Code}] {n.Kind} rumour '{n.Rumour}' tags [{string.Join(",", n.Tags)}] {n.El}");
            var extra = n.Strings.Where(x => !x.EndsWith(n.Map, StringComparison.Ordinal)).Take(8).ToList();
            if (extra.Count > 0) sb.AppendLine("     " + string.Join(" | ", extra));
        }
        sb.AppendLine($"\n== BUTTONS ({s.Buttons.Count}) ==");
        foreach (var b in s.Buttons) sb.AppendLine($"{b.Kind} ({b.GX},{b.GY}) blk({b.GX >> 4},{b.GY >> 4}) vis {(b.Visible ? 1 : 0)} {b.El}  {string.Join(" | ", b.Strings.Take(6))}");
        sb.AppendLine($"\n== ENTITIES ({s.Entities.Count}) ==");
        foreach (var e in s.Entities)
        {
            sb.AppendLine($"#{e.Id} ({e.GX:0},{e.GY:0}) {e.Meta} {e.Addr}  comps: {string.Join(",", e.CompNames)}");
            foreach (var i in e.Info) sb.AppendLine("     " + i);
            foreach (var cp in e.Comps?.Where(x => x.Strings is { Count: > 0 }) ?? Enumerable.Empty<CompRec>())
                sb.AppendLine($"       {cp.Name}: {string.Join(" | ", cp.Strings!.Take(10))}");
        }
        return sb.ToString();
    }

    // ── --saga-diff ─────────────────────────────────────────────────────────────────────────────
    private static readonly (int off, int len)[] NodeIgnore = { (0x100, 8), (0x168, 4) };

    public static int Diff(string a, string b, string dir)
    {
        Snap? Load(string t)
        {
            var p = File.Exists(t) ? t : Path.Combine(dir, t.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? t : t + ".json");
            if (!File.Exists(p)) { Console.Error.WriteLine("missing " + p); return null; }
            return JsonSerializer.Deserialize<Snap>(File.ReadAllText(p), Json);
        }
        var A = Load(a); var B = Load(b);
        if (A == null || B == null) return 1;
        var sb = new StringBuilder();
        sb.AppendLine($"SAGA DIFF '{A.Tag}' ({A.Time}, {A.Area}) -> '{B.Tag}' ({B.Time}, {B.Area})");

        // items
        sb.AppendLine("\n== ITEMS ==");
        var ia = A.Items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        var ib = B.Items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var k in ia.Keys.Except(ib.Keys)) sb.AppendLine($"- removed {Desc(ia[k])}");
        foreach (var k in ib.Keys.Except(ia.Keys)) sb.AppendLine($"+ added   {Desc(ib[k])}");
        foreach (var k in ia.Keys.Intersect(ib.Keys))
        {
            var x = ia[k]; var y = ib[k];
            var lines = new List<string>();
            if (x.Stack != y.Stack) lines.Add($"stack {x.Stack} -> {y.Stack}");
            if (x.Addr != y.Addr) lines.Add($"entity addr {x.Addr} -> {y.Addr}");
            if (!x.Mods.SequenceEqual(y.Mods)) lines.Add($"mods [{string.Join("; ", x.Mods)}] -> [{string.Join("; ", y.Mods)}]");
            if (!x.CompNames.SequenceEqual(y.CompNames)) lines.Add($"components [{string.Join(",", x.CompNames.Except(y.CompNames))}] -> [{string.Join(",", y.CompNames.Except(x.CompNames))}]");
            if (x.EntityBytes != null && y.EntityBytes != null) AddRanges(lines, "entity", x.EntityBytes, y.EntityBytes, null);
            if (x.Comps != null && y.Comps != null)
                foreach (var cy in y.Comps)
                {
                    var cx = x.Comps.FirstOrDefault(z => z.Name == cy.Name);
                    if (cx?.Bytes == null || cy.Bytes == null) continue;
                    AddRanges(lines, cy.Name, cx.Bytes, cy.Bytes, null);
                    var newS = (cy.Strings ?? new()).Except(cx.Strings ?? new()).Take(6).ToList();
                    if (newS.Count > 0) lines.Add($"{cy.Name} new strings: {string.Join(" | ", newS)}");
                }
            if (lines.Count == 0) continue;
            sb.AppendLine($"~ {Desc(y)}");
            foreach (var l in lines) sb.AppendLine("     " + l);
        }

        // raw regions
        sb.AppendLine("\n== RAW REGIONS ==");
        foreach (var (k, vb) in B.Raw)
        {
            if (!A.Raw.TryGetValue(k, out var va)) { sb.AppendLine($"{k}: only in B"); continue; }
            var lines = new List<string>();
            AddRanges(lines, k, va, vb, null, 24);
            sb.AppendLine(lines.Count == 0 ? $"{k}: unchanged" : string.Join("\n", lines.Select(l => "  " + l)));
        }
        foreach (var (k, vb) in B.RawStrings)
        {
            var va = A.RawStrings.GetValueOrDefault(k) ?? new();
            var add = vb.Except(va).ToList(); var rem = va.Except(vb).ToList();
            if (add.Count + rem.Count == 0) continue;
            sb.AppendLine($"{k} strings: +{add.Count} -{rem.Count}");
            foreach (var s in add.Take(20)) sb.AppendLine("   + " + s);
            foreach (var s in rem.Take(10)) sb.AppendLine("   - " + s);
        }

        // atlas
        sb.AppendLine($"\n== ATLAS == nodes {A.Nodes.Count} -> {B.Nodes.Count}, revealed {A.Nodes.Count(n => n.Revealed)} -> {B.Nodes.Count(n => n.Revealed)}");
        var na = A.Nodes.GroupBy(n => (n.GX, n.GY)).ToDictionary(g => g.Key, g => g.First());
        var nbd = B.Nodes.GroupBy(n => (n.GX, n.GY)).ToDictionary(g => g.Key, g => g.First());
        var offHist = new Dictionary<int, int>();
        var detail = new StringBuilder();
        var changedCount = 0;
        foreach (var (k, y) in nbd)
        {
            if (!na.TryGetValue(k, out var x)) { detail.AppendLine($"+ node ({y.GX},{y.GY}) {y.Map} rev {y.RevealId}"); continue; }
            var ranges = x.Bytes != null && y.Bytes != null ? Ranges(x.Bytes, y.Bytes, NodeIgnore) : new();
            foreach (var (o, _) in ranges) offHist[o & ~7] = offHist.GetValueOrDefault(o & ~7) + 1;
            var revCh = x.RevealId != y.RevealId;
            var metaCh = x.Map != y.Map || x.Rumour != y.Rumour || !x.Tags.SequenceEqual(y.Tags) || x.Status != y.Status || x.Content != y.Content;
            if (!revCh && !metaCh) continue;
            changedCount++;
            detail.AppendLine($"* ({y.GX},{y.GY}) blk({y.BX},{y.BY}) rev {x.RevealId}->{y.RevealId} vis {(x.Visible ? 1 : 0)}->{(y.Visible ? 1 : 0)} st {x.Status:X2}->{y.Status:X2} map '{x.Map}'->'{y.Map}' rumour '{y.Rumour}' tags [{string.Join(",", y.Tags)}]");
            if (x.Bytes != null && y.Bytes != null) foreach (var l in FormatRanges(x.Bytes, y.Bytes, ranges, 16)) detail.AppendLine("     " + l);
            foreach (var (po, tgt) in y.Ptrs)
            {
                var old = x.Ptrs.GetValueOrDefault(po);
                if (old == tgt)
                {
                    // same pointer: did the pointed-to data change?
                    if (A.Blobs.TryGetValue(tgt, out var ba) && B.Blobs.TryGetValue(tgt, out var bb))
                    {
                        var rr = Ranges(ba, bb, Array.Empty<(int, int)>());
                        if (rr.Count > 0) detail.AppendLine($"     {po}->{tgt} contents changed: {string.Join(" ", FormatRanges(ba, bb, rr, 6))}");
                    }
                    continue;
                }
                detail.AppendLine($"     ptr {po}: {old ?? "-"} -> {tgt}");
                foreach (var s in y.Strings.Where(s => s.StartsWith(po + ":", StringComparison.Ordinal) || s.StartsWith(po + ">", StringComparison.Ordinal)).Take(12))
                    detail.AppendLine("        " + s);
            }
            var newStr = y.Strings.Except(x.Strings).Take(20).ToList();
            if (newStr.Count > 0) detail.AppendLine($"     new strings: {string.Join(" | ", newStr)}");
        }
        foreach (var k in na.Keys.Except(nbd.Keys)) detail.AppendLine($"- node ({k.GX},{k.GY}) {na[k].Map}");
        sb.AppendLine($"nodes with reveal/map/tag/status changes: {changedCount}");
        sb.Append(detail);
        if (offHist.Count > 0)
            sb.AppendLine("byte-change histogram over ALL nodes (qword offset: #nodes, ignoring +100/+168): " +
                          string.Join(" ", offHist.OrderByDescending(kv => kv.Value).Take(30).Select(kv => $"+{kv.Key:X}:{kv.Value}")));

        // boat buttons
        var ba2 = A.Buttons.ToDictionary(x => (x.GX, x.GY, x.Kind), x => x);
        foreach (var y in B.Buttons)
        {
            if (!ba2.TryGetValue((y.GX, y.GY, y.Kind), out var x)) { sb.AppendLine($"+ button {y.Kind} ({y.GX},{y.GY})"); continue; }
            if (x.Visible != y.Visible) sb.AppendLine($"~ button {y.Kind} ({y.GX},{y.GY}) vis {x.Visible}->{y.Visible}");
        }

        // entities
        sb.AppendLine("\n== ENTITIES ==");
        var ea = A.Entities.Select(e => $"{e.Meta}").ToHashSet();
        var eb = B.Entities.Select(e => $"{e.Meta}").ToHashSet();
        foreach (var m in eb.Except(ea)) sb.AppendLine("+ " + m);
        foreach (var m in ea.Except(eb)) sb.AppendLine("- " + m);
        foreach (var e in B.Entities.Where(e => e.Info.Count > 0)) sb.AppendLine($"  #{e.Id} {e.Meta}: {e.Info[0]}");

        var outPath = Path.Combine(dir, $"diff-{A.Tag}-{B.Tag}.txt");
        try { Directory.CreateDirectory(dir); File.WriteAllText(outPath, sb.ToString()); } catch (Exception ex) { Console.Error.WriteLine("write failed: " + ex.Message); }
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    private static string Desc(ItemRec i) => $"[{i.Inv} {i.InvName}] ({i.X},{i.Y}) {i.Name} x{i.Stack} {i.Meta}";

    /// <summary>Changed byte runs (merged when gaps ≤ 3), skipping ignore windows.</summary>
    private static List<(int off, int len)> Ranges(byte[] a, byte[] b, (int off, int len)[]? ignore)
    {
        var res = new List<(int, int)>();
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n)
        {
            if (a[i] == b[i] || Ignored(i)) { i++; continue; }
            var s = i; var last = i;
            while (i < n && i - last <= 3) { if (a[i] != b[i] && !Ignored(i)) last = i; i++; }
            res.Add((s, last - s + 1));
        }
        if (a.Length != b.Length) res.Add((n, Math.Abs(a.Length - b.Length)));
        return res;
        bool Ignored(int o) { if (ignore == null) return false; foreach (var (io, il) in ignore) if (o >= io && o < io + il) return true; return false; }
    }

    private static List<string> FormatRanges(byte[] a, byte[] b, List<(int off, int len)> ranges, int max)
    {
        var res = new List<string>();
        foreach (var (o, l) in ranges.Take(max))
        {
            var ll = Math.Min(l, 16);
            string Hex(byte[] x) => o + ll <= x.Length ? Convert.ToHexString(x, o, ll) : "?";
            var extra = "";
            if (l <= 8 && (o & 7) + l <= 8)
            {
                var q = o & ~7;
                if (q + 8 <= a.Length && q + 8 <= b.Length)
                {
                    var va = BitConverter.ToInt64(a, q); var vb = BitConverter.ToInt64(b, q);
                    extra = IsPtr(vb) || IsPtr(va) ? $" (q+{q:X} 0x{va:X}->0x{vb:X})" : $" (i32 {BitConverter.ToInt32(a, o & ~3)}->{BitConverter.ToInt32(b, o & ~3)})";
                }
            }
            res.Add($"+{o:X}[{l}] {Hex(a)}->{Hex(b)}{extra}");
        }
        if (ranges.Count > max) res.Add($"... {ranges.Count - max} more ranges");
        return res;
    }

    private static void AddRanges(List<string> lines, string label, byte[] a, byte[] b, (int, int)[]? ignore, int max = 12)
    {
        var r = Ranges(a, b, ignore);
        if (r.Count == 0) return;
        lines.Add($"{label}: {r.Count} range(s)");
        foreach (var l in FormatRanges(a, b, r, max)) lines.Add("   " + l);
    }
}
