using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace POE2Radar.Overlay.Diagnostics;

/// <summary>
/// Session diagnostics log for bug reports from testers. <see cref="Init"/> (first thing in Program.cs)
/// tees <see cref="Console.Out"/> / <see cref="Console.Error"/> into <c>logs\radar-yyyyMMdd-HHmmss.log</c>
/// next to the exe (sibling of <c>config\</c>): every line is timestamped (HH:mm:ss.fff) + thread-tagged,
/// redacted (<see cref="Redact"/>), flushed per line, capped at <see cref="MaxSessionBytes"/>, and only the
/// last <see cref="KeepSessions"/> session logs are kept. The console output itself is unchanged.
/// Also hooks unhandled / unobserved-task exceptions so crashes land in the log with a stack trace.
/// Notes, JSON snapshots and the zip export (/api/diag/*) are built here too. Read-side only — this
/// never touches the game process.
/// </summary>
internal static class RadarLog
{
    public const long MaxSessionBytes = 20L * 1024 * 1024;
    public const int KeepSessions = 10;
    private const int KeepExports = 10;

    public static string BaseDir { get; } = AppContext.BaseDirectory;
    public static string LogDir { get; } = Path.Combine(BaseDir, "logs");
    public static string SnapshotDir { get; } = Path.Combine(LogDir, "snapshots");
    public static string ExportDir { get; } = Path.Combine(BaseDir, "exports");
    private static string ConfigDir => Path.Combine(BaseDir, "config");

