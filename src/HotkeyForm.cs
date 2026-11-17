using System.Drawing.Drawing2D;

namespace RedDrop;

/// <summary>Small dialog that records a new global shortcut.</summary>
sealed class HotkeyForm : Form
{
    readonly FlatButton save = new() { Text = "Save" };
    readonly FlatButton cancel = new() { Text = "Cancel", Secondary = true };
    Hotkey current;
    bool complete = true;
    string? error;

    public HotkeyForm(Hotkey existing)
    {
        current = existing;
        Text = "Set shortcut";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        KeyPreview = true;
        DoubleBuffered = true;
        ClientSize = new Size(S(360), S(196));

        int bw = S(96), bh = S(34), m = S(16);
        save.Bounds = new Rectangle(ClientSize.Width - m - bw, ClientSize.Height - m - bh, bw, bh);
        cancel.Bounds = new Rectangle(save.Left - S(8) - bw, save.Top, bw, bh);
        save.Click += (_, _) => TrySave();
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(save);
        Controls.Add(cancel);
    }

    public Hotkey Result => current;

    int S(int value) => LogicalToDeviceUnits(value);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= Native.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle);
    }

    void TrySave()
    {
        if (complete && current.IsValid) DialogResult = DialogResult.OK;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        bool ctrl = keyData.HasFlag(Keys.Control), alt = keyData.HasFlag(Keys.Alt), shift = keyData.HasFlag(Keys.Shift);
        bool win = Native.IsKeyDown(Keys.LWin) || Native.IsKeyDown(Keys.RWin);
        bool plain = !ctrl && !alt && !shift && !win;

        if (plain && key == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        if (plain && key == Keys.Enter) { TrySave(); return true; }

        bool modifierOnly = key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
            or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu;
        current = new Hotkey(ctrl, alt, shift, win, modifierOnly ? Keys.None : key);
        complete = !modifierOnly && current.IsValid;
        error = !modifierOnly && !current.IsValid ? "Add Ctrl, Alt, Shift or Win to the key" : null;
        save.Enabled = complete;
        Invalidate();
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int m = S(18), w = ClientSize.Width;
        Theme.DrawText(g, "Set a shortcut", Theme.UiBold, new Rectangle(m, S(14), w - 2 * m, S(24)), Theme.Text);
        Theme.DrawText(g, "Press the key combination you want to use", Theme.UiSmall, new Rectangle(m, S(36), w - 2 * m, S(20)), Theme.Muted);

        var parts = current.Parts();
        if (!complete) parts.Add("…");

        // Keycaps, centered.
        int capH = S(36), gap = S(22), y = S(68);
        var widths = parts.Select(p => Math.Max(S(40), TextRenderer.MeasureText(g, p, Theme.UiBold).Width + S(18))).ToList();
        int total = widths.Sum() + gap * (parts.Count - 1);
        int x = (w - total) / 2;
        for (int i = 0; i < parts.Count; i++)
        {
            var cap = new Rectangle(x, y, widths[i], capH);
            Theme.FillRound(g, Theme.Border, cap, S(7));
            Theme.FillRound(g, Theme.Surface, new RectangleF(cap.X + 1, cap.Y + 1, cap.Width - 2, cap.Height - S(3)), S(6));
            Theme.DrawText(g, parts[i], Theme.UiBold, new Rectangle(cap.X, cap.Y, cap.Width, cap.Height - S(2)),
                parts[i] == "…" ? Theme.Muted : Theme.Text, TextFormatFlags.HorizontalCenter);
            x = cap.Right;
            if (i < parts.Count - 1)
            {
                Theme.DrawText(g, "+", Theme.Ui, new Rectangle(x, y, gap, capH), Theme.Muted, TextFormatFlags.HorizontalCenter);
                x += gap;
            }
        }

        if (error != null)
            Theme.DrawText(g, error, Theme.UiSmall, new Rectangle(m, S(112), w - 2 * m, S(20)), Theme.Error, TextFormatFlags.HorizontalCenter);
    }
}
