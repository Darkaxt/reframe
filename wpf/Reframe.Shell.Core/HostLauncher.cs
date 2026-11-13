using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Reframe.Shell.Core;

/// <summary>Starts a profile's host: called with the host folder and its token, returns the new host's descriptor.</summary>
public delegate Task<HostDescriptor> HostStarter(string hostDir, string token, CancellationToken cancel);

/// <summary>
/// Starts the Rust PTY host as reframe does (src/main/ptyhost-runtime.ts, ptyhost-client.ts <c>launch</c>): from a
/// versioned copy of its files in the profile's <c>ptyhost/runtimes</c> (so the shell's own folder can be replaced while
/// the host outlives it), detached, with REFRAME_PTYHOST_DIR and REFRAME_PTYHOST_VERSION, then waits for its descriptor.
/// The pipe name is the host's own (derived from the host folder and its pid), so reframe and the shell find the same
/// host through the descriptors.
/// </summary>
public sealed class HostLauncher(string sourceDir, string version)
{
    /// <summary>What a Rust host runs from (RUST_HOST_FILES in src/main/ptyhost-files.ts).</summary>
    public static readonly string[] Files = ["reframe-host.exe", "conpty.dll", "OpenConsole.exe"];
    public const string Exe = "reframe-host.exe";
    static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(20);

    public string SourceDir { get; } = sourceDir;

    /// <summary>The folder with the host's files: <paramref name="explicitDir"/> (<c>--host-dir</c>), else
    /// REFRAME_RUST_HOST_DIR (as reframe's development builds take it), else <c>reframe-host</c> beside the shell; null
    /// when none of them has all the files.</summary>
    public static string? FindSource(string? explicitDir, string baseDir)
    {
        var dir = explicitDir ?? Environment.GetEnvironmentVariable("REFRAME_RUST_HOST_DIR") ?? Path.Combine(baseDir, "reframe-host");
        return Files.All(f => File.Exists(Path.Combine(dir, f))) ? Path.GetFullPath(dir) : null;
    }

    public async Task<HostDescriptor> StartAsync(string hostDir, string token, CancellationToken cancel = default)
    {
        var root = Environment.GetEnvironmentVariable("REFRAME_PTYHOST_ROOT") is { Length: > 0 } r ? r : Path.Combine(hostDir, "runtimes");
        var copy = EnsureCopy(SourceDir, root, $"shell-rust-{Hash(SourceDir)[..12]}");
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string?)e.Value ?? "";
        env["REFRAME_PTYHOST_DIR"] = hostDir;
        env["REFRAME_PTYHOST_VERSION"] = version;
        env.Remove("ELECTRON_RUN_AS_NODE");
        var pid = Detached.Start(Path.Combine(copy, Exe), env, hostDir);

        var deadline = DateTime.UtcNow + LaunchTimeout;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (HostFiles.ReadDescriptors(hostDir).FirstOrDefault(d => d.Pid == pid) is { } descriptor) return descriptor;
            if (!HostFiles.Alive(pid)) throw new IOException($"the pty host exited on start (see host.log in {hostDir})");
            if (DateTime.UtcNow > deadline)
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
                throw new TimeoutException("the pty host did not start in time");
            }
            await Task.Delay(50, cancel);
        }
    }

    /// <summary>Hash of what the host runs (runtimeHash in ptyhost-files.ts: the kind, then each file's name and content).</summary>
    public static string Hash(string sourceDir)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("rust"));
        foreach (var file in Files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file));
            hash.AppendData(File.ReadAllBytes(Path.Combine(sourceDir, file)));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Copies the host's files to <c>&lt;root&gt;/&lt;name&gt;</c> unless there (ensureRustHostCopy: made under a
    /// temporary name first, so a copy is whole or absent).</summary>
    public static string EnsureCopy(string sourceDir, string root, string name)
    {
        var target = Path.Combine(root, name);
        if (File.Exists(Path.Combine(target, Exe))) return target;
        var temp = Path.Combine(root, $"{name}.tmp-{Environment.ProcessId}");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        foreach (var file in Files) File.Copy(Path.Combine(sourceDir, file), Path.Combine(temp, file));
        try
        {
            Directory.Move(temp, target);
        }
        catch (IOException)
        {
            // Another client made it meanwhile.
            Directory.Delete(temp, true);
            if (!File.Exists(Path.Combine(target, Exe))) throw;
        }
        return target;
    }

    /// <summary>Win32.spawnDetached of src/main/win32.ts: no console, its own process group, out of the shell's job
    /// when the job allows it, so the host outlives the shell.</summary>
    static class Detached
    {
        const uint DETACHED_PROCESS = 0x8, CREATE_NEW_PROCESS_GROUP = 0x200, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessW(string? app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string cwd, ref STARTUPINFO startup, out PROCESS_INFORMATION info);

        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        public static int Start(string exe, IDictionary<string, string> env, string cwd)
        {
            var block = new StringBuilder();
            foreach (var (key, value) in env) block.Append(key).Append('=').Append(value).Append('\0');
            block.Append('\0');
            var environment = Marshal.StringToHGlobalUni(block.ToString());
            try
            {
                foreach (var extra in new[] { CREATE_BREAKAWAY_FROM_JOB, 0u })
                {
                    var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                    var flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT | extra;
                    if (!CreateProcessW(exe, new StringBuilder($"\"{exe}\""), IntPtr.Zero, IntPtr.Zero, false, flags, environment, cwd, ref startup, out var info))
                        continue;
                    CloseHandle(info.hThread);
                    CloseHandle(info.hProcess);
                    return info.dwProcessId;
                }
                throw new IOException($"could not start the pty host {exe}", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            finally
            {
                Marshal.FreeHGlobal(environment);
            }
        }
    }
}
