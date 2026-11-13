using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// src/consolehelper as the shell has it: each request runs in a short-lived copy of the shell started with
/// <c>--console-helper &lt;request&gt;</c>, because attaching to another process's console must not happen in the shell
/// itself. The copy answers one JSON line on its standard output and exits; it never opens a window.
/// </summary>
sealed class ConsoleHelperClient : IConsoleHelper
{
    public const string Argument = "--console-helper";

    public Task<ConsoleResult> InterruptAsync(int pid, int times, int intervalMs) =>
        RunAsync(new JsonObject { ["type"] = "interrupt", ["pid"] = pid, ["times"] = times, ["intervalMs"] = intervalMs });

    public Task<ConsoleResult> TypeAsync(int pid, string text) => RunAsync(new JsonObject { ["type"] = "type", ["pid"] = pid, ["text"] = text });

    public Task<ConsoleResult> OwnersAsync(IReadOnlyList<int> pids) =>
        RunAsync(new JsonObject { ["type"] = "owners", ["pids"] = new JsonArray([.. pids.Select(p => (JsonNode)p)]) });

    public Task<ConsoleResult> ConsoleProcessesAsync(int pid) => RunAsync(new JsonObject { ["type"] = "console-processes", ["pid"] = pid });

    static async Task<ConsoleResult> RunAsync(JsonObject request, int timeoutMs = 10_000)
    {
        if (Environment.ProcessPath is not { } self) return ConsoleResult.Failed("no executable to run the console helper");
        var start = new ProcessStartInfo(self) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(request.ToJsonString());
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("not started");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ConsoleResult.Failed($"console helper did not start: {error.Message}");
        }
        using (process)
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return Parse(output);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return ConsoleResult.Failed("console helper timed out");
            }
        }
    }

    static ConsoleResult Parse(string output)
    {
        try
        {
            var root = JsonNode.Parse(output);
            if (root is null) return ConsoleResult.Failed("console helper exited");
            if ((bool?)root["ok"] != true) return ConsoleResult.Failed((string?)root["error"] ?? "console helper failed");
            var owners = root["owners"]?.AsArray().Select(o => new ConsoleOwner((int)o!["pid"]!, (string?)o["consoleWindow"], (string?)o["owner"], (string?)o["rootOwner"])).ToList();
            var processes = root["processes"]?.AsArray().Select(p => new AttachedProcess((int)p!["pid"]!, (string?)p["name"])).ToList();
            return new ConsoleResult(true, null, owners, processes);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return ConsoleResult.Failed("console helper exited");
        }
    }
}

/// <summary>The helper's side (src/consolehelper/index.ts): runs one request attached to another console.</summary>
static class ConsoleHelperMain
{
    [StructLayout(LayoutKind.Explicit)]
    struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int bKeyDown;
        [FieldOffset(8)] public ushort wRepeatCount;
        [FieldOffset(10)] public ushort wVirtualKeyCode;
        [FieldOffset(12)] public ushort wVirtualScanCode;
        [FieldOffset(14)] public ushort UnicodeChar;
        [FieldOffset(16)] public uint dwControlKeyState;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetConsoleProcessList([Out] uint[] list, uint count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteConsoleInputW(IntPtr handle, INPUT_RECORD[] records, uint count, out uint written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] name, ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern short VkKeyScanW(char ch);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint mapType);

    const uint GENERIC_READ_WRITE = 0xc0000000, FILE_SHARE_READ_WRITE = 3, OPEN_EXISTING = 3;
    const ushort KEY_EVENT = 1;
    const uint LEFT_CTRL_PRESSED = 0x0008, SHIFT_PRESSED = 0x0010, GW_OWNER = 4, GA_ROOTOWNER = 3, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    readonly record struct Key(ushort Vk, ushort Scan, char Char, uint State);

    static readonly Key CtrlC = new(0x43, 0x2e, (char)3, LEFT_CTRL_PRESSED);

    /// <summary>The key that produces <paramref name="ch"/> on the current keyboard layout ("\r" is Enter).</summary>
    static Key KeyFor(char ch)
    {
        if (ch == '\r') return new Key(0x0d, 0x1c, '\r', 0);
        var code = VkKeyScanW(ch);
        var vk = (ushort)(code & 0xff);
        return new Key(vk, (ushort)MapVirtualKeyW(vk, 0), ch, ((code >> 8) & 1) != 0 ? SHIFT_PRESSED : 0);
    }

