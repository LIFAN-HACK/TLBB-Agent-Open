using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UuMemoryTrace;

/// <summary>
/// Read-only observer for discovering UU-related Game.exe offsets.
/// Does NOT inject into UU, does NOT WriteProcessMemory.
/// Workflow: start this tool → start UU+Game → mark features → value-scan / sample / diff.
/// </summary>
internal static class Program
{
    private static readonly string DefaultOutputRoot =
        Path.Combine(Environment.CurrentDirectory, "artifacts", "uu-observation");
    private static readonly string DefaultCandidates =
        Path.Combine(Environment.CurrentDirectory, "data", "offsets", "uu-static-candidates.json");

    private static readonly string[] UuExactNames = ["uu.dvm", "vv.dvm"];
    private static readonly string[] GameExactNames = ["Game"];

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "UuMemoryTrace (read-only)";

        var outputRoot = Arg(args, "--out") ?? DefaultOutputRoot;
        var candidatesPath = Arg(args, "--candidates") ?? DefaultCandidates;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var runDir = Path.Combine(outputRoot, $"uu-mem-trace-{stamp}");
        Directory.CreateDirectory(runDir);

        var logPath = Path.Combine(runDir, "session.log");
        var samplesPath = Path.Combine(runDir, "samples.jsonl");
        var scansPath = Path.Combine(runDir, "scans.jsonl");
        var markersPath = Path.Combine(runDir, "markers.csv");
        var summaryPath = Path.Combine(runDir, "SUMMARY.md");

        File.WriteAllText(markersPath, "ts,marker,detail\n", Encoding.UTF8);
        WriteReadme(runDir);

        Log(logPath, $"UuMemoryTrace start {DateTime.Now:O}");
        Log(logPath, $"runDir={runDir}");
        Log(logPath, "Safety: ReadProcessMemory only. No inject, no WriteProcessMemory, no SendInput.");

        var candidates = OffsetCatalog.Load(candidatesPath, logPath);
        Log(logPath, $"Loaded {candidates.Chains.Count} pointer chains, {candidates.Watches.Count} absolute watches from candidates.");

        PrintBanner(runDir, candidatesPath);

        var state = new SessionState();
        var lastSnapshot = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            RefreshTargets(state, logPath);
            PrintStatus(state);

            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  r          refresh UU/Game process list");
            Console.WriteLine("  s          sample all candidate chains + watches");
            Console.WriteLine("  d          diff vs last sample");
            Console.WriteLine("  v          known-value scan (int32, e.g. kill count on screen)");
            Console.WriteLine("  w <addr>   watch absolute hex address (e.g. w 0x12345678)");
            Console.WriteLine("  c <name>   sample one named chain from candidates");
            Console.WriteLine("  1..9       feature markers (see help)");
            Console.WriteLine("  m <text>   custom marker");
            Console.WriteLine("  h          help");
            Console.WriteLine("  q          quit + write SUMMARY.md");
            Console.Write("> ");

            var line = Console.ReadLine();
            if (line is null)
                break;

            line = line.Trim();
            if (line.Length == 0)
                continue;

