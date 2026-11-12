using System.Runtime.InteropServices;

namespace Corkboard;

internal sealed class TrayIcon : NativeWindow, IDisposable
{
    private const uint Add = 0;
    private const uint Modify = 1;
    private const uint Delete = 2;
    private const uint SetVersion = 4;
    private const uint MessageFlag = 1;
    private const uint IconFlag = 2;
    private const uint TipFlag = 4;
    private const uint ShowTipFlag = 0x80;
    private const int Callback = 0x8001;
    private const int Select = 0x400;
    private const int KeySelect = 0x401;
    private const int ContextMenu = 0x007B;
    private const int LeftButtonUp = 0x0202;
    private const int RightButtonUp = 0x0205;

    private readonly Icon icon;
    private readonly ContextMenuStrip menu;
    private readonly Action open;
    private readonly Action restoreIfHidden;
    private readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private bool registered;
    private bool modernCallbacks;

    public TrayIcon(Icon icon, ContextMenuStrip menu, Action open, Action restoreIfHidden)
    {
        this.icon = icon;
        this.menu = menu;
        this.open = open;
        this.restoreIfHidden = restoreIfHidden;
        CreateHandle(new CreateParams { Caption = "Corkboard tray" });
    }

    public bool EnsureRegistered()
    {
        NotifyData data = CreateData();
        if (registered && ShellNotifyIcon(Modify, ref data)) return true;

        registered = ShellNotifyIcon(Add, ref data);
        if (registered)
        {
            data.Version = 4;
            modernCallbacks = ShellNotifyIcon(SetVersion, ref data);
        }
        return registered;
    }

    protected override void WndProc(ref Message message)
    {
        if (taskbarCreated != 0 && message.Msg == taskbarCreated)
        {
            registered = false;
            if (!EnsureRegistered()) restoreIfHidden();
        }
        else if (message.Msg == Callback)
        {
            int notification = (int)message.LParam & 0xFFFF;
            if (modernCallbacks && notification is Select or KeySelect || !modernCallbacks && notification == LeftButtonUp)
                open();
            else if (modernCallbacks && notification == ContextMenu || !modernCallbacks && notification == RightButtonUp)
            {
                Point anchor = modernCallbacks
                    ? new Point(unchecked((short)message.WParam.ToInt64()), unchecked((short)(message.WParam.ToInt64() >> 16)))
                    : Cursor.Position;
                SetForegroundWindow(Handle);
                menu.Show(anchor, ToolStripDropDownDirection.AboveLeft);
            }
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        NotifyData data = CreateData();
        if (registered) ShellNotifyIcon(Delete, ref data);
        registered = false;
        DestroyHandle();
    }

    private NotifyData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyData>(),
        Window = Handle,
        Id = 1,
        Flags = MessageFlag | IconFlag | TipFlag | ShowTipFlag,
        CallbackMessage = Callback,
        Icon = icon.Handle,
        Tip = "Corkboard",
        Info = "",
        InfoTitle = ""
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint action, ref NotifyData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
