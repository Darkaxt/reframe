using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Reframe.Shell.Core;

namespace Reframe.Shell;

/// <summary>
/// app.ts askToClose as Electron's dialog.showMessageBox shows it on Windows: a task dialog (TaskDialogIndirect) titled
/// "Close window", the message as main instruction and the detail as content, "End sessions" and "Keep in the tray" as
/// command links (Electron's default for buttons that are not common ones), Cancel as the common Cancel button, Keep
/// the default, and the "Don't ask again" check box. Needs common controls 6 (app.manifest).
/// </summary>
static class CloseWindowDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    struct TASKDIALOG_BUTTON
    {
        public int nButtonID;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszButtonText;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    struct TASKDIALOGCONFIG
    {
        public uint cbSize;
        public IntPtr hwndParent, hInstance;
        public uint dwFlags, dwCommonButtons;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszWindowTitle;
        public IntPtr hMainIcon;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszMainInstruction;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszContent;
        public uint cButtons;
        public IntPtr pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public IntPtr pRadioButtons;
        public int nDefaultRadioButton;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszVerificationText;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszExpandedInformation, pszExpandedControlText, pszCollapsedControlText;
        public IntPtr hFooterIcon;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszFooter;
        public IntPtr pfCallback, lpCallbackData;
        public uint cxWidth;
    }

    const uint TDF_USE_HICON_MAIN = 0x2, TDF_ALLOW_DIALOG_CANCELLATION = 0x8, TDF_USE_COMMAND_LINKS = 0x10, TDF_POSITION_RELATIVE_TO_WINDOW = 0x1000;
    const uint TDCBF_CANCEL_BUTTON = 0x8;
    const int IDCANCEL = 2, END = 100, KEEP = 101, IDI_QUESTION = 32514;

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG config, out int button, out int radio, out bool verification);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    /// <summary>"end", "keep" or null (Cancel), and whether the answer is to be kept.</summary>
    public static (string? Choice, bool Remember) Ask(Window owner, int count)
    {
        var q = CloseWindowQuestion.For(count);
        var buttons = new[] { new TASKDIALOG_BUTTON { nButtonID = END, pszButtonText = q.End }, new TASKDIALOG_BUTTON { nButtonID = KEEP, pszButtonText = q.Keep } };
        var size = Marshal.SizeOf<TASKDIALOG_BUTTON>();
        var array = Marshal.AllocHGlobal(size * buttons.Length);
        try
        {
            for (var i = 0; i < buttons.Length; i++) Marshal.StructureToPtr(buttons[i], array + i * size, false);
            var config = new TASKDIALOGCONFIG
            {
                cbSize = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                hwndParent = new WindowInteropHelper(owner).Handle,
                dwFlags = TDF_USE_HICON_MAIN | TDF_ALLOW_DIALOG_CANCELLATION | TDF_USE_COMMAND_LINKS | TDF_POSITION_RELATIVE_TO_WINDOW,
                dwCommonButtons = TDCBF_CANCEL_BUTTON,
                pszWindowTitle = q.Title,
                hMainIcon = LoadIcon(IntPtr.Zero, IDI_QUESTION),
                pszMainInstruction = q.Message,
                pszContent = q.Detail,
                cButtons = (uint)buttons.Length,
                pButtons = array,
                nDefaultButton = KEEP,
                pszVerificationText = q.Remember,
            };
            if (TaskDialogIndirect(ref config, out var pressed, out _, out var remember) != 0) return (null, false);
            return (pressed switch { END => "end", KEEP => "keep", _ => null }, remember && pressed is END or KEEP);
        }
        finally
        {
            for (var i = 0; i < buttons.Length; i++) Marshal.DestroyStructure<TASKDIALOG_BUTTON>(array + i * size);
            Marshal.FreeHGlobal(array);
        }
    }
}
