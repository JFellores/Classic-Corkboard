using System.Runtime.InteropServices;

namespace Corkboard;

internal class ResizableWindow : Form
{
    protected virtual int FrameDragWidth => 0;

    public ResizableWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.Style |= 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000;
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0083)
        {
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
        if (message.Msg != 0x0084 || message.Result != new IntPtr(1)) return;

        long packed = message.LParam.ToInt64();
        Point point = PointToClient(new Point(unchecked((short)packed), unchecked((short)(packed >> 16))));
        int edge = LogicalToDeviceUnits(7);
        if (WindowState == FormWindowState.Normal)
        {
            bool left = point.X < edge;
            bool right = point.X >= ClientSize.Width - edge;
            bool top = point.Y < edge;
            bool bottom = point.Y >= ClientSize.Height - edge;
            int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 1;
            if (hit != 1)
            {
                message.Result = new IntPtr(hit);
                return;
            }
        }

        int frame = LogicalToDeviceUnits(FrameDragWidth);
        if (point.X < frame || point.Y < frame || point.X >= ClientSize.Width - frame || point.Y >= ClientSize.Height - frame)
            message.Result = new IntPtr(2);
    }

    protected void BeginWindowDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    public static Rectangle FitOnScreen(Rectangle wanted, Size minimum, Screen fallback)
    {
        Rectangle area = Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(wanted))
            ? Screen.FromRectangle(wanted).WorkingArea : fallback.WorkingArea;
        int width = Math.Clamp(wanted.Width, Math.Min(minimum.Width, area.Width), area.Width);
        int height = Math.Clamp(wanted.Height, Math.Min(minimum.Height, area.Height), area.Height);
        return new Rectangle(Math.Clamp(wanted.X, area.Left, area.Right - width), Math.Clamp(wanted.Y, area.Top, area.Bottom - height), width, height);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

internal static class NamePrompt
{
    public static string? Show(IWin32Window owner, string caption, string label, string value, int maxLength)
    {
        using var form = new Form
        {
            Text = caption, ClientSize = new Size(340, 125), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, ShowInTaskbar = false, MaximizeBox = false,
            MinimizeBox = false, AutoScaleMode = AutoScaleMode.Dpi, Font = new Font("Segoe UI", 10F), BackColor = BoardStyle.Paper
        };
        var prompt = new Label { Text = label, Location = new Point(14, 12), AutoSize = true };
        var entry = new TextBox { Text = value, MaxLength = maxLength, Bounds = new Rectangle(14, 37, 310, 25), AccessibleName = label };
        Button save = BoardStyle.Button("Save", BoardStyle.Brick, Color.White);
        save.Bounds = new Rectangle(244, 78, 80, 32);
        save.DialogResult = DialogResult.OK;
        Button cancel = BoardStyle.Button("Cancel", BoardStyle.PanelTint, BoardStyle.Ink);
        cancel.Bounds = new Rectangle(154, 78, 80, 32);
        cancel.DialogResult = DialogResult.Cancel;
        form.Controls.AddRange([prompt, entry, save, cancel]);
        form.AcceptButton = save;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { entry.Focus(); entry.SelectAll(); };
        return form.ShowDialog(owner) == DialogResult.OK ? entry.Text.Trim() : null;
    }
}