            try
            {
                if (line is "q" or "Q" or "quit" or "exit")
                    break;

                if (line is "h" or "?" or "help")
                {
                    PrintHelp();
                    continue;
                }

                if (line is "r" or "R")
                {
                    RefreshTargets(state, logPath);
                    continue;
                }

                if (line is "s" or "S")
                {
                    if (!EnsureGame(state))
                        continue;
                    lastSnapshot = SampleAll(state, candidates, samplesPath, logPath, "sample");
                    continue;
                }

                if (line is "d" or "D")
                {
                    if (!EnsureGame(state))
                        continue;
                    var now = SampleAll(state, candidates, samplesPath, logPath, "diff-sample");
                    PrintDiff(lastSnapshot, now);
                    lastSnapshot = now;
                    continue;
                }

                if (line is "v" or "V")
                {
                    if (!EnsureGame(state))
                        continue;
                    Console.Write("Known int32 value to scan (e.g. 今日杀怪数=12 → 12): ");
                    var vs = Console.ReadLine()?.Trim();
                    if (!int.TryParse(vs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        Console.WriteLine("Invalid number.");
                        continue;
                    }

                    Console.Write("Max hits to report [40]: ");
                    var hs = Console.ReadLine()?.Trim();
                    var maxHits = 40;
                    if (int.TryParse(hs, out var mh) && mh > 0)
                        maxHits = Math.Min(mh, 200);

                    RunValueScan(state, value, maxHits, scansPath, logPath);
                    continue;
                }

                if (line.StartsWith("w ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!EnsureGame(state))
                        continue;
                    var addrText = line[2..].Trim();
                    if (!TryParseAddress(addrText, out var addr))
                    {
                        Console.WriteLine("Bad address. Example: w 0x1A2B3C00");
                        continue;
                    }

                    SampleAddress(state, addr, samplesPath, logPath);
                    continue;
                }

                if (line.StartsWith("c ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!EnsureGame(state))
                        continue;
                    var name = line[2..].Trim();
                    var chain = candidates.Chains.FirstOrDefault(x =>
                        x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (chain is null)
                    {
                        Console.WriteLine("Unknown chain. Known names:");
                        foreach (var c in candidates.Chains)
                            Console.WriteLine($"  - {c.Name}: {c.Description}");
                        continue;
                    }

                    SampleChain(state, chain, samplesPath, logPath);
                    continue;
                }

                if (line.Length == 1 && char.IsDigit(line[0]))
                {
                    var marker = MarkerFromKey(line[0]);
                    WriteMarker(markersPath, logPath, marker, "");
                    Console.WriteLine($"Marker recorded: {marker}");
                    if (state.Game is not null)
                        lastSnapshot = SampleAll(state, candidates, samplesPath, logPath, $"marker:{marker}");
                    continue;
                }

                if (line.StartsWith("m ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("M ", StringComparison.Ordinal))
                {
                    var text = line[2..].Trim();
                    WriteMarker(markersPath, logPath, "custom", text);
                    Console.WriteLine($"Marker recorded: custom | {text}");
                    if (state.Game is not null)
                        lastSnapshot = SampleAll(state, candidates, samplesPath, logPath, $"marker:custom:{text}");
                    continue;
                }

                Console.WriteLine("Unknown command. Type h for help.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                Log(logPath, "ERROR " + ex);
            }
        }

        WriteSummary(summaryPath, runDir, state, candidates, logPath);
        Console.WriteLine();
        Console.WriteLine($"Done. Output: {runDir}");
        Console.WriteLine($"Open SUMMARY.md for next steps.");
        return 0;
    }

    private static void PrintBanner(string runDir, string candidatesPath)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine(" UuMemoryTrace — UU offset discovery (READ-ONLY)");
        Console.WriteLine("============================================================");
        Console.WriteLine("Safety:");
        Console.WriteLine("  - Only ReadProcessMemory / VirtualQueryEx on Game.exe");
        Console.WriteLine("  - Does NOT inject into UU, does NOT write game memory");
        Console.WriteLine();
        Console.WriteLine("Suggested run:");
        Console.WriteLine("  1) Keep this window open (preferably Run as Administrator)");
        Console.WriteLine("  2) Start UU and bind a Game.exe window");
        Console.WriteLine("  3) In UU: enable 检测杀怪数 / 挂机返点 related UI");
        Console.WriteLine("  4) Press r until both UU + Game are listed");
        Console.WriteLine("  5) Press 1 (mark kill-count) then v → enter the number shown");
        Console.WriteLine("  6) Press s / d while walking / after a kill for diffs");
        Console.WriteLine();
        Console.WriteLine($"Output : {runDir}");
        Console.WriteLine($"Candidates: {candidatesPath}");
        Console.WriteLine("============================================================");
    }

    private static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("Feature markers (digit keys):");
        Console.WriteLine("  1 = kill-count UI / 检测杀怪数");
        Console.WriteLine("  2 = key-combat on / 按键打怪开启");
        Console.WriteLine("  3 = death / 死亡相关");
        Console.WriteLine("  4 = return-point / 返点");
        Console.WriteLine("  5 = horse-bandit / 马贼");
        Console.WriteLine("  6 = grab / 抢怪");
        Console.WriteLine("  7 = red-only / 只打红怪");
        Console.WriteLine("  8 = walk / 走动坐标变化");
        Console.WriteLine("  9 = feature off / 功能关闭");
        Console.WriteLine();
        Console.WriteLine("Value scan tips:");
        Console.WriteLine("  - Prefer small stable ints (kill count, map id).");
        Console.WriteLine("  - After scan, note candidate addresses → add to watches or re-test after kill.");
        Console.WriteLine("  - Pointer chains from UU static strings use module base = Game.exe.");
    }

    private static string MarkerFromKey(char c) => c switch
    {
        '1' => "kill-count",
        '2' => "key-combat",
        '3' => "death",
        '4' => "return-point",
        '5' => "horse-bandit",
        '6' => "grab",
        '7' => "red-only",
        '8' => "walk",
        '9' => "feature-off",
        _ => "marker-" + c
    };

    private static void RefreshTargets(SessionState state, string logPath)
    {
        var selfPid = Environment.ProcessId;
        state.UuList = Process.GetProcesses()
            .Where(p => p.Id != selfPid && IsUuProcess(p))
            .Select(ProcessInfo.TryCreate)
            .Where(x => x is not null)
            .Cast<ProcessInfo>()
            .ToList();

        state.GameList = Process.GetProcesses()
            .Where(p => p.Id != selfPid && IsGameProcess(p))
            .Select(ProcessInfo.TryCreate)
            .Where(x => x is not null)
            .Cast<ProcessInfo>()
            .Where(x => !string.IsNullOrWhiteSpace(x.Path) || !string.IsNullOrWhiteSpace(x.Title))
            .ToList();

        // Prefer Game that is TLBB path/title; else first with a path.
        state.Uu = state.UuList.FirstOrDefault();
        state.Game = state.GameList.FirstOrDefault(g =>
                         g.Path.Contains("TLBB", StringComparison.OrdinalIgnoreCase)
                         || g.Title.Contains("天龙", StringComparison.OrdinalIgnoreCase))
                     ?? state.GameList.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g.Path))
                     ?? state.GameList.FirstOrDefault();

        if (state.Game is not null)
        {
            try
            {
                state.GameBase = Native.GetModuleBaseAddress(state.Game.Pid, Path.GetFileName(state.Game.Path));
                if (state.GameBase == IntPtr.Zero)
                    state.GameBase = Native.GetModuleBaseAddress(state.Game.Pid, "Game.exe");
            }
            catch (Exception ex)
            {
                state.GameBase = IntPtr.Zero;
                Log(logPath, "GetModuleBase failed: " + ex.Message);
            }
        }
        else
        {
            state.GameBase = IntPtr.Zero;
        }

        Log(logPath,
            $"refresh uu={state.Uu?.Pid.ToString() ?? "-"} game={state.Game?.Pid.ToString() ?? "-"} base=0x{(state.GameBase == IntPtr.Zero ? 0 : state.GameBase.ToInt64()):X}");
    }

    private static void PrintStatus(SessionState state)
    {
        Console.WriteLine();
        Console.WriteLine("---- Process status ----");
        if (state.UuList.Count == 0)
            Console.WriteLine("UU : (not running)  ← start uu.dvm.exe");
        else
            foreach (var u in state.UuList)
                Console.WriteLine($"UU : pid={u.Pid}  {u.Name}  {u.Path}");

        if (state.GameList.Count == 0)
            Console.WriteLine("Game: (not running) ← start and bind in UU");
        else
            foreach (var g in state.GameList)
            {
                var mark = state.Game?.Pid == g.Pid ? " [ACTIVE]" : "";
                Console.WriteLine($"Game: pid={g.Pid}  title={Trim(g.Title, 40)}  base will resolve for ACTIVE{mark}");
                Console.WriteLine($"      path={g.Path}");
            }

        if (state.Game is not null)
        {
            Console.WriteLine(
                $"ACTIVE Game base = 0x{(state.GameBase == IntPtr.Zero ? 0 : state.GameBase.ToInt64()):X}  bitness-tool=x86");
            if (state.GameBase == IntPtr.Zero)
                Console.WriteLine("WARN: module base is 0 — try Run as Administrator, or Game is 64-bit while tool is x86.");
        }
    }

    private static bool EnsureGame(SessionState state)
    {
        if (state.Game is null)
        {
            Console.WriteLine("Game.exe not found. Start game + press r.");
            return false;
        }

        if (state.GameBase == IntPtr.Zero)
        {
            Console.WriteLine("Game module base unknown. Try Administrator, then r.");
            // still allow absolute watches / full scan of committed regions
        }

        return true;
    }

    private static Dictionary<string, long?> SampleAll(
        SessionState state,
        OffsetCatalog catalog,
        string samplesPath,
        string logPath,
        string reason)
    {
        var map = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"Sampling ({reason})...");

        foreach (var chain in catalog.Chains)
        {
            var value = TryReadChain(state, chain, out var resolved, out var err);
            map[chain.Name] = value;
            var line = new SampleRow(
                DateTime.Now.ToString("o"),
                reason,
                chain.Name,
                chain.Format,
                resolved,
                value,
                err);
            AppendJsonl(samplesPath, line);
            Console.WriteLine(
                value is null
                    ? $"  {chain.Name,-28} FAIL  {err}"
                    : $"  {chain.Name,-28} 0x{resolved:X} = {value} (0x{value:X})");
        }

        foreach (var watch in catalog.Watches)
        {
            if (!TryParseAddress(watch.Address, out var addr))
                continue;
            var value = TryReadInt32(state.Game!.Pid, addr, out var err);
            map[watch.Name] = value;
            AppendJsonl(samplesPath, new SampleRow(
                DateTime.Now.ToString("o"), reason, watch.Name, watch.Address,
                addr.ToInt64(), value, err));
            Console.WriteLine(
                value is null
                    ? $"  {watch.Name,-28} FAIL  {err}"
                    : $"  {watch.Name,-28} 0x{addr.ToInt64():X} = {value}");
        }

        Log(logPath, $"sample reason={reason} count={map.Count}");
        return map;
    }

