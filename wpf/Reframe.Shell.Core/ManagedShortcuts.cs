using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Reframe.Shell.Core;

/// <summary>
/// The managed launcher's shortcuts (src/main/toast.ts managedShortcuts / shortcutWrite, src/main/index.ts): the Start
/// menu one (REFRAME_APP_SHORTCUT), through which Windows routes toast clicks, and Reframe.lnk in the installation
/// folder. Both run the launcher (REFRAME_APP_LAUNCHER, a .vbs, through wscript) and carry the app id and the toast
/// activator CLSID. reframe.exe kept them at each start; after the cutover (S14) the shell does.
/// </summary>
public static class ManagedShortcuts
{
    public sealed record Spec(string File, string Target, string Arguments, string WorkingDirectory, string Description,
        string Icon, string AppUserModelId, string ToastActivatorClsid);

    /// <summary>What a shortcut file has (null fields: not set).</summary>
    public sealed record Existing(string? AppUserModelId, string? ToastActivatorClsid);

    /// <summary>managedShortcuts: the two shortcuts, empty without the launcher's variables.</summary>
    public static IReadOnlyList<Spec> Wanted(IDictionary<string, string?> env, string appId, string executable)
    {
        string? fixedId = env.GetValueOrDefault("REFRAME_APP_ID"), file = env.GetValueOrDefault("REFRAME_APP_SHORTCUT"),
            launcher = env.GetValueOrDefault("REFRAME_APP_LAUNCHER");
        if (string.IsNullOrEmpty(fixedId) || appId != fixedId.Trim()) return [];
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(launcher) || !Path.IsPathFullyQualified(file) || !Path.IsPathFullyQualified(launcher)) return [];
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !launcher.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase)) return [];
        var systemRoot = env.GetValueOrDefault("SystemRoot") is { Length: > 0 } r ? r : @"C:\Windows";
        var installRoot = Path.GetDirectoryName(Path.GetDirectoryName(launcher))!;
        Spec Make(string lnk) => new(lnk, Path.Combine(systemRoot, "System32", "wscript.exe"), $"//B //Nologo \"{launcher}\"",
            installRoot, Path.GetFileNameWithoutExtension(file), executable, appId, Notifications.ToastActivatorClsid(appId));
        return [Make(file), Make(Path.Combine(installRoot, "Reframe.lnk"))];
    }

    public enum Operation { None, Create, Update }

    static bool SameClsid(string? a, string b) =>
        string.Equals((a ?? "").Trim('{', '}'), b.Trim('{', '}'), StringComparison.OrdinalIgnoreCase);

    /// <summary>shortcutWrite: create a missing shortcut whole; only add the app id and CLSID to one that lacks them
    /// (its target, arguments and icon stay); nothing when it carries both.</summary>
    public static Operation Decide(Existing? existing, Spec wanted)
    {
        if (existing is null) return Operation.Create;
        return existing.AppUserModelId == wanted.AppUserModelId && SameClsid(existing.ToastActivatorClsid, wanted.ToastActivatorClsid)
            ? Operation.None : Operation.Update;
    }

    /// <summary>Keeps each wanted shortcut (best effort, as reframe: a failure is reported, never fatal).</summary>
    public static List<string> Apply(IEnumerable<Spec> wanted)
    {
        var report = new List<string>();
        foreach (var spec in wanted)
        {
            try
            {
                var operation = Decide(File.Exists(spec.File) ? ShortcutFile.Read(spec.File) : null, spec);
                if (operation != Operation.None) ShortcutFile.Write(spec, operation == Operation.Create);
                report.Add($"{spec.File}: {operation}");
            }
            catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
            {
                report.Add($"{spec.File}: failed ({e.Message})");
            }
        }
        return report;
    }
}

/// <summary>A .lnk file's target and AppUserModel properties through the shell's IShellLink and IPropertyStore.</summary>
public static class ShortcutFile
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    const uint IdProperty = 5, ToastActivatorProperty = 26;
    const ushort VT_LPWSTR = 31, VT_CLSID = 72;
    const int STGM_READ = 0, STGM_READWRITE = 2;
    const uint SLGP_RAWPATH = 4;

    public sealed record Link(string Target, string Arguments, string? AppUserModelId, string? ToastActivatorClsid);

    static string? GetString(IPropertyStore store, uint id)
    {
        var key = new PropertyKey(AppUserModel, id);
        store.GetValue(ref key, out var value);
        try { return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    static string? GetClsid(IPropertyStore store, uint id)
    {
        var key = new PropertyKey(AppUserModel, id);
        store.GetValue(ref key, out var value);
        try { return value.Type == VT_CLSID ? "{" + Marshal.PtrToStructure<Guid>(value.Pointer).ToString().ToUpperInvariant() + "}" : null; }
        finally { PropVariantClear(ref value); }
    }

    public static Link ReadLink(string file)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(file, STGM_READ);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            var args = new StringBuilder(2048);
            link.GetArguments(args, args.Capacity);
            var store = (IPropertyStore)link;
            return new Link(target.ToString(), args.ToString(), GetString(store, IdProperty), GetClsid(store, ToastActivatorProperty));
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    public static ManagedShortcuts.Existing Read(string file)
    {
        var link = ReadLink(file);
        return new ManagedShortcuts.Existing(link.AppUserModelId, link.ToastActivatorClsid);
    }

    /// <summary>Writes <paramref name="spec"/> whole (<paramref name="create"/>), or only its app id and CLSID into the
    /// existing file.</summary>
    public static void Write(ManagedShortcuts.Spec spec, bool create)
    {
        var link = (IShellLinkW)new ShellLink();
        var clsid = Marshal.AllocCoTaskMem(16);
        var id = Marshal.StringToCoTaskMemUni(spec.AppUserModelId);
        try
        {
            if (create)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(spec.File)!);
                link.SetPath(spec.Target);
                link.SetArguments(spec.Arguments);
                link.SetWorkingDirectory(spec.WorkingDirectory);
                link.SetDescription(spec.Description);
                link.SetIconLocation(spec.Icon, 0);
            }
            else ((IPersistFile)link).Load(spec.File, STGM_READWRITE);
            var store = (IPropertyStore)link;
            var key = new PropertyKey(AppUserModel, IdProperty);
            var value = new PropVariant { Type = VT_LPWSTR, Pointer = id };
            store.SetValue(ref key, ref value);
            Marshal.StructureToPtr(Guid.Parse(spec.ToastActivatorClsid), clsid, false);
            key = new PropertyKey(AppUserModel, ToastActivatorProperty);
            value = new PropVariant { Type = VT_CLSID, Pointer = clsid };
            store.SetValue(ref key, ref value);
            store.Commit();
            ((IPersistFile)link).Save(spec.File, true);
        }
        finally
        {
            Marshal.FreeCoTaskMem(id);
            Marshal.FreeCoTaskMem(clsid);
            Marshal.FinalReleaseComObject(link);
        }
    }
}
