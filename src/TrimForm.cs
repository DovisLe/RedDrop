using System.Drawing.Drawing2D;

namespace RedDrop;

/// <summary>Pick a start and end with two handles; while dragging, a frame preview + time floats above the handle.</summary>
sealed class TrimForm : Form
{
    readonly TrimTimeline timeline;
    readonly FrameGrabber? grabber; // null for MP3s: no frames to show
    readonly string file;
    ClipPlayer? player;             // sound + (for videos) moving picture; null if ffmpeg is missing
    readonly FlatButton play = new() { Text = "Play clip", Glyph = Glyphs.Play, Secondary = true };
    readonly System.Windows.Forms.Timer playTimer = new() { Interval = 15 };   // only runs while something plays
    readonly System.Windows.Forms.Timer nudgeTimer = new() { Interval = 450 }; // replay the edge after arrow-key nudges
    readonly FlatButton cancel = new() { Text = "Cancel", Secondary = true };
    readonly FlatButton saveClip = new() { Text = "Save clip", Secondary = true };
    readonly FlatButton saveDiscord = new() { Text = "Save for Discord" };
    readonly IconButton close = new(Glyphs.Close);
    readonly MediaInfo info;
    readonly long fileSize;
    readonly Settings settings;
    readonly string title;
    readonly string? note;
    Rectangle infoRect, discordRect, noteRect;

    /// <summary>start, end, forDiscord</summary>
    public event Action<double, double, bool>? SaveRequested;

    public TrimForm(string title, string file, MediaInfo info, long fileSize, Settings settings, double start, double end, string? note)
    {
        this.title = title;
        this.file = file;
        this.info = info;
        this.fileSize = fileSize;
        this.settings = settings;
        this.note = note;
        Text = "Trim";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        KeyPreview = true;
        DoubleBuffered = true;
        Icon = AppIcon.Get(32);

        grabber = info.HasVideo ? new FrameGrabber(file, info.Duration, Math.Max(240, S(150))) : null;
        timeline = new TrimTimeline(grabber, info, start, end);
        timeline.RangeChanged += UpdateEstimates;
        if (grabber != null) grabber.FrameReady += timeline.Invalidate;

        // Play the whole clip, or a 3-second snippet at a handle you just let go of (player is made after layout).
        play.Click += (_, _) => TogglePlay();
        playTimer.Tick += (_, _) => FollowPlayback();
        nudgeTimer.Tick += (_, _) => { nudgeTimer.Stop(); PlayEdge(timeline.EndSelected); };
        timeline.DragStarted += StopPlayback;
        timeline.HandleReleased += PlayEdge;

        cancel.Click += (_, _) => Close();
        close.Click += (_, _) => Close();
        saveClip.Click += (_, _) => Save(forDiscord: false);
        saveDiscord.Click += (_, _) => Save(forDiscord: true);
        Controls.AddRange(new Control[] { timeline, play, cancel, saveClip, saveDiscord, close });
        DoLayout();
        // The picture is decoded at exactly its on-screen size, so it needs the final layout.
        if ((info.HasAudio || info.HasVideo) && Tools.Find("ffmpeg.exe") is { } ffmpeg)
            player = new ClipPlayer(file, ffmpeg, info.HasAudio, info.HasVideo ? timeline.PlayerSize : Size.Empty, info.Fps);
        play.Enabled = player != null;
        UpdateEstimates();
    }

    int S(int value) => LogicalToDeviceUnits(value);

    internal TrimTimeline Timeline => timeline;

    /// <summary>The frame at the clip's start (for the new card's thumbnail), if it was loaded.</summary>
    public Bitmap? TakeStartFrame() => grabber?.CloneExact(timeline.Start);

