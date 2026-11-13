using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// Windows toasts as reframe shows them (src/main/index.ts, app.ts notify and toast.ts), through the Windows Runtime
/// toast API of the target framework (no package). Windows shows a toast, and routes its click, only for an
/// AppUserModelID that a Start menu shortcut carries: the process takes the same id as reframe
/// (<see cref="Notifications.AppUserModelId"/>), and while it runs it serves the toast activator CLSID that shortcut
/// names, so a click from Action Center also reaches it. The shortcut itself is the deployment's (S14); without one the
/// toast is not shown, which is logged and otherwise harmless.
/// </summary>
sealed class Toasts : IDisposable
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("ole32.dll", PreserveSig = false)]
    static extern void CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    static extern int CoRevokeClassObject(uint cookie);

    const uint CLSCTX_LOCAL_SERVER = 4, REGCLS_MULTIPLEUSE = 1;

    public string AppId { get; }
    readonly ToastNotifier? notifier;
    readonly Action<string> activated;
    /// <summary>NotificationKeeper: the shown toast per session, so its Activated handler lives until it is replaced.</summary>
    readonly Dictionary<string, ToastNotification> held = [];
    uint cookie;

    /// <param name="activated">A toast naming this session was clicked (on any thread).</param>
    public Toasts(Action<string> activated)
    {
        this.activated = activated;
        AppId = Notifications.AppUserModelId(Environment.GetEnvironmentVariable("REFRAME_APP_ID"), Environment.ProcessPath ?? "");
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppId);
            notifier = ToastNotificationManager.CreateToastNotifier(AppId);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"toasts unavailable: {e.Message}");
        }
        try
        {
            var clsid = Guid.Parse(Notifications.ToastActivatorClsid(AppId));
            CoRegisterClassObject(ref clsid, new ActivatorFactory(this), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out cookie);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"could not set up toast activation: {e.Message}");
        }
    }

    /// <summary>Shows a session's toast; a newer one for the same session replaces the older.</summary>
    public void Show(string sessionId, string title, string body)
    {
        if (notifier is null) return;
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(Notifications.SessionToastXml(sessionId, title, body));
            var toast = new ToastNotification(xml);
            toast.Activated += (_, args) => Activate((args as ToastActivatedEventArgs)?.Arguments);
            toast.Dismissed += (_, _) => Release(sessionId, toast);
            toast.Failed += (_, e) =>
            {
                Trace.WriteLine($"notification failed: {e.ErrorCode.Message}");
                Release(sessionId, toast);
            };
            Hide(sessionId);
            lock (held) held[sessionId] = toast;
            notifier.Show(toast);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"notification failed: {e.Message}");
        }
    }

    void Hide(string sessionId)
    {
        ToastNotification? previous;
        lock (held) held.Remove(sessionId, out previous);
        if (previous is null) return;
        try { notifier?.Hide(previous); } catch (Exception) { }
    }

    void Release(string sessionId, ToastNotification toast)
    {
        lock (held)
            if (held.TryGetValue(sessionId, out var current) && current == toast) held.Remove(sessionId);
    }

    void Activate(string? arguments)
    {
        if (Notifications.SessionFromActivation(arguments) is { } sessionId) activated(sessionId);
        else Trace.WriteLine("notification activation ignored");
    }

    public void Dispose()
    {
        if (cookie != 0) CoRevokeClassObject(cookie);
        cookie = 0;
    }

    // ---------- the toast activator (INotificationActivationCallback) ----------

    [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INotificationActivationCallback
    {
        void Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
            IntPtr data, uint count);
    }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance);
        [PreserveSig] int LockServer(bool lockServer);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Activator(Toasts owner) : INotificationActivationCallback
    {
        public void Activate(string appUserModelId, string invokedArgs, IntPtr data, uint count) => owner.Activate(invokedArgs);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivatorFactory(Toasts owner) : IClassFactory
    {
        const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110), E_NOINTERFACE = unchecked((int)0x80004002);

        public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance)
        {
            instance = IntPtr.Zero;
            if (outer != IntPtr.Zero) return CLASS_E_NOAGGREGATION;
            var unknown = Marshal.GetIUnknownForObject(new Activator(owner));
            try
            {
                return Marshal.QueryInterface(unknown, in riid, out instance) == 0 ? 0 : E_NOINTERFACE;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int LockServer(bool lockServer) => 0;
    }
}
