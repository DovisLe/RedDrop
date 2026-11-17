using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RedDrop;

/// <summary>The floating always-on-top panel: paste a link, press Enter, drag the finished video out.</summary>
sealed class WidgetForm : Form
{
    const int HotkeyId = 0x5944;
    const int FormW = 460, Pad = 14, HeaderH = 44, InputH = 40, HintH = 30, ItemGap = 6, MaxItems = 5;
    static readonly Regex WebLink = new(@"^https?://[\w.-]+\.[a-z]{2,}(:\d+)?/\S+$", RegexOptions.IgnoreCase);

    readonly Settings settings;
    readonly ContextMenuStrip menu;
    readonly UrlBox urlBox = new();
    readonly FlatButton downloadButton = new() { Text = "Download", Glyph = Glyphs.Download };
    readonly FormatToggle formatToggle = new();
    readonly IconButton folderButton = new(Glyphs.Folder), menuButton = new(Glyphs.Settings), closeButton = new(Glyphs.Close);
    readonly ToolTip tips = new();
    readonly List<MediaCard> items = new();
    readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 2500 };
    readonly System.Windows.Forms.Timer hotkeyTimer = new();
    readonly System.Windows.Forms.Timer idleTimer = new() { Interval = 4000 };
    readonly Bitmap headerIcon;
    Rectangle hintRect;
    string? flash;
    Color flashColor;
    string? lastSubmitted;
    string? folderStats;
    Point? userLocation;
    Point moveStart;
    Hotkey? wantedHotkey;
    bool hotkeyRegistered;
    int hotkeyRetries;
    TrimForm? trimForm;

    public event Action<MediaCard>? ItemFinished;

    public WidgetForm(Settings settings, ContextMenuStrip menu)
    {
        this.settings = settings;
        this.menu = menu;
        Text = Brand.Name;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        KeyPreview = true;
        AllowDrop = true;
        DoubleBuffered = true;
        Icon = AppIcon.Get(32);
        using (var icon = AppIcon.Get(S(16))) headerIcon = icon.ToBitmap();

        urlBox.Submit += (_, _) => StartDownload();
        downloadButton.Click += (_, _) => StartDownload();
        folderButton.Click += (_, _) => Shell.OpenFolder(settings.Folder);
        menuButton.Click += (_, _) => menu.Show(menuButton, new Point(0, menuButton.Height + S(4)));
        closeButton.Click += (_, _) => HideWidget();
        tips.SetToolTip(folderButton, "Open save folder");
        tips.SetToolTip(menuButton, "Settings");
        tips.SetToolTip(closeButton, "Hide (Esc)");
        tips.SetToolTip(formatToggle, "MP4 = video, MP3 = audio only");
        formatToggle.Audio = settings.Audio;
        formatToggle.Changed += () =>
        {
            settings.Audio = formatToggle.Audio;
            settings.Save();
            urlBox.FocusInput();
        };
        flashTimer.Tick += (_, _) => { flashTimer.Stop(); flash = null; Invalidate(hintRect); };
        hotkeyTimer.Tick += (_, _) => RetryHotkey();
        idleTimer.Tick += (_, _) => GoIdle();

        Controls.AddRange(new Control[] { urlBox, formatToggle, downloadButton, folderButton, menuButton, closeButton });
        DoLayout();
    }

    int S(int value) => LogicalToDeviceUnits(value);

    public int ActiveCount => items.Count(i => i.IsActive);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= Native.CS_DROPSHADOW;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW; // no taskbar button / Alt+Tab entry
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle);
        // A recreated window loses its hotkey; take it again.
        if (wantedHotkey != null)
        {
            hotkeyRegistered = false;
            RegisterWanted("window recreated");
        }
    }

    // ---------------------------------------------------------------- hotkey

    public bool TrySetHotkey(Hotkey hotkey)
    {
        UnregisterHotkey();
        wantedHotkey = hotkey;
        hotkeyRetries = 0;
        if (RegisterWanted("set")) return true;
        hotkeyTimer.Interval = 5000;
        hotkeyTimer.Start(); // only runs while the shortcut is unavailable
        return false;
    }

    public void UnregisterHotkey()
    {
        wantedHotkey = null;
        hotkeyTimer.Stop();
        if (!hotkeyRegistered) return;
        Native.UnregisterHotKey(Handle, HotkeyId);
        hotkeyRegistered = false;
    }

    /// <summary>Drops and re-takes the shortcut (after sleep or unlock).</summary>
    public void RefreshHotkey(string reason)
    {
        if (wantedHotkey == null) return;
        if (hotkeyRegistered) Native.UnregisterHotKey(Handle, HotkeyId);
        hotkeyRegistered = false;
        if (!RegisterWanted(reason) && !hotkeyTimer.Enabled)
        {
            hotkeyTimer.Interval = 5000;
            hotkeyTimer.Start();
        }
    }

    bool RegisterWanted(string reason)
    {
        if (wantedHotkey is not Hotkey hk || !IsHandleCreated) return false;
        hotkeyRegistered = Native.RegisterHotKey(Handle, HotkeyId, hk.Modifiers | Native.MOD_NOREPEAT, (uint)hk.Key);
        int error = hotkeyRegistered ? 0 : Marshal.GetLastWin32Error();
        Log.Write(hotkeyRegistered ? $"Hotkey {hk} registered ({reason})" : $"Hotkey {hk} NOT registered ({reason}), error {error}");
        Invalidate(hintRect);
        return hotkeyRegistered;
    }

    void RetryHotkey()
    {
        if (RegisterWanted("retry") || wantedHotkey == null)
        {
            hotkeyTimer.Stop();
            return;
        }
        if (++hotkeyRetries == 24) hotkeyTimer.Interval = 60000; // after ~2 minutes, slow down
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == HotkeyId)
        {
            Log.Write("Hotkey pressed");
            Toggle();
            return;
        }
        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- show / hide / idle

    public void Toggle()
    {
        if (Visible && ActiveForm == this) HideWidget();
        else ShowWidget();
    }

    public void ShowWidget()
    {
        if (!Visible)
        {
            PositionWindow();
            PrefillFromClipboard();
            UpdateFolderStats();
            Show();
        }
        Activate();
        Native.SetForegroundWindow(Handle);
        urlBox.FocusInput();
    }

    public void HideWidget() => Hide();

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateIdle();
    }

    /// <summary>
    /// When nothing is on screen and nothing is running, drop into Windows efficiency mode and give memory back.
    /// No timers or polling run while idle; only the hotkey (a Windows message) wakes the app.
    /// </summary>
    public void UpdateIdle()
    {
        idleTimer.Stop();
        if (IsIdle) idleTimer.Start();
        else Efficiency.SetIdle(false);
    }

    bool IsIdle => !Visible && trimForm == null && ActiveCount == 0;

    void GoIdle()
    {
        idleTimer.Stop();
        if (!IsIdle) return;
        Efficiency.SetIdle(true);
        Efficiency.TrimMemory();
    }

    void PositionWindow()
    {
        if (userLocation is Point p && Screen.AllScreens.Any(s => s.WorkingArea.Contains(p)))
        {
            Location = p;
            return;
        }
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 7);
    }

    void PrefillFromClipboard()
    {
        if (urlBox.Value.Length > 0) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText().Trim();
            if (text.Length < 500 && text != lastSubmitted && WebLink.IsMatch(text))
            {
                urlBox.Value = text;
                urlBox.SelectAll();
            }
        }
        catch { /* clipboard busy */ }
    }

    /// <summary>"12 videos · 1.4 GB" for the header; counted only when the panel opens or a job finishes.</summary>
    void UpdateFolderStats()
    {
        string folder = settings.Folder;
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(folder)) return "";
                var dir = new DirectoryInfo(folder);
                var videos = dir.GetFiles("*.mp4");
                var songs = dir.GetFiles("*.mp3");
                if (videos.Length + songs.Length == 0) return "";
                var parts = new List<string>();
                if (videos.Length > 0) parts.Add($"{videos.Length} video{(videos.Length == 1 ? "" : "s")}");
                if (songs.Length > 0) parts.Add($"{songs.Length} MP3{(songs.Length == 1 ? "" : "s")}");
                parts.Add(Fmt.Size(videos.Sum(f => f.Length) + songs.Sum(f => f.Length)));
                return string.Join("  ·  ", parts);
            }
            catch { return ""; }
        }).ContinueWith(t =>
        {
            folderStats = t.Result.Length > 0 ? t.Result : null;
            Invalidate(new Rectangle(0, 0, Width, S(HeaderH)));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HideWidget();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // Drag the window by its background.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero);
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        base.OnResizeBegin(e);
        moveStart = Location;
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (Location != moveStart) userLocation = Location;
    }

    // Accept a link dragged in from a browser.
    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = DroppedText(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var text = DroppedText(e.Data);
        if (text == null) return;
        urlBox.Value = text;
        Activate();
        urlBox.FocusInput();
    }

    static string? DroppedText(IDataObject? data)
    {
        if (data == null || data.GetDataPresent(DataFormats.FileDrop)) return null;
        var text = (data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) as string;
        text = text?.Trim();
        return string.IsNullOrEmpty(text) || text.Contains('\n') ? null : text;
    }

    // ---------------------------------------------------------------- downloads, trims, shrinks

    void StartDownload()
    {
        var raw = urlBox.Value.Trim();
        if (raw.Length == 0) { Flash("Paste a link first", Theme.Error); urlBox.FocusInput(); return; }
        var url = NormalizeUrl(raw);
        if (url == null) { Flash("That doesn't look like a video link", Theme.Error); urlBox.FocusInput(); return; }

        lastSubmitted = raw;
        urlBox.Value = "";
        AddItem(MediaCard.ForDownload(settings, url, settings.Audio)).Start();
        urlBox.FocusInput();
    }

    MediaCard AddItem(MediaCard item)
    {
        item.RemoveRequested += it => BeginInvoke(new Action(() => RemoveItem(it)));
        item.HeightChanged += _ => DoLayout();
        item.TrimRequested += OnTrimRequested;
        item.ShrinkRequested += OnShrinkRequested;
        item.Finished += it =>
        {
            ItemFinished?.Invoke(it);
            if (Visible) UpdateFolderStats();
            UpdateIdle();
        };
        items.Insert(0, item);
        Controls.Add(item);
        // Keep the list short: drop the oldest finished cards (files stay on disk).
        for (int i = items.Count - 1; i >= 0 && items.Count > MaxItems; i--)
            if (!items[i].IsActive) RemoveItem(items[i], relayout: false);
        DoLayout();
        UpdateIdle();
        return item;
    }

    void RemoveItem(MediaCard item, bool relayout = true)
    {
        if (!items.Remove(item)) return;
        Controls.Remove(item);
        item.Dispose();
        if (relayout) DoLayout();
        UpdateIdle();
    }

    async void OnShrinkRequested(MediaCard card)
    {
        var info = await card.EnsureInfoAsync();
        if (info == null || card.FilePath == null) { Flash("Couldn't read that video (is ffmpeg installed?)", Theme.Error); return; }
        double max = info.HasVideo ? Encoder.MaxSeconds(settings.DiscordLimitBytes) : Encoder.MaxAudioSeconds(settings.DiscordLimitBytes);
        if (info.Duration > max)
        {
            // Too long to look decent at the limit: let the user pick the part that matters.
            OpenTrim(card, info, 0, max,
                $"{Fmt.Clock(info.Duration)} is too long for {settings.DiscordMb} MB. Pick up to {Fmt.Clock(max)}, then Save for Discord.");
            return;
        }
        StartEncode(card.Title, card.FilePath, card.FileSize, info, 0, info.Duration, forDiscord: true, card.CloneThumb());
    }

    async void OnTrimRequested(MediaCard card)
    {
        var info = await card.EnsureInfoAsync();
        if (info == null || card.FilePath == null) { Flash("Couldn't read that video (is ffmpeg installed?)", Theme.Error); return; }
        OpenTrim(card, info, 0, info.Duration, null);
    }

    void OpenTrim(MediaCard card, MediaInfo info, double start, double end, string? note)
    {
        trimForm?.Close();
        string title = card.Title, file = card.FilePath!;
        long size = card.FileSize;
        var form = new TrimForm(title, file, info, size, settings, start, end, note);
        form.SaveRequested += (s, e, discord) =>
            StartEncode(title, file, size, info, s, e - s, discord, form.TakeStartFrame() ?? card.CloneThumb());
        form.FormClosed += (_, _) =>
        {
            if (trimForm == form) trimForm = null;
            UpdateIdle();
        };
        trimForm = form;
        form.Location = new Point(Left + (Width - form.Width) / 2, Math.Max(Screen.FromControl(this).WorkingArea.Top, Top + S(30)));
        form.Show();
        UpdateIdle();
    }

    void StartEncode(string title, string input, long inputBytes, MediaInfo info, double start, double length, bool forDiscord, Bitmap? thumb)
    {
        var job = new EncodeJob(input, "", start, length, forDiscord, settings.DiscordLimitBytes, info, inputBytes);
        string suffix = job.IsWhole
            ? $"discord {settings.DiscordMb}MB"
            : $"clip {Fmt.Stamp(start)}-{Fmt.Stamp(start + length)}{(forDiscord ? ", discord" : "")}";
        job = job with { Output = UniquePath(input, suffix) };
        string label = job.IsWhole ? "for Discord" : $"clip {Fmt.Clock(start)}–{Fmt.Clock(start + length)}";
        AddItem(MediaCard.ForEncode(settings, $"{title} ({label})", thumb, audio: !info.HasVideo)).StartEncode(job);
    }

    /// <summary>Next to the original, same extension, never overwriting anything.</summary>
    static string UniquePath(string input, string suffix)
    {
        string dir = Path.GetDirectoryName(input)!, name = Path.GetFileNameWithoutExtension(input), ext = Path.GetExtension(input);
        string path = Path.Combine(dir, $"{name} ({suffix}){ext}");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name} ({suffix}) {i}{ext}");
        return path;
    }

    public void KillAll()
    {
        trimForm?.Close();
        foreach (var item in items.ToList()) item.KillNow();
    }

    /// <summary>Repaint cards after the Discord limit setting changes.</summary>
    public void RefreshCards()
    {
        foreach (var item in items) item.Invalidate();
    }

    static string? NormalizeUrl(string s)
    {
        s = s.Trim().Trim('"', '\'', '<', '>');
        if (Regex.IsMatch(s, @"^[\w-]{11}$")) return "https://www.youtube.com/watch?v=" + s; // bare video ID
        if (!s.Contains("://")) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.')
            ? s
            : null;
    }

    void Flash(string text, Color color)
    {
        flash = text;
        flashColor = color;
        flashTimer.Stop();
        flashTimer.Start();
        Invalidate(hintRect);
    }

    string Hint =>
        !hotkeyRegistered ? "Shortcut unavailable. Pick another one in Settings."
        : items.Count == 0 ? $"Enter to download  ·  Esc to hide  ·  {settings.Hotkey} to open"
        : "Drag videos into any app  ·  click to play  ·  right-click for more";

    // ---------------------------------------------------------------- layout + painting

    void DoLayout()
    {
        int w = S(FormW), pad = S(Pad), b = S(30);
        closeButton.Bounds = new Rectangle(w - S(10) - b, (S(HeaderH) - b) / 2 + S(2), b, b);
        menuButton.Bounds = new Rectangle(closeButton.Left - S(2) - b, closeButton.Top, b, b);
        folderButton.Bounds = new Rectangle(menuButton.Left - S(2) - b, closeButton.Top, b, b);

        int y = S(HeaderH), buttonW = S(112), toggleW = S(92);
        downloadButton.Bounds = new Rectangle(w - pad - buttonW, y, buttonW, S(InputH));
        formatToggle.Bounds = new Rectangle(downloadButton.Left - S(8) - toggleW, y, toggleW, S(InputH));
        urlBox.Bounds = new Rectangle(pad, y, formatToggle.Left - S(8) - pad, S(InputH));
        y = urlBox.Bottom;
        hintRect = new Rectangle(pad + S(2), y, w - 2 * pad - S(4), S(HintH));
        y = hintRect.Bottom;
        foreach (var item in items)
        {
            item.Bounds = new Rectangle(pad, y, w - 2 * pad, S(item.PreferredHeight));
            y = item.Bottom + S(ItemGap);
        }
        y += items.Count > 0 ? pad - S(ItemGap) : S(4);
        ClientSize = new Size(w, y);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int pad = S(Pad), headerH = S(HeaderH) + S(4), iconSize = S(16);
        g.DrawImage(headerIcon, new Rectangle(pad, (headerH - iconSize) / 2, iconSize, iconSize));

        int x = pad + iconSize + S(8);
        var titleW = TextRenderer.MeasureText(g, Brand.Name, Theme.UiBold, Size.Empty, TextFormatFlags.NoPadding).Width;
        Theme.DrawText(g, Brand.Name, Theme.UiBold, new Rectangle(x, 0, titleW + S(2), headerH), Theme.Text);
        x += titleW + S(8);
        Theme.DrawText(g, folderStats ?? Brand.Tagline, Theme.UiSmall, new Rectangle(x, 0, folderButton.Left - x - S(6), headerH), Theme.Muted);

        Theme.DrawText(g, flash ?? Hint, Theme.UiSmall, hintRect, flash != null ? flashColor : Theme.Muted);
    }

    // ---------------------------------------------------------------- preview helpers

    internal void AddDemoItems(string? video = null)
    {
        hotkeyRegistered = true; // previews never register the shortcut; show the normal hint
        var thumb = new Bitmap(320, 180);
        using (var g = Graphics.FromImage(thumb))
        using (var brush = new LinearGradientBrush(new Rectangle(0, 0, 320, 180), Color.FromArgb(40, 90, 70), Color.FromArgb(150, 160, 90), 30f))
            g.FillRectangle(brush, 0, 0, 320, 180);
        folderStats = "12 videos  ·  3 MP3s  ·  1.4 GB";
        AddItem(MediaCard.Demo(settings, "Forest airsoft game — full match POV", ItemState.Done, 1, "", new Bitmap(thumb),
            186_400_000, new MediaInfo(754, 1920, 1080, 30, true)));
        AddItem(MediaCard.Demo(settings, "Squad radio chatter mix", ItemState.Done, 1, "", new Bitmap(thumb),
            7_300_000, new MediaInfo(238, 0, 0, 0, true, HasVideo: false), audio: true));
        AddItem(MediaCard.Demo(settings, "LoRa range test (for Discord)", ItemState.Done, 1, "", new Bitmap(thumb),
            18_900_000, new MediaInfo(95, 1280, 720, 30, true)));
        AddItem(MediaCard.Demo(settings, "Night op briefing (clip 1:12–1:45)", ItemState.Running, 0.46, "Shrinking to 720p  ·  46%", new Bitmap(thumb)));
        AddItem(MediaCard.Demo(settings, "Sniper loadout review", ItemState.Running, 0.42, "12.4 MB of 29.5 MB  ·  3.1 MB/s  ·  6 s left", thumb));
    }

    internal Bitmap DemoDragImage() => items.First(i => i.State == ItemState.Done).DemoDragImage();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            headerIcon.Dispose();
            tips.Dispose();
            flashTimer.Dispose();
            hotkeyTimer.Dispose();
            idleTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