    static string? Handle(IntPtr value) => value == IntPtr.Zero ? null : value.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Runs <paramref name="request"/>, writes the answer to standard output (opened before attaching to any
    /// console, so it stays the shell's pipe) and returns the exit code.</summary>
    public static int Run(string request)
    {
        using var stdout = Console.OpenStandardOutput();
        JsonObject answer;
        try
        {
            answer = Handle(JsonNode.Parse(request)!.AsObject());
        }
        catch (Exception error)
        {
            answer = Failed(error.Message);
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(answer.ToJsonString());
        stdout.Write(bytes);
        stdout.Flush();
        return 0;
    }

    static JsonObject Failed(string error) => new() { ["ok"] = false, ["error"] = error };

    static JsonObject Handle(JsonObject request)
    {
        var pid = (uint?)request["pid"] ?? 0;
        switch ((string?)request["type"])
        {
            case "interrupt":
                return TypeKeys(pid, [.. Enumerable.Repeat(new[] { CtrlC }, (int?)request["times"] ?? 1)], (int?)request["intervalMs"] ?? 0);
            case "type":
                return TypeKeys(pid, [((string?)request["text"] ?? "").Select(KeyFor).ToArray()], 0);
            case "owners":
                var owners = new JsonArray();
                foreach (var p in request["pids"]?.AsArray() ?? [])
                {
                    var id = (uint)p!;
                    JsonObject owner = new() { ["pid"] = id, ["consoleWindow"] = null, ["owner"] = null, ["rootOwner"] = null };
                    FreeConsole();
                    if (AttachConsole(id))
                    {
                        var window = GetConsoleWindow();
                        owner["consoleWindow"] = Handle(window);
                        owner["owner"] = window == IntPtr.Zero ? null : Handle(GetWindow(window, GW_OWNER));
                        owner["rootOwner"] = window == IntPtr.Zero ? null : Handle(GetAncestor(window, GA_ROOTOWNER));
                        FreeConsole();
                    }
                    owners.Add(owner);
                }
                return new JsonObject { ["ok"] = true, ["owners"] = owners };
            case "console-processes":
                FreeConsole();
                if (!AttachConsole(pid)) return Failed($"AttachConsole({pid}) failed ({Marshal.GetLastWin32Error()})");
                var list = new uint[256];
                uint count;
                try
                {
                    count = GetConsoleProcessList(list, (uint)list.Length);
                }
                finally
                {
                    FreeConsole();
                }
                var self = (uint)Environment.ProcessId;
                var processes = new JsonArray();
                foreach (var p in list.Take((int)Math.Min(count, (uint)list.Length)).Where(p => p != self))
                    processes.Add(new JsonObject { ["pid"] = p, ["name"] = ImageName(p) });
                return new JsonObject { ["ok"] = true, ["processes"] = processes };
            default:
                return Failed("unknown request");
        }
    }

    /// <summary>Types groups of keys into the console of <paramref name="pid"/>, as if pressed there, with a pause between groups.</summary>
    static JsonObject TypeKeys(uint pid, Key[][] groups, int pauseMs)
    {
        FreeConsole();
        if (!AttachConsole(pid)) return Failed($"AttachConsole failed ({Marshal.GetLastWin32Error()})");
        var input = CreateFileW("CONIN$", GENERIC_READ_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (input == IntPtr.Zero || input == new IntPtr(-1))
        {
            var error = $"CONIN$ not available ({Marshal.GetLastWin32Error()})";
            FreeConsole();
            return Failed(error);
        }
        try
        {
            for (var i = 0; i < groups.Length; i++)
            {
                if (i > 0) Thread.Sleep(pauseMs);
                foreach (var key in groups[i])
                {
                    INPUT_RECORD Record(bool down) => new()
                    {
                        EventType = KEY_EVENT, bKeyDown = down ? 1 : 0, wRepeatCount = 1, wVirtualKeyCode = key.Vk,
                        wVirtualScanCode = key.Scan, UnicodeChar = key.Char, dwControlKeyState = key.State,
                    };
                    if (!WriteConsoleInputW(input, [Record(true)], 1, out _) || !WriteConsoleInputW(input, [Record(false)], 1, out _))
                        return Failed($"WriteConsoleInput failed ({Marshal.GetLastWin32Error()})");
                }
            }
            return new JsonObject { ["ok"] = true };
        }
        finally
        {
            CloseHandle(input);
            FreeConsole();
        }
    }

    static string? ImageName(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            if (!QueryFullProcessImageNameW(process, 0, buffer, ref size)) return null;
            return Path.GetFileName(new string(buffer, 0, (int)size));
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
