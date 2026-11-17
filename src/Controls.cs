using System.Drawing.Drawing2D;

namespace RedDrop;

/// <summary>Base for the custom-painted, non-focusable controls.</summary>
abstract class PaintedControl : Control
{
    protected bool Hover, Down;

    protected PaintedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
    }

    protected int S(int value) => LogicalToDeviceUnits(value);

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = Down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

sealed class UrlBox : PaintedControl
{
    readonly TextBox box = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = Theme.Surface,
        ForeColor = Theme.Text,
        Font = Theme.Input,
    };
    bool focused;

    public event EventHandler? Submit;

    public UrlBox()
    {
        Cursor = Cursors.IBeam;
        box.GotFocus += (_, _) => { focused = true; Invalidate(); };
        box.LostFocus += (_, _) => { focused = false; Invalidate(); };
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            Submit?.Invoke(this, EventArgs.Empty);
        };
        box.HandleCreated += (_, _) =>
            Native.SendMessage(box.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, Brand.LinkHint);
        Controls.Add(box);
    }

    public string Value
    {
        get => box.Text;
        set => box.Text = value;
    }

    public void SelectAll() => box.SelectAll();
    public void FocusInput() => box.Focus();

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int px = S(12);
        box.SetBounds(px, (Height - box.Height) / 2, Width - 2 * px, box.Height);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        box.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = S(8);
        Theme.FillRound(g, Theme.Surface, new RectangleF(0, 0, Width, Height), radius);
        float w = focused ? S(2) * 0.75f : 1f;
        using var pen = new Pen(focused ? Theme.Accent : Theme.Border, w);
        using var path = Theme.RoundRect(new RectangleF(w / 2, w / 2, Width - w, Height - w), radius);
        g.DrawPath(pen, path);
    }
}

sealed class FlatButton : PaintedControl
{
    public string? Glyph;
    public bool Secondary;

    public FlatButton()
    {
        Font = Theme.UiBold;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg, fg = Theme.Text;
        if (!Enabled) { bg = Theme.Surface; fg = Theme.Muted; }
        else if (Secondary) bg = Down ? Theme.Pressed : Hover ? Theme.SurfaceHover : Theme.Surface;
        else bg = Down ? Theme.AccentDown : Hover ? Theme.AccentHover : Theme.Accent;
        Theme.FillRound(g, bg, new RectangleF(0, 0, Width, Height), S(8));

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
        int glyphW = Glyph == null ? 0 : S(16) + S(7);
        int x = (Width - textSize.Width - glyphW) / 2;
        if (Glyph != null) Theme.DrawGlyph(g, Glyph, new Rectangle(x, 0, S(16), Height), fg);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + glyphW, 0, textSize.Width + S(2), Height), fg, flags);
    }
}

sealed class IconButton : PaintedControl
{
    readonly string glyph;

    public IconButton(string glyph)
    {
        this.glyph = glyph;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Hover) Theme.FillRound(g, Down ? Theme.Pressed : Theme.SurfaceHover, new RectangleF(0, 0, Width, Height), S(6));
        Theme.DrawGlyph(g, glyph, ClientRectangle, Hover ? Theme.Text : Theme.Muted);
    }
}

/// <summary>Two-way switch: MP4 (video) or MP3 (audio only).</summary>
sealed class FormatToggle : PaintedControl
{
    static readonly string[] Labels = { "MP4", "MP3" };
    int selected, hover = -1;

    public event Action? Changed;

    public FormatToggle() { Cursor = Cursors.Hand; }

    public bool Audio
    {
        get => selected == 1;
        set { selected = value ? 1 : 0; Invalidate(); }
    }

    Rectangle Segment(int i)
    {
        int pad = S(3), w = (Width - 2 * pad) / 2;
        return new Rectangle(pad + i * w, pad, w, Height - 2 * pad);
    }

    int IndexAt(Point p) => Segment(0).Contains(p) ? 0 : Segment(1).Contains(p) ? 1 : -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = -1;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = IndexAt(e.Location);
        if (e.Button != MouseButtons.Left || i < 0 || i == selected) return;
        selected = i;
        Invalidate();
        Changed?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(g, Theme.Surface, new RectangleF(0, 0, Width, Height), S(8));
        for (int i = 0; i < Labels.Length; i++)
        {
            var r = Segment(i);
            if (i == selected) Theme.FillRound(g, Theme.Pressed, r, S(6));
            var color = i == selected || i == hover ? Theme.Text : Theme.Muted;
            Theme.DrawText(g, Labels[i], i == selected ? Theme.UiSmallBold : Theme.UiSmall, r, color, TextFormatFlags.HorizontalCenter);
        }
    }
}

/// <summary>Dark context menus to match the widget.</summary>
sealed class DarkRenderer : ToolStripProfessionalRenderer
{
    public DarkRenderer() { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        using var b = new SolidBrush(Theme.Pressed);
        e.Graphics.FillRectangle(b, new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        bool isShortcut = e.Item is ToolStripMenuItem mi && e.Text == mi.ShortcutKeyDisplayString;
        e.TextColor = !e.Item.Enabled || isShortcut ? Theme.Muted : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) =>
        Theme.DrawGlyph(e.Graphics, Glyphs.Check, e.ImageRectangle, Theme.Text, Theme.IconsSmall);

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Text;
        base.OnRenderArrow(e);
    }
}