    private static void SampleChain(SessionState state, PointerChain chain, string samplesPath, string logPath)
    {
        var value = TryReadChain(state, chain, out var resolved, out var err);
        AppendJsonl(samplesPath, new SampleRow(
            DateTime.Now.ToString("o"), "chain", chain.Name, chain.Format, resolved, value, err));
        Console.WriteLine(value is null
            ? $"FAIL {chain.Name}: {err}"
            : $"OK   {chain.Name}: addr=0x{resolved:X} value={value} (0x{value:X})");
        Log(logPath, $"chain {chain.Name} value={value} err={err}");
    }

    private static void SampleAddress(SessionState state, IntPtr addr, string samplesPath, string logPath)
    {
        var i32 = TryReadInt32(state.Game!.Pid, addr, out var err);
        var bytes = Native.ReadBytes(state.Game.Pid, addr, 16, out _);
        AppendJsonl(samplesPath, new SampleRow(
            DateTime.Now.ToString("o"), "watch", $"abs:{addr.ToInt64():X}", $"0x{addr.ToInt64():X}",
            addr.ToInt64(), i32, err));
        Console.WriteLine(i32 is null
            ? $"FAIL read 0x{addr.ToInt64():X}: {err}"
            : $"OK   0x{addr.ToInt64():X} int32={i32}  hex16={Convert.ToHexString(bytes)}");
        Log(logPath, $"watch 0x{addr.ToInt64():X} = {i32}");
    }