    /// <summary>For MP3s, draw the song's waveform on the track once the window is up.</summary>
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await LoadWaveformAsync();
    }

    internal async Task LoadWaveformAsync()
    {
        if (info.HasVideo || timeline.HasWaveform) return;
        var size = timeline.TrackSize;
        var wave = await Waveform.RenderAsync(file, size.Width, size.Height);
        if (IsDisposed) wave?.Dispose();
        else timeline.SetWaveform(wave);
    }

    void Save(bool forDiscord)
    {
        if (forDiscord && !saveDiscord.Enabled) return;
        StopPlayback();
        SaveRequested?.Invoke(timeline.Start, timeline.End, forDiscord);
        Close();
    }

    // ---------------------------------------------------------------- sound

    const double SnippetSeconds = 3;
    bool playActive;

    void TogglePlay()
    {
        if (player == null) return;
        if (playActive) StopPlayback();
        else PlayRange(timeline.Start, timeline.End - timeline.Start);
    }

    /// <summary>Let go of a handle: hear (and see) the first 3 s of the clip, or the last 3 s before the cut.</summary>
    void PlayEdge(bool end)
    {
        double length = Math.Min(SnippetSeconds, timeline.End - timeline.Start);
        PlayRange(end ? timeline.End - length : timeline.Start, length);
    }

    void PlayRange(double from, double seconds)
    {
        if (player == null) return;
        StopPlayback();
        playActive = true;
        player.Play(from, seconds); // starts streaming at once: no waiting for a whole decode
        SetPlayButton(playing: true);
        playTimer.Interval = info.HasVideo ? 15 : 33; // video needs each new frame on time; sound only moves the playhead
        playTimer.Start();
    }

    void FollowPlayback()
    {
        if (player == null || !player.IsPlaying) { StopPlayback(); return; }
        bool newFrame = player.Present();
        if (newFrame && timeline.PlayerFrame == null) timeline.PlayerFrame = player.Frame; // first picture is in
        timeline.UpdatePlayback(player.Position, newFrame);
    }

    void StopPlayback()
    {
        playActive = false;
        player?.Stop();
        playTimer.Stop();
        nudgeTimer.Stop();
        timeline.PlayerFrame = null;
        timeline.Playhead = null;
        SetPlayButton(playing: false);
    }

    /// <summary>Preview renders only: play the picture without sound.</summary>
    internal void PlaySilentForPreview(double from, double seconds)
    {
        if (!info.HasVideo || Tools.Find("ffmpeg.exe") is not { } ffmpeg) return;
        player?.Dispose();
        player = new ClipPlayer(file, ffmpeg, hasAudio: false, timeline.PlayerSize, info.Fps);
        PlayRange(from, seconds);
    }

    internal void StopForPreview() => StopPlayback();

    void SetPlayButton(bool playing)
    {
        play.Text = playing ? "Stop" : "Play clip";
        play.Glyph = playing ? Glyphs.Stop : Glyphs.Play;
        play.Invalidate();
    }

    void DoLayout()
    {
        int w = S(600), pad = S(16);
        close.Bounds = new Rectangle(w - S(10) - S(30), S(9), S(30), S(30));
        int y = S(46);
        noteRect = new Rectangle(pad, y, w - 2 * pad, note != null ? S(20) : 0);
        y = noteRect.Bottom + S(4);
        timeline.Bounds = new Rectangle(pad, y, w - 2 * pad, S(timeline.LogicalHeight));
        y = timeline.Bottom + S(6);
        infoRect = new Rectangle(pad, y, w - 2 * pad, S(22));
        discordRect = new Rectangle(pad, infoRect.Bottom, w - 2 * pad, S(20));
        y = discordRect.Bottom + S(14);
        int bh = S(36);
        saveDiscord.Bounds = new Rectangle(w - pad - S(150), y, S(150), bh);
        saveClip.Bounds = new Rectangle(saveDiscord.Left - S(8) - S(108), y, S(108), bh);
        cancel.Bounds = new Rectangle(pad, y, S(92), bh);
        play.Bounds = new Rectangle(cancel.Right + S(8), y, S(120), bh);
        ClientSize = new Size(w, y + bh + pad);
    }

    EncodeJob JobFor(bool forDiscord) => new("", "", timeline.Start, timeline.End - timeline.Start, forDiscord,
        settings.DiscordLimitBytes, info, fileSize);

    void UpdateEstimates()
    {
        double length = timeline.End - timeline.Start;
        double estimate = Encoder.EstimateBytes(JobFor(false));
        string range = $"{Fmt.Clock(timeline.Start, true)}  →  {Fmt.Clock(timeline.End, true)}";
        infoText = $"{range}   ·   {length:0.0} s   ·   about {Fmt.Size(estimate)}";

        var discordJob = JobFor(true);
        bool fits = Encoder.FitsAsIs(discordJob);
        // How it would be squeezed: a resolution for videos, a bitrate for MP3s (null = too long).
        string? squeeze = fits ? null
            : info.HasVideo ? Encoder.DiscordPlan(discordJob) is { } plan ? $"{plan.Height}p" : null
            : Encoder.AudioKbpsFor(discordJob) is int kbps ? $"{kbps} kbps MP3" : null;
        double maxLength = info.HasVideo ? Encoder.MaxSeconds(settings.DiscordLimitBytes) : Encoder.MaxAudioSeconds(settings.DiscordLimitBytes);
        if (fits)
        {
            discordText = $"Fits Discord's {settings.DiscordMb} MB at full quality";
            discordColor = Theme.Success;
        }
        else if (squeeze == null)
        {
            discordText = $"Too long for Discord's {settings.DiscordMb} MB. Keep it under {Fmt.Clock(maxLength)}";
            discordColor = Theme.Error;
        }
        else
        {
            discordText = $"For Discord: {squeeze}, just under {settings.DiscordMb} MB";
            discordColor = Theme.Muted;
        }
        saveDiscord.Enabled = fits || squeeze != null;
        Invalidate(Rectangle.Union(infoRect, discordRect));
    }

    string infoText = "", discordText = "";
    Color discordColor = Theme.Muted;

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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Space: TogglePlay(); return true;
            case Keys.Left: Nudge(-0.1); return true;
            case Keys.Right: Nudge(0.1); return true;
            case Keys.Shift | Keys.Left: Nudge(-1); return true;
            case Keys.Shift | Keys.Right: Nudge(1); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void Nudge(double seconds)
    {
        StopPlayback();
        timeline.Nudge(seconds);
        if (player != null) nudgeTimer.Start(); // replay the edge once the key presses settle
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopPlayback();
        base.OnFormClosed(e);
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
        int pad = S(16), headerH = S(48);
        Theme.DrawGlyph(g, Glyphs.Cut, new Rectangle(pad, 0, S(16), headerH), Theme.Text);
        int x = pad + S(24);
        int titleW = TextRenderer.MeasureText(g, "Trim", Theme.UiBold, Size.Empty, TextFormatFlags.NoPadding).Width;
        Theme.DrawText(g, "Trim", Theme.UiBold, new Rectangle(x, 0, titleW + S(2), headerH), Theme.Text);
        x += titleW + S(10);
        Theme.DrawText(g, title, Theme.UiSmall, new Rectangle(x, 0, close.Left - x - S(8), headerH), Theme.Muted);

        if (note != null) Theme.DrawText(g, note, Theme.UiSmall, noteRect, Color.FromArgb(255, 196, 92));
        Theme.DrawText(g, infoText, Theme.UiBold, infoRect, Theme.Text);
        Theme.DrawText(g, discordText + (player != null ? "      Space: play  ·  ← →: fine-tune" : "      ← →: fine-tune"),
            Theme.UiSmall, discordRect, discordColor);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            grabber?.Dispose();
            player?.Dispose();
            playTimer.Dispose();
            nudgeTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Timeline with start/end handles. The top part is the stage where previews appear: video frames for
/// videos, just the time for MP3s (whose track shows the waveform instead).
/// </summary>
sealed class TrimTimeline : PaintedControl
{
    const double MinLength = 0.3;

    enum Grip { None, Start, End }

    readonly FrameGrabber? grabber;
    readonly MediaInfo info;
    Bitmap? waveform;
    Grip dragging, selected = Grip.Start;

    public TrimTimeline(FrameGrabber? grabber, MediaInfo info, double start, double end)
    {
        this.grabber = grabber;
        this.info = info;
        Start = Math.Clamp(start, 0, info.Duration);
        End = Math.Clamp(end, Start + MinLength, info.Duration);
        Cursor = Cursors.Hand;
    }

    public double Start { get; private set; }
    public double End { get; private set; }
    public event Action? RangeChanged;
    public event Action? DragStarted;
    public event Action<bool>? HandleReleased; // true = the end handle

    public bool EndSelected => selected == Grip.End;

    /// <summary>Where the sound being played is right now (null when nothing plays).</summary>
    public double? Playhead
    {
        get => playhead;
        set { playhead = value; Invalidate(); }
    }

    double? playhead;

    /// <summary>The moving picture while a video plays (null otherwise, or until the first frame is in).</summary>
    public Bitmap? PlayerFrame
    {
        get => playerFrame;
        set { playerFrame = value; Invalidate(); }
    }

    Bitmap? playerFrame;

    /// <summary>Video area while playing: centred in the stage, as large as fits (even size, decoded to match exactly).</summary>
    Rectangle PlayerRect()
    {
        int h = S(StageH) - S(6) - S(22) - S(16);
        int w = (int)(h * info.Aspect);
        if (w > Width - S(12)) { w = Width - S(12); h = (int)(w / info.Aspect); }
        w &= ~1;
        h &= ~1;
        return new Rectangle((Width - w) / 2, S(6), w, h);
    }

    public Size PlayerSize => PlayerRect().Size;

    /// <summary>During playback: repaint only what moved (the playhead line, and the picture when there's a new frame).</summary>
    public void UpdatePlayback(double position, bool newFrame)
    {
        int oldX = playhead is double before ? X(before) : -1;
        string oldLabel = playhead is double b ? Fmt.Clock(b, true) : "";
        playhead = position;
        int newX = X(position);
        var track = Track;
        if (oldX != newX)
        {
            Invalidate(new Rectangle(oldX - S(3), track.Y - S(4), S(6), track.Height + S(8)));
            Invalidate(new Rectangle(newX - S(3), track.Y - S(4), S(6), track.Height + S(8)));
        }
        bool labelChanged = Fmt.Clock(position, true) != oldLabel;
        if (newFrame || labelChanged || (!Frames && oldX != newX))
            Invalidate(new Rectangle(0, 0, Width, track.Y - S(1))); // the stage: picture + time pill
    }

    bool Frames => grabber != null;
    int StageH => Frames ? 160 : 58;
    int TrackH => Frames ? 40 : 64;
    public int LogicalHeight => StageH + TrackH + 30;
    public Size TrackSize => Track.Size;
    public bool HasWaveform => waveform != null;

    public void SetWaveform(Bitmap? image)
    {
        waveform?.Dispose();
        waveform = image;
        Invalidate();
    }

    Rectangle Track => new(S(12), S(StageH), Width - S(24), S(TrackH));
    int X(double t) => Track.X + (int)Math.Round(t / info.Duration * Track.Width);
    double T(int x) => Math.Clamp((x - Track.X) / (double)Track.Width, 0, 1) * info.Duration;

    internal void SimulateDrag(bool end)
    {
        dragging = selected = end ? Grip.End : Grip.Start;
        Invalidate();
    }

    public void Nudge(double seconds)
    {
        MoveGrip(selected, (selected == Grip.Start ? Start : End) + seconds);
    }

    void MoveGrip(Grip handle, double t)
    {
        if (handle == Grip.Start) Start = Math.Clamp(t, 0, End - MinLength);
        else if (handle == Grip.End) End = Math.Clamp(t, Start + MinLength, info.Duration);
        Invalidate();
        RangeChanged?.Invoke();
    }

    Grip HitHandle(int x)
    {
        int tolerance = S(14);
        int ds = Math.Abs(x - X(Start)), de = Math.Abs(x - X(End));
        if (ds > tolerance && de > tolerance) return Grip.None;
        if (ds == de) return x < X(Start) ? Grip.Start : Grip.End; // overlapping: pick by side
        return ds < de ? Grip.Start : Grip.End;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        DragStarted?.Invoke();
        var handle = HitHandle(e.X);
        if (handle == Grip.None)
        {
            // Clicking the track jumps the nearest handle there.
            double t = T(e.X);
            handle = Math.Abs(t - Start) <= Math.Abs(t - End) ? Grip.Start : Grip.End;
            MoveGrip(handle, t);
        }
        dragging = selected = handle;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging != Grip.None) MoveGrip(dragging, T(e.X));
        else Cursor = HitHandle(e.X) != Grip.None ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var released = dragging;
        dragging = Grip.None;
        Invalidate();
        if (released != Grip.None) HandleReleased?.Invoke(released == Grip.End);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        var track = Track;
        int xs = X(Start), xe = X(End);

        // Track: dim outside the selection, highlighted frame around it, chunky handles at both ends.
        Theme.FillRound(g, Theme.Surface, track, S(6));
        if (waveform != null)
        {
            g.DrawImage(waveform, track);
        }
        else
        {
            using var tick = new Pen(Theme.Border);
            for (int i = 1; i < 10; i++)
            {
                int x = track.X + track.Width * i / 10;
                g.DrawLine(tick, x, track.Y + S(12), x, track.Bottom - S(12));
            }
        }
        int hw = S(10);
        var selection = new Rectangle(xs - hw, track.Y, xe - xs + 2 * hw, track.Height);
        using (var fill = new SolidBrush(Color.FromArgb(40, Theme.Accent)))
        using (var path = Theme.RoundRect(selection, S(6)))
            g.FillPath(fill, path);
        using (var pen = new Pen(Theme.Accent, S(2)))
        using (var path = Theme.RoundRect(new RectangleF(selection.X + S(1), selection.Y + S(1), selection.Width - S(2), selection.Height - S(2)), S(6)))
            g.DrawPath(pen, path);
        DrawHandle(g, new Rectangle(xs - hw, track.Y, hw, track.Height), selected == Grip.Start);
        DrawHandle(g, new Rectangle(xe, track.Y, hw, track.Height), selected == Grip.End);
        if (playhead is double now)
        {
            using var line = new Pen(Theme.Text, S(2));
            int px = X(now);
            g.DrawLine(line, px, track.Y - S(3), px, track.Bottom + S(3));
        }

        // Time labels under the track.
        var labels = new Rectangle(track.X, track.Bottom + S(4), track.Width, S(20));
        Theme.DrawText(g, "0:00", Theme.UiSmall, labels, Theme.Muted);
        Theme.DrawText(g, Fmt.Clock(info.Duration), Theme.UiSmall, labels, Theme.Muted, TextFormatFlags.Right);

        // Stage: one bubble following the dragged handle (or the playing sound), otherwise start and end side by side.
        if (playhead is double playing && dragging == Grip.None)
        {
            string label = "▶  " + Fmt.Clock(playing, tenths: true);
            if (Frames && playerFrame != null) DrawPlayer(g, playerFrame, label);
            else if (Frames) DrawFrame(g, playing, X(playing), label, pointer: true, highlight: true); // until the stream's first frame
            else DrawPill(g, X(playing), S(14), label, highlight: true, pointer: true);
        }
        else if (dragging != Grip.None)
        {
            double t = dragging == Grip.Start ? Start : End;
            int x = dragging == Grip.Start ? xs - hw / 2 : xe + hw / 2;
            string label = Fmt.Clock(t, tenths: true);
            if (Frames) DrawFrame(g, t, x, label, pointer: true, highlight: true);
            else DrawPill(g, x, S(14), label, highlight: true, pointer: true);
        }
        else if (Frames)
        {
            int gap = S(10), bw = BubbleSize().Width;
            DrawFrame(g, Start, Width / 2 - gap / 2 - bw / 2, "Start  " + Fmt.Clock(Start, true), pointer: false, highlight: selected == Grip.Start);
            DrawFrame(g, End, Width / 2 + gap / 2 + bw / 2, "End  " + Fmt.Clock(End, true), pointer: false, highlight: selected == Grip.End);
        }
        else
        {
            DrawPill(g, Width / 2 - S(70), S(14), "Start  " + Fmt.Clock(Start, true), selected == Grip.Start, pointer: false);
            DrawPill(g, Width / 2 + S(70), S(14), "End  " + Fmt.Clock(End, true), selected == Grip.End, pointer: false);
        }
    }

    /// <summary>Time label pill centered on <paramref name="centerX"/>, optionally with a line down to the track.</summary>
    Rectangle DrawPill(Graphics g, int centerX, int top, string label, bool highlight, bool pointer)
    {
        int labelW = TextRenderer.MeasureText(g, label, Theme.UiSmallBold, Size.Empty, TextFormatFlags.NoPadding).Width + S(16);
        int lx = Math.Clamp(centerX - labelW / 2, 0, Width - labelW);
        var pill = new Rectangle(lx, top, labelW, S(22));
        Theme.FillRound(g, highlight ? Theme.Accent : Theme.SurfaceHover, pill, pill.Height / 2f);
        Theme.DrawText(g, label, Theme.UiSmallBold, pill, Theme.Text, TextFormatFlags.HorizontalCenter);
        if (pointer)
        {
            int px = Math.Clamp(centerX, S(6), Width - S(6));
            using var pen = new Pen(Theme.Accent, S(2));
            g.DrawLine(pen, px, pill.Bottom, px, Track.Y);
        }
        return pill;
    }

    /// <summary>The playing video, drawn 1:1 (it was decoded at exactly this size, so no scaling per frame).</summary>
    void DrawPlayer(Graphics g, Bitmap frame, string label)
    {
        var rect = PlayerRect();
        using (var path = Theme.RoundRect(rect, S(6)))
        {
            if (frame.Size == rect.Size) Thumbs.FillRounded(g, frame, rect, path);
            else
            {
                using var scaled = Thumbs.Cover(frame, rect.Size, dimWhites: false);
                Thumbs.FillRounded(g, scaled, rect, path);
            }
            using var border = new Pen(Theme.Accent, S(2));
            g.DrawPath(border, path);
        }
        DrawPill(g, rect.X + rect.Width / 2, rect.Bottom + S(6), label, highlight: true, pointer: false);
    }

    void DrawHandle(Graphics g, Rectangle r, bool selectedHandle)
    {
        Theme.FillRound(g, selectedHandle ? Theme.AccentHover : Theme.Accent, r, S(4));
        using var grip = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1, S(1)));
        int cx = r.X + r.Width / 2;
        g.DrawLine(grip, cx - S(2), r.Y + S(13), cx - S(2), r.Bottom - S(13));
        g.DrawLine(grip, cx + S(1), r.Y + S(13), cx + S(1), r.Bottom - S(13));
    }

    Size BubbleSize()
    {
        int h = S(108);
        int w = Math.Clamp((int)(h * info.Aspect), S(70), S(220));
        return new Size(w, h);
    }

    void DrawFrame(Graphics g, double t, int centerX, string label, bool pointer, bool highlight)
    {
        var size = BubbleSize();
        int x = Math.Clamp(centerX - size.Width / 2, 0, Width - size.Width);
        var rect = new Rectangle(x, S(6), size.Width, size.Height);
        using (var path = Theme.RoundRect(rect, S(6)))
        {
            // While dragging or playing, quick keyframes keep up; when still, the exact frame.
            var frame = grabber!.Get(t, exact: dragging == Grip.None && playhead == null);
            if (frame != null)
            {
                using var scaled = Thumbs.Cover(frame, rect.Size, dimWhites: false);
                Thumbs.FillRounded(g, scaled, rect, path);
            }
            else
            {
                using var bg = new SolidBrush(Theme.Thumb);
                g.FillPath(bg, path);
                Theme.DrawText(g, "loading…", Theme.UiSmall, rect, Theme.Muted, TextFormatFlags.HorizontalCenter);
            }
            using var border = new Pen(highlight ? Theme.Accent : Theme.Border, highlight ? S(2) : 1);
            g.DrawPath(border, path);
        }

        // The time pill sits under the frame, the pointer line runs from it down to the handle.
        int pillCenter = rect.X + rect.Width / 2;
        var pill = DrawPill(g, pillCenter, rect.Bottom + S(6), label, highlight, pointer: false);
        if (pointer)
        {
            int px = Math.Clamp(centerX, S(6), Width - S(6));
            using var pen = new Pen(Theme.Accent, S(2));
            g.DrawLine(pen, px, pill.Bottom, px, Track.Y);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) waveform?.Dispose();
        base.Dispose(disposing);
    }
}