    /// <summary>Full path of this session's log file ("" when file logging failed to start).</summary>
    public static string SessionPath { get; private set; } = "";
    public static string SessionStem { get; private set; } = "";
    public static DateTime StartedAt { get; } = DateTime.Now;
    public static long SessionBytes => Interlocked.Read(ref _bytes);
    public static bool Capped => _capped;

    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static long _bytes;
    private static volatile bool _capped;
    private static string? _lastLine;   // consecutive-duplicate suppression
    private static int _repeat;
    private static bool _initialized;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            Directory.CreateDirectory(LogDir);
            SessionStem = $"radar-{DateTime.Now:yyyyMMdd-HHmmss}";
            var path = Path.Combine(LogDir, SessionStem + ".log");
            PruneOldSessions(KeepSessions - 1);   // leave room for this one
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _file = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            SessionPath = path;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Diagnostics log disabled: {ex.Message}");
            _file = null;
        }

        Console.SetOut(new TeeWriter(Console.Out, "OUT"));
        Console.SetError(new TeeWriter(Console.Error, "ERR"));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteRaw("CRASH", $"Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteRaw("CRASH", $"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        WriteHeader();
    }

    private static void WriteHeader()
    {
        WriteRaw("INFO", "==================== POE2Radar diagnostics session ====================");
        foreach (var line in SystemInfoLines()) WriteRaw("INFO", line);
        WriteRaw("INFO", "=======================================================================");
    }

    /// <summary>App/OS/runtime facts — the log header and the export's system-info.txt.</summary>
    public static IEnumerable<string> SystemInfoLines()
    {
        yield return $"App version : {UpdateChecker.Current}";
        yield return $"OS          : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        yield return $".NET        : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})";
        yield return $"Exe dir     : {Redact(BaseDir)}";
        yield return $"Session log : {Redact(SessionPath)}";
        yield return $"Started     : {StartedAt:yyyy-MM-dd HH:mm:ss zzz}";
        yield return $"CPU cores   : {Environment.ProcessorCount}; 64-bit process: {Environment.Is64BitProcess}";
        if (_attachInfo != null) yield return $"Game        : {_attachInfo}";
        if (_windowInfo != null) yield return $"Game window : {_windowInfo}";
    }

    private static string? _attachInfo, _windowInfo;
    private static int _winW = -1, _winH = -1;

    /// <summary>Record the attached game process (name, PID, main-module base/size) in the log.</summary>
    public static void NoteAttach(string processName, int pid, nint moduleBase, uint moduleSize)
    {
        _attachInfo = $"{processName} (PID {pid}) main module base 0x{moduleBase:X} size 0x{moduleSize:X} ({moduleSize / 1024.0 / 1024.0:F1} MB)";
        WriteRaw("INFO", "Game attach: " + _attachInfo);
    }

    /// <summary>Record the game window client size (logged when it is first seen or changes).</summary>
    public static void NoteGameWindow(int width, int height)
    {
        if (width == _winW && height == _winH) return;   // per-frame call: no allocation unless it changed
        _winW = width; _winH = height;
        _windowInfo = $"{width}x{height}";
        WriteRaw("INFO", "Game window: " + _windowInfo);
    }

    /// <summary>Tester note: a "=== NOTE: ..." marker line in the session log.</summary>
    public static void Note(string text)
    {
        text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 2000) text = text[..2000] + "…";
        WriteRaw("NOTE", "=== NOTE: " + text);
    }

    /// <summary>Write a (redacted) JSON snapshot next to the session log; returns its path.</summary>
    public static string WriteSnapshot(string json)
    {
        Directory.CreateDirectory(SnapshotDir);
        var stem = string.IsNullOrEmpty(SessionStem) ? "radar" : SessionStem;
        var path = Path.Combine(SnapshotDir, $"{stem}-snap-{DateTime.Now:HHmmss-fff}.json");
        File.WriteAllText(path, Redact(json), new UTF8Encoding(false));
        WriteRaw("NOTE", $"=== SNAPSHOT: {Redact(path)} ({new FileInfo(path).Length / 1024.0:F1} KB)");
        return path;
    }

    /// <summary>
    /// Build the bug-report zip (kept session logs + snapshots + radar_settings.json + display_rules.json +
    /// system-info.txt, everything redacted), save a copy under exports\ and return (path, bytes).
    /// </summary>
    public static (string Path, string FileName, byte[] Bytes) Export()
    {
        var name = $"POE2Radar-diag-{UpdateChecker.Current}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        WriteRaw("NOTE", "=== EXPORT: " + name);
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (Directory.Exists(LogDir))
                    foreach (var f in Directory.GetFiles(LogDir, "radar-*.log").OrderBy(f => f))
                        AddText(zip, "logs/" + Path.GetFileName(f), f);
                if (Directory.Exists(SnapshotDir))
                    foreach (var f in Directory.GetFiles(SnapshotDir, "*.json").OrderBy(f => f))
                        AddText(zip, "logs/snapshots/" + Path.GetFileName(f), f);
                foreach (var cfg in new[] { "radar_settings.json", "display_rules.json" })
                {
                    var f = Path.Combine(ConfigDir, cfg);
                    if (File.Exists(f)) AddText(zip, "config/" + cfg, f);
                }
                var info = new StringBuilder();
                foreach (var l in SystemInfoLines()) info.AppendLine(l);
                info.AppendLine($"Exported    : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
                info.AppendLine();
                info.AppendLine("Redaction: user profile path -> %USERPROFILE%, Windows user name -> <user>, machine name -> <machine>,");
                info.AppendLine("character name(s) seen this session -> <character>, e-mail addresses -> <email>.");
                var e = zip.CreateEntry("system-info.txt");
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(Redact(info.ToString()));
            }
            bytes = ms.ToArray();
        }
        Directory.CreateDirectory(ExportDir);
        var path = Path.Combine(ExportDir, name);
        File.WriteAllBytes(path, bytes);
        PruneFiles(ExportDir, "POE2Radar-diag-*.zip", KeepExports);
        WriteRaw("NOTE", $"=== EXPORT written: {Redact(path)} ({bytes.Length / 1024.0:F1} KB)");
        return (path, name, bytes);
    }

    private static void AddText(ZipArchive zip, string entryName, string file)
    {
        string text;
        try
        {
            // The live session log is open for append — share read/write so we can still read it.
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, Encoding.UTF8);
            text = r.ReadToEnd();
        }
        catch (Exception ex) { text = $"<could not read {Path.GetFileName(file)}: {ex.Message}>"; }
        var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(Redact(text));
    }

    // ── redaction ─────────────────────────────────────────────────────────────────────────────

    private static readonly string UserProfile = SafeEnv(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private static readonly string UserName = SafeEnv(() => Environment.UserName);
    private static readonly string MachineName = SafeEnv(() => Environment.MachineName);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly object NameGate = new();
    private static string[] _characterNames = Array.Empty<string>();

    private static string SafeEnv(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

    /// <summary>Register the local character name so it is redacted from the log, snapshots and exports.</summary>
    public static void AddCharacterName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length < 2) return;
        lock (NameGate)
        {
            if (_characterNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
            _characterNames = _characterNames.Append(name).OrderByDescending(n => n.Length).ToArray();
        }
    }

    /// <summary>Strip personal data: profile path, user/machine name, character name(s), e-mails.</summary>
    public static string Redact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (UserProfile.Length > 3)
        {
            s = s.Replace(UserProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            // JSON-escaped form (C:\\Users\\name) as it appears inside serialized snapshots/configs.
            s = s.Replace(UserProfile.Replace("\\", "\\\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        s = Email.Replace(s, "<email>");
        foreach (var n in _characterNames) s = ReplaceWord(s, n, "<character>");
        if (UserName.Length >= 2) s = ReplaceWord(s, UserName, "<user>");
        if (MachineName.Length >= 2) s = ReplaceWord(s, MachineName, "<machine>");
        return s;
    }

    private static string ReplaceWord(string s, string word, string with)
    {
        if (s.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) return s;
        return Regex.Replace(s, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}_])", with,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // ── file writing ──────────────────────────────────────────────────────────────────────────

    /// <summary>Write one log line to the session file only (not to the console).</summary>
    public static void WriteRaw(string tag, string text)
    {
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            AppendLine(tag, line, dedupe: false);
    }

    private static void AppendLine(string tag, string line, bool dedupe)
    {
        if (_file == null) return;
        lock (Gate)
        {
            if (_file == null || _capped) return;
            if (dedupe)
            {
                if (line.Length > 0 && line == _lastLine)
                {
                    // Collapse consecutive identical spam lines; still emit a heartbeat every 1000 repeats.
                    if (++_repeat % 1000 != 0) return;
                    WriteLocked("DUP", $"(previous line repeated {_repeat} times so far)");
                    return;
                }
                FlushRepeatLocked();
                _lastLine = line;
            }
            else FlushRepeatLocked();
            WriteLocked(tag, line);
        }
    }

    private static void FlushRepeatLocked()
    {
        if (_repeat > 0 && _repeat % 1000 != 0) WriteLocked("DUP", $"(previous line repeated {_repeat} times in total)");
        _repeat = 0;
    }

    private static void WriteLocked(string tag, string line)
    {
        var t = Thread.CurrentThread;
        var thread = t.Name ?? (t.IsThreadPoolThread ? $"pool-{t.ManagedThreadId}" : $"T{t.ManagedThreadId}");
        if (thread.StartsWith("POE2Radar.", StringComparison.Ordinal)) thread = thread["POE2Radar.".Length..];
        var full = $"{DateTime.Now:HH:mm:ss.fff} [{thread}] {tag} {Redact(line)}";
        try
        {
            if (_bytes + full.Length + 2 > MaxSessionBytes)
            {
                _file!.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{thread}] INFO === session log cap reached ({MaxSessionBytes / 1024 / 1024} MB); file logging stopped (console continues).");
                _capped = true;
                return;
            }
            _file!.WriteLine(full);
            Interlocked.Add(ref _bytes, Encoding.UTF8.GetByteCount(full) + 2);
        }
        catch { /* disk full / file gone — never take the overlay down for logging */ }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_file == null) return;
            FlushRepeatLocked();
            WriteLocked("INFO", "Session end.");
            try { _file.Flush(); _file.Dispose(); } catch { }
            _file = null;
        }
    }

    private static void PruneOldSessions(int keep)
    {
        PruneFiles(LogDir, "radar-*.log", keep);
        // Drop snapshots whose session log no longer exists.
        if (!Directory.Exists(SnapshotDir)) return;
        var kept = Directory.GetFiles(LogDir, "radar-*.log").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(SessionStem)) kept.Add(SessionStem);
        foreach (var f in Directory.GetFiles(SnapshotDir, "*.json"))
        {
            var name = Path.GetFileName(f);
            var i = name.IndexOf("-snap-", StringComparison.Ordinal);
            if (i <= 0 || !kept.Contains(name[..i])) TryDelete(f);
        }
    }

    private static void PruneFiles(string dir, string pattern, int keep)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, pattern).OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(Math.Max(0, keep)))
                TryDelete(f);
        }
        catch { }
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }

    /// <summary>
    /// Console writer that forwards everything to the original console writer unchanged and also
    /// assembles complete lines into the session log. Partial writes (Write without newline) are
    /// buffered per writer until the newline arrives.
    /// </summary>
    private sealed class TeeWriter(TextWriter inner, string tag) : TextWriter
    {
        private readonly StringBuilder _line = new();
        private readonly object _gate = new();
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            lock (_gate) { inner.Write(value); Feed(value); }
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            lock (_gate) { inner.Write(value); foreach (var c in value) Feed(c); }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate) { inner.Write(buffer, index, count); for (int i = 0; i < count; i++) Feed(buffer[index + i]); }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate) { inner.WriteLine(value); if (value != null) foreach (var c in value) Feed(c); Feed('\n'); }
        }

        public override void WriteLine()
        {
            lock (_gate) { inner.WriteLine(); Feed('\n'); }
        }

        public override void Flush() { lock (_gate) inner.Flush(); }

        private void Feed(char c)
        {
            if (c == '\r') return;
            if (c != '\n') { _line.Append(c); return; }
            var s = _line.ToString();
            _line.Clear();
            AppendLine(tag, s, dedupe: true);
        }
    }
}