    private static void PrintDiff(Dictionary<string, long?> before, Dictionary<string, long?> after)
    {
        Console.WriteLine("---- Diff (changed only) ----");
        var keys = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k);
        var any = false;
        foreach (var k in keys)
        {
            before.TryGetValue(k, out var b);
            after.TryGetValue(k, out var a);
            if (b == a)
                continue;
            any = true;
            Console.WriteLine($"  {k,-28} {Fmt(b)}  ->  {Fmt(a)}");
        }

        if (!any)
            Console.WriteLine("  (no changes among sampled keys)");
    }

    private static string Fmt(long? v) => v is null ? "null" : v.Value.ToString(CultureInfo.InvariantCulture);

    private static void RunValueScan(SessionState state, int value, int maxHits, string scansPath, string logPath)
    {
        Console.WriteLine($"Scanning Game pid={state.Game!.Pid} for int32={value} (committed regions, may take a while)...");
        var sw = Stopwatch.StartNew();
        var hits = Native.ScanInt32(state.Game.Pid, value, maxHits, out var regions, out var bytesScanned);
        sw.Stop();

        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:0.0}s  regions={regions}  scannedMB={bytesScanned / 1024.0 / 1024.0:0.0}  hits={hits.Count}");
        var baseAddr = state.GameBase;
        for (var i = 0; i < hits.Count; i++)
        {
            var addr = hits[i];
            var rva = baseAddr == IntPtr.Zero ? (long?)null : addr.ToInt64() - baseAddr.ToInt64();
            var rvaText = rva is null ? "-" : (rva.Value >= 0 ? $"+0x{rva.Value:X}" : $"-0x{-rva.Value:X}");
            Console.WriteLine($"  [{i + 1,2}] 0x{addr.ToInt64():X}  Game.exe{rvaText}");
        }

        var row = new
        {
            Time = DateTime.Now.ToString("o"),
            Value = value,
            MaxHits = maxHits,
            Regions = regions,
            BytesScanned = bytesScanned,
            ElapsedMs = sw.ElapsedMilliseconds,
            GamePid = state.Game.Pid,
            GameBase = baseAddr == IntPtr.Zero ? 0 : baseAddr.ToInt64(),
            Hits = hits.Select(h => h.ToInt64()).ToArray()
        };
        AppendJsonl(scansPath, row);
        Log(logPath, $"scan value={value} hits={hits.Count} elapsedMs={sw.ElapsedMilliseconds}");

        if (hits.Count > 0)
        {
            Console.WriteLine("Tip: kill one more monster (kill count +1), press v again with new value,");
            Console.WriteLine("     then keep addresses that appear in BOTH scans — those are real kill-count candidates.");
        }
    }

    private static long? TryReadChain(SessionState state, PointerChain chain, out long resolvedAddr, out string error)
    {
        resolvedAddr = 0;
        error = "";
        if (state.Game is null)
        {
            error = "no game";
            return null;
        }

        // Format: "mod:Game.exe|+0x64|+0x148|+0x10"  or  "base|+0x64|+0x2c"  or absolute "0x...."
        try
        {
            var parts = chain.Format.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                error = "empty format";
                return null;
            }

            IntPtr addr;
            var start = 0;
            if (parts[0].StartsWith("mod:", StringComparison.OrdinalIgnoreCase))
            {
                var mod = parts[0]["mod:".Length..];
                addr = Native.GetModuleBaseAddress(state.Game.Pid, mod);
                if (addr == IntPtr.Zero && mod.Equals("Game.exe", StringComparison.OrdinalIgnoreCase))
                    addr = state.GameBase;
                if (addr == IntPtr.Zero)
                {
                    error = "module base not found: " + mod;
                    return null;
                }

                start = 1;
            }
            else if (parts[0].Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                addr = state.GameBase;
                if (addr == IntPtr.Zero)
                {
                    error = "game base is 0";
                    return null;
                }

                start = 1;
            }
            else if (TryParseAddress(parts[0], out addr))
            {
                start = 1;
            }
            else
            {
                error = "bad base token: " + parts[0];
                return null;
            }

            // Intermediate offsets: dereference pointer at (addr+off) except last which is final field offset
            for (var i = start; i < parts.Length; i++)
            {
                if (!TryParseOffset(parts[i], out var off))
                {
                    error = "bad offset: " + parts[i];
                    return null;
                }

                var next = IntPtr.Add(addr, off);
                var isLast = i == parts.Length - 1;
                if (isLast)
                {
                    // final: read int32 at addr+off
                    var value = TryReadInt32(state.Game.Pid, next, out error);
                    resolvedAddr = next.ToInt64();
                    return value;
                }

                // dereference 32-bit pointer
                var ptrVal = TryReadUInt32(state.Game.Pid, next, out error);
                if (ptrVal is null)
                    return null;
                if (ptrVal.Value < 0x10000)
                {
                    error = $"null/low pointer at 0x{next.ToInt64():X}";
                    return null;
                }

                addr = new IntPtr(unchecked((int)ptrVal.Value)); // x86
            }

            error = "chain had no final field";
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static int? TryReadInt32(int pid, IntPtr addr, out string error)
    {
        error = "";
        var buf = Native.ReadBytes(pid, addr, 4, out error);
        if (buf.Length < 4)
            return null;
        return BitConverter.ToInt32(buf, 0);
    }

    private static uint? TryReadUInt32(int pid, IntPtr addr, out string error)
    {
        error = "";
        var buf = Native.ReadBytes(pid, addr, 4, out error);
        if (buf.Length < 4)
            return null;
        return BitConverter.ToUInt32(buf, 0);
    }

    private static bool TryParseAddress(string text, out IntPtr addr)
    {
        addr = IntPtr.Zero;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        addr = new IntPtr(unchecked((long)v));
        return true;
    }

    private static bool TryParseOffset(string text, out int offset)
    {
        offset = 0;
        text = text.Trim();
        if (text.StartsWith("+", StringComparison.Ordinal))
            text = text[1..];
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
        }

        // allow bare hex like 64 or 2c
        if (int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset))
            return true;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
    }

    private static bool IsUuProcess(Process p)
    {
        try
        {
            var n = p.ProcessName;
            // Exact DVM names only — do NOT use Contains("uu") (matches UuMemoryTrace).
            if (UuExactNames.Any(x => n.Equals(x, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (n.Equals("uu", StringComparison.OrdinalIgnoreCase)
                || n.Equals("vv", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var path = p.MainModule?.FileName ?? "";
                    return path.EndsWith(".dvm.exe", StringComparison.OrdinalIgnoreCase)
                           || path.Contains("\\uu\\", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return true; // name is exactly uu/vv
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGameProcess(Process p)
    {
        try
        {
            // Exact process name "Game" only (TLBB client). Avoid Contains which is too broad.
            if (!GameExactNames.Any(x => p.ProcessName.Equals(x, StringComparison.OrdinalIgnoreCase)))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void WriteMarker(string path, string logPath, string marker, string detail)
    {
        var ts = DateTime.Now.ToString("o");
        File.AppendAllText(path, $"\"{ts}\",\"{marker.Replace("\"", "\"\"")}\",\"{detail.Replace("\"", "\"\"")}\"\n", Encoding.UTF8);
        Log(logPath, $"marker {marker} {detail}");
    }

    private static void AppendJsonl(string path, object row)
    {
        var json = JsonSerializer.Serialize(row, JsonOpts);
        File.AppendAllText(path, json + Environment.NewLine, Encoding.UTF8);
    }

    private static void Log(string path, string message)
    {
        File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}", Encoding.UTF8);
    }

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "...");

    private static void WriteReadme(string runDir)
    {
        File.WriteAllText(Path.Combine(runDir, "README.txt"),
            """
            UuMemoryTrace output (READ-ONLY)

            files:
              session.log     - text log
              markers.csv     - feature markers you pressed
              samples.jsonl   - each sample of candidate chains/watches
              scans.jsonl     - known-value scan results
              SUMMARY.md      - written on quit

            Safety: this tool never injects into UU and never writes game memory.
            """, Encoding.UTF8);
    }

    private static void WriteSummary(string path, string runDir, SessionState state, OffsetCatalog catalog, string logPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# UuMemoryTrace SUMMARY");
        sb.AppendLine();
        sb.AppendLine($"- Time: {DateTime.Now:O}");
        sb.AppendLine($"- RunDir: `{runDir}`");
        sb.AppendLine($"- UU pid: {state.Uu?.Pid.ToString() ?? "-"}");
        sb.AppendLine($"- Game pid: {state.Game?.Pid.ToString() ?? "-"}");
        sb.AppendLine($"- Game base: 0x{(state.GameBase == IntPtr.Zero ? 0 : state.GameBase.ToInt64()):X}");
        sb.AppendLine($"- Candidate chains: {catalog.Chains.Count}");
        sb.AppendLine();
        sb.AppendLine("## Next steps");
        sb.AppendLine("1. Open `scans.jsonl` — find addresses that appear in two kill-count scans with +1 value.");
        sb.AppendLine("2. Open `samples.jsonl` — see which static chains returned stable values.");
        sb.AppendLine("3. Promote confirmed addresses into `data/offsets/<game-version>.json`.");
        sb.AppendLine("4. Optional: export API Monitor RPM log and place beside this folder for correlation.");
        sb.AppendLine();
        sb.AppendLine("## Safety");
        sb.AppendLine("- ReadProcessMemory only.");
        sb.AppendLine("- No inject into UU, no WriteProcessMemory.");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Log(logPath, "summary written");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class SessionState
{
    public List<ProcessInfo> UuList { get; set; } = new();
    public List<ProcessInfo> GameList { get; set; } = new();
    public ProcessInfo? Uu { get; set; }
    public ProcessInfo? Game { get; set; }
    public IntPtr GameBase { get; set; }
}

internal sealed class ProcessInfo
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Title { get; init; } = "";

    public static ProcessInfo? TryCreate(Process p)
    {
        try
        {
            string path = "";
            try { path = p.MainModule?.FileName ?? ""; } catch { /* access */ }
            return new ProcessInfo
            {
                Pid = p.Id,
                Name = p.ProcessName,
                Path = path,
                Title = p.MainWindowTitle ?? ""
            };
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class OffsetCatalog
{
    public List<PointerChain> Chains { get; set; } = new();
    public List<WatchItem> Watches { get; set; } = new();

    public static OffsetCatalog Load(string path, string logPath)
    {
        if (!File.Exists(path))
        {
            File.AppendAllText(logPath, $"candidates missing: {path}{Environment.NewLine}");
            return SeedDefaults();
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var cat = JsonSerializer.Deserialize<OffsetCatalog>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            return cat ?? SeedDefaults();
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"candidates parse fail: {ex.Message}{Environment.NewLine}");
            return SeedDefaults();
        }
    }

    public static OffsetCatalog SeedDefaults() => new()
    {
        Chains =
        {
            new PointerChain
            {
                Name = "static-killcount-2c-family",
                Description = "UU string ]+64]+2c near 2c20 kill-count UI (UNVERIFIED)",
                Format = "mod:Game.exe|+0x64|+0x2c"
            },
            new PointerChain
            {
                Name = "static-kill-148-10",
                Description = "UU string ]+64]+148]+10 near 杀怪数/打死换怪 (UNVERIFIED)",
                Format = "mod:Game.exe|+0x64|+0x148|+0x10"
            },
            new PointerChain
            {
                Name = "static-kill-148-10-4c",
                Description = "UU string ]+64]+148]+10]+4c (UNVERIFIED)",
                Format = "mod:Game.exe|+0x64|+0x148|+0x10|+0x4c"
            }
        }
    };
}

internal sealed class PointerChain
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>mod:Game.exe|+0x64|+0x2c  or base|+0x64|+0x2c</summary>
    public string Format { get; set; } = "";
}

internal sealed class WatchItem
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Note { get; set; } = "";
}

internal sealed record SampleRow(
    string Time,
    string Reason,
    string Name,
    string Spec,
    long ResolvedAddress,
    long? Value,
    string? Error);

internal static class Native
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, int cb, out int lpcbNeeded, uint dwFilterFlag);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, StringBuilder lpFilename, int nSize);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, int cb);

    private const uint ListModulesAll = 0x03;
    private const uint ListModules32Bit = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

    public static IntPtr GetModuleBaseAddress(int pid, string moduleFileName)
    {
        var h = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero)
            return IntPtr.Zero;
        try
        {
            var modules = new IntPtr[1024];
            if (!EnumProcessModulesEx(h, modules, modules.Length * IntPtr.Size, out var needed, ListModulesAll)
                && !EnumProcessModulesEx(h, modules, modules.Length * IntPtr.Size, out needed, ListModules32Bit))
                return IntPtr.Zero;

            var count = needed / IntPtr.Size;
            var want = moduleFileName;
            for (var i = 0; i < count; i++)
            {
                var sb = new StringBuilder(512);
                if (GetModuleFileNameEx(h, modules[i], sb, sb.Capacity) == 0)
                    continue;
                var path = sb.ToString();
                var name = Path.GetFileName(path);
                if (name.Equals(want, StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(want, StringComparison.OrdinalIgnoreCase))
                {
                    if (GetModuleInformation(h, modules[i], out var info, Marshal.SizeOf<MODULEINFO>()))
                        return info.lpBaseOfDll;
                    return modules[i];
                }
            }

            return IntPtr.Zero;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static byte[] ReadBytes(int pid, IntPtr address, int size, out string error)
    {
        error = "";
        var h = OpenProcess(ProcessVmRead | ProcessQueryInformation | ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero)
        {
            error = "OpenProcess failed (try Administrator). err=" + Marshal.GetLastWin32Error();
            return Array.Empty<byte>();
        }

        try
        {
            var buf = new byte[size];
            if (!ReadProcessMemory(h, address, buf, size, out var read) || read <= 0)
            {
                error = "RPM failed err=" + Marshal.GetLastWin32Error();
                return Array.Empty<byte>();
            }

            if (read != size)
                Array.Resize(ref buf, read);
            return buf;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static List<IntPtr> ScanInt32(int pid, int value, int maxHits, out int regions, out long bytesScanned)
    {
        regions = 0;
        bytesScanned = 0;
        var hits = new List<IntPtr>();
        var needle = BitConverter.GetBytes(value);

        var h = OpenProcess(ProcessVmRead | ProcessQueryInformation | ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero)
            return hits;

        try
        {
            var addr = IntPtr.Zero;
            // 32-bit user space ceiling
            var ceiling = new IntPtr(unchecked((int)0x7FFF0000));

            while (addr.ToInt64() < ceiling.ToInt64())
            {
                if (!VirtualQueryEx(h, addr, out var mbi, (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()))
                    break;

                var size = (long)mbi.RegionSize.ToUInt64();
                if (size <= 0)
                    break;

                var readable = mbi.State == MemCommit
                               && (mbi.Protect & PageNoAccess) == 0
                               && (mbi.Protect & PageGuard) == 0
                               && IsReadableProtect(mbi.Protect);

                if (readable && size <= 64 * 1024 * 1024)
                {
                    regions++;
                    var chunkSize = (int)Math.Min(size, 1024 * 1024);
                    long offset = 0;
                    while (offset < size && hits.Count < maxHits)
                    {
                        var toRead = (int)Math.Min(chunkSize, size - offset);
                        var buf = new byte[toRead];
                        var baseAddr = IntPtr.Add(mbi.BaseAddress, (int)offset);
                        if (ReadProcessMemory(h, baseAddr, buf, toRead, out var read) && read > 3)
                        {
                            bytesScanned += read;
                            for (var i = 0; i <= read - 4 && hits.Count < maxHits; i++)
                            {
                                if (buf[i] == needle[0] && buf[i + 1] == needle[1]
                                    && buf[i + 2] == needle[2] && buf[i + 3] == needle[3])
                                {
                                    hits.Add(IntPtr.Add(baseAddr, i));
                                }
                            }
                        }

                        offset += toRead;
                    }
                }

                var next = mbi.BaseAddress.ToInt64() + size;
                if (next <= addr.ToInt64())
                    break;
                addr = new IntPtr(next);
            }
        }
        finally
        {
            CloseHandle(h);
        }

        return hits;
    }

    private static bool IsReadableProtect(uint protect)
    {
        // PAGE_READONLY 0x02, READWRITE 0x04, WRITECOPY 0x08, EXECUTE_READ 0x20, EXECUTE_READWRITE 0x40, EXECUTE_WRITECOPY 0x80
        const uint mask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
        return (protect & mask) != 0;
    }
}
