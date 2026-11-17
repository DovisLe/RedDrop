using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;
using System.Text.RegularExpressions;

namespace RedDrop;

enum ItemState { Starting, Running, Finishing, Done, Failed }

/// <summary>
/// One video in the list. It either downloads with yt-dlp or encodes with ffmpeg (shrink / clip);
/// once finished it shows the size, can be dragged into any app, trimmed or shrunk for Discord.
/// </summary>
sealed class MediaCard : Control
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly Encoding Utf8 = new UTF8Encoding(false);

    enum Zone { None, Body, Folder, Close, Trim, Shrink }

    readonly Settings settings;
    readonly SynchronizationContext ui;
    readonly System.Windows.Forms.Timer anim = new() { Interval = 30 };
    readonly List<string> log = new();
    readonly string? url;
    readonly bool audio;

    Process? proc;
    CancellationTokenSource? encodeCts;
    EncodeJob? job;
    bool canceled;
    string? lastError, extractor;
    long totalApprox, doneBytes, curBytes, curTotal;
    double percent = -1, speed;
    string status = "";
    float animPhase;
    long lastPaint;
    int lastHeight;
    Bitmap? thumb, thumbCache;

    bool hovering, mouseDown;
    Zone hoverZone, downZone;
    Point downPos;

    MediaCard(Settings settings, string title, string? url, Bitmap? thumb, bool audio)
    {
        this.settings = settings;
        this.url = url;
        this.thumb = thumb;
        this.audio = audio;
        Title = title;
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Bg;
        lastHeight = PreferredHeight;
        anim.Tick += (_, _) =>
        {
            animPhase = (animPhase + 0.012f) % 1f;
            Invalidate(BarRect());
        };
    }

    public static MediaCard ForDownload(Settings settings, string url, bool audio) =>
        new(settings, Regex.Replace(url, @"^https?://(www\.)?", ""), url, null, audio);

    public static MediaCard ForEncode(Settings settings, string title, Bitmap? thumb, bool audio) =>
        new(settings, title, null, thumb, audio);

    string Extension => audio ? "mp3" : "mp4";

    public string Title { get; private set; }
    public string? FilePath { get; private set; }
    public long FileSize { get; private set; }
    public MediaInfo? Info { get; private set; }
    public ItemState State { get; private set; }
    public bool IsActive => proc != null || encodeCts != null;
    public string? Error => State == ItemState.Failed ? status : null;
    public bool FitsDiscord => FileSize > 0 && FileSize <= settings.DiscordLimitBytes;
    /// <summary>Logical height: finished cards get an extra row for their buttons.</summary>
    public int PreferredHeight => State == ItemState.Done ? 82 : 66;

    public event Action<MediaCard>? Finished;
    public event Action<MediaCard>? RemoveRequested;
    public event Action<MediaCard>? HeightChanged;
    public event Action<MediaCard>? TrimRequested;
    public event Action<MediaCard>? ShrinkRequested;

    public Bitmap? CloneThumb() => thumb == null ? null : new Bitmap(thumb);

    public async Task<MediaInfo?> EnsureInfoAsync()
    {
        if (Info == null && FilePath != null) Info = await MediaInfo.ProbeAsync(FilePath);
        return Info;
    }

    // ================================================================ download (yt-dlp)

    static IEnumerable<string> DownloadArguments(string url, string folder, bool audio) => new[]
    {
        "--no-playlist", "--newline", "--progress", "--no-simulate", "--no-mtime", "--encoding", "utf-8",
    }.Concat(audio
        // Best audio → MP3 (VBR ~245 kbps), with the thumbnail as cover art and title/channel tags.
        ? new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0", "--embed-thumbnail", "--embed-metadata" }
        // H.264 + AAC in an .mp4 plays everywhere (Windows player, phones, editors, Discord previews).
        : new[] { "-S", "vcodec:h264,res,acodec:aac", "--merge-output-format", "mp4", "--remux-video", "mp4" }
    ).Concat(new[]
    {
        "-P", folder, "-o", "%(title).150B [%(id)s].%(ext)s",
        "--progress-template", "download:[dl]%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s",
        "--progress-template", "postprocess:[pp]%(progress.status)s|%(progress.postprocessor)s",
        "--print", "video:[meta]%(id)s|%(extractor_key)s|%(filesize,filesize_approx)s|%(title)s",
        "--print", "after_move:[file]%(filepath)s",
        "--", url,
    });

    string? videoId;

    public void Start()
    {
        if (url == null) { if (job != null) StartEncode(job); return; }
        ResetProgress();
        SetState(ItemState.Starting, videoId == null ? "Getting video info…" : "Starting…");

        var exe = Tools.Find("yt-dlp.exe");
        if (exe == null) { Fail("yt-dlp not found. Install it with: winget install yt-dlp.yt-dlp"); return; }
        try { Directory.CreateDirectory(settings.Folder); }
        catch (Exception ex) { Fail("Can't use the save folder: " + ex.Message); return; }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = settings.Folder,
        };
        foreach (var arg in DownloadArguments(url, settings.Folder, audio)) psi.ArgumentList.Add(arg);
        psi.Environment["PATH"] = Tools.SearchPath(); // so yt-dlp finds ffmpeg + deno
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (sender, e) => { if (e.Data != null) ui.Post(state => HandleLine(e.Data), null); };
        p.ErrorDataReceived += (sender, e) => { if (e.Data != null) ui.Post(state => HandleLine(e.Data), null); };
        try { p.Start(); }
        catch (Exception ex)
        {
            p.Dispose();
            Fail("Couldn't start yt-dlp: " + ex.Message);
            return;
        }
        proc = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _ = WatchDownloadAsync(p);
    }

    void ResetProgress()
    {
        canceled = false;
        lastError = null;
        FilePath = null;
        totalApprox = doneBytes = curBytes = curTotal = 0;
        percent = -1;
        speed = 0;
        log.Clear();
    }

    async Task WatchDownloadAsync(Process p)
    {
        try { await p.WaitForExitAsync(); } catch { }
        int code = -1;
        try { code = p.ExitCode; } catch { }
        p.Dispose();
        if (canceled)
        {
            string folder = settings.Folder; string? id = videoId;
            _ = Task.Run(() => CleanupPartials(folder, id));
            return;
        }
        proc = null;
        if (IsDisposed) return;
        if (code == 0 && (FilePath == null || !File.Exists(FilePath))) FilePath = FindDownloadedFile();
        if (code == 0 && FilePath != null && File.Exists(FilePath)) CompleteDone();
        else Fail(lastError ?? (code == 0 ? "Finished, but the file wasn't found" : $"yt-dlp stopped (exit code {code})"));
    }

    void HandleLine(string line)
    {
        if (IsDisposed || canceled) return;
        AddLog(line);

        if (line.StartsWith("[dl]"))
        {
            OnDownloadProgress(line[4..].Split('|'));
        }
        else if (line.StartsWith("[pp]"))
        {
            var p = line[4..].Split('|');
            if (p.Length == 2 && p[0] == "started") SetState(ItemState.Finishing, StepLabel(p[1], audio));
        }
        else if (line.StartsWith("[meta]"))
        {
            var p = line[6..].Split('|', 4);
            if (p.Length < 4) return;
            videoId = p[0];
            extractor = p[1];
            totalApprox = (long)Fmt.Num(p[2]);
            Title = p[3];
            if (State == ItemState.Starting) status = totalApprox > 0 ? $"Starting download  ·  about {Fmt.Size(totalApprox)}" : "Starting download…";
            _ = LoadThumbnailAsync();
            Invalidate();
        }
        else if (line.StartsWith("[file]"))
        {
            FilePath = line[6..].Trim();
        }
        else if (line.StartsWith("ERROR:"))
        {
            lastError = Regex.Replace(line[6..].Trim(), @"^\[[^\]]+\]\s*[\w-]+:\s*", "");
        }
    }

    void OnDownloadProgress(string[] p)
    {
        if (p.Length < 5) return;
        double downloaded = Fmt.Num(p[1]), total = Fmt.Num(p[2]);
        if (total <= 0) total = Fmt.Num(p[3]);

        if (p[0] == "finished")
        {
            // Video and audio arrive as separate streams; keep a running total for one smooth bar.
            doneBytes += (long)Math.Max(total, downloaded);
            curBytes = curTotal = 0;
        }
        else if (p[0] == "downloading")
        {
            curBytes = (long)downloaded;
            curTotal = (long)total;
            speed = Fmt.Num(p[4]);
        }
        else return;

        long have = doneBytes + curBytes;
        long expected = totalApprox > 0 ? Math.Max(totalApprox, have) : curTotal > 0 ? doneBytes + curTotal : 0;
        percent = expected > 0 ? Math.Min(0.995, have / (double)expected) : -1;

        var sb = new StringBuilder(expected > 0 ? $"{Fmt.Size(have)} of {Fmt.Size(expected)}" : Fmt.Size(have));
        if (p[0] == "downloading" && speed > 0)
        {
            sb.Append($"  ·  {Fmt.Size(speed)}/s");
            if (expected > have) sb.Append($"  ·  {Fmt.Eta((expected - have) / speed)}");
        }
        ShowProgress(sb.ToString(), p[0] == "finished");
    }

    static string StepLabel(string postprocessor, bool audio) => postprocessor switch
    {
        var s when s.Contains("Thumbnails") => "Preparing cover art…",
        var s when s.Contains("EmbedThumbnail") => "Adding cover art…",
        var s when s.Contains("ExtractAudio") => "Converting to MP3…",
        var s when s.Contains("Metadata") => audio ? "Adding song info…" : "Adding info…",
        var s when s.Contains("Merger") => "Merging video and audio…",
        var s when s.Contains("Remux") || s.Contains("Convertor") => "Converting to MP4…",
        var s when s.Contains("Fixup") => "Fixing up the file…",
        "MoveFiles" => "Saving…",
        _ => "Finishing…",
    };

    string? FindDownloadedFile()
    {
        if (videoId == null || !Directory.Exists(settings.Folder)) return null;
        return new DirectoryInfo(settings.Folder).GetFiles($"*[{videoId}].{Extension}")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    /// <summary>Removes the .part / intermediate files a cancelled download leaves behind.</summary>
    static void CleanupPartials(string folder, string? id)
    {
        if (id == null) return;
        Thread.Sleep(400);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, $"*[{id}]*"))
            {
                var name = Path.GetFileName(file);
                bool partial = name.EndsWith(".part") || name.EndsWith(".ytdl") || name.Contains(".part-Frag") ||
                               name.Contains(".temp.") || Regex.IsMatch(name, @"\]\.(f[\w-]+\.\w+|webp|jpg|png)$");
                if (partial) try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    async Task LoadThumbnailAsync()
    {
        if (thumb != null || videoId == null || extractor != "Youtube") return;
        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg");
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            if (IsDisposed) return;
            thumb = new Bitmap(img);
            thumbCache?.Dispose();
            thumbCache = null;
            Invalidate();
        }
        catch { /* thumbnail is cosmetic */ }
    }

    // ================================================================ encode (ffmpeg)

    public async void StartEncode(EncodeJob encodeJob)
    {
        job = encodeJob;
        ResetProgress();
        SetState(ItemState.Starting, "Preparing…");
        var cts = encodeCts = new CancellationTokenSource();
        try
        {
            await Encoder.RunAsync(encodeJob, (p, label) => ui.Post(state => OnEncodeProgress(cts, p, label), null), cts.Token);
            encodeCts = null;
            if (IsDisposed) return;
            FilePath = encodeJob.Output;
            CompleteDone();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            encodeCts = null;
            AddLog(ex.ToString());
            if (!IsDisposed && !cts.IsCancellationRequested) Fail(ex.Message);
        }
        finally
        {
            if (encodeCts == cts) encodeCts = null;
            cts.Dispose();
        }
    }

    void OnEncodeProgress(CancellationTokenSource cts, double p, string label)
    {
        if (IsDisposed || cts.IsCancellationRequested) return;
        percent = Math.Min(0.995, p);
        ShowProgress($"{label}  ·  {(int)(p * 100)}%", false);
    }

    // ================================================================ shared state

    void ShowProgress(string text, bool force)
    {
        State = ItemState.Running;
        status = text;
        UpdateAnimation();
        long now = Environment.TickCount64;
        if (force || now - lastPaint > 80)
        {
            lastPaint = now;
            Invalidate();
        }
    }

    void CompleteDone()
    {
        FileSize = new FileInfo(FilePath!).Length;
        percent = 1;
        SetState(ItemState.Done, "");
        Finished?.Invoke(this);
        _ = LoadInfoAsync();
    }

    async Task LoadInfoAsync()
    {
        await EnsureInfoAsync();
        if (!IsDisposed) Invalidate();
    }

    void Fail(string message)
    {
        SetState(ItemState.Failed, message);
        Finished?.Invoke(this);
    }

    void SetState(ItemState state, string text)
    {
        State = state;
        status = text;
        UpdateAnimation();
        Invalidate();
        if (PreferredHeight != lastHeight)
        {
            lastHeight = PreferredHeight;
            HeightChanged?.Invoke(this);
        }
    }

    void UpdateAnimation() =>
        anim.Enabled = State is ItemState.Starting or ItemState.Finishing || (State == ItemState.Running && percent < 0);

    void AddLog(string line)
    {
        if (log.Count >= 300) log.RemoveAt(0);
        log.Add(line);
    }

    public void Cancel()
    {
        canceled = true;
        var p = proc;
        if (p != null)
        {
            proc = null;
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        encodeCts?.Cancel();
        RemoveRequested?.Invoke(this);
    }

    /// <summary>Synchronous stop used when the app exits.</summary>
    public void KillNow()
    {
        canceled = true;
        encodeCts?.Cancel();
        var p = proc;
        if (p == null) return;
        proc = null;
        try
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
        }
        catch { }
        CleanupPartials(settings.Folder, videoId);
    }

    // ================================================================ actions

    void OpenFile() { if (EnsureFile()) Shell.Open(FilePath!); }
    void ShowInFolder() { if (EnsureFile()) Shell.ShowInFolder(FilePath!); }
    void CopyFile() { if (EnsureFile()) Clipboard.SetFileDropList(new StringCollection { FilePath! }); }

    void CopyLog()
    {
        var text = log.Count > 0 ? string.Join(Environment.NewLine, log) : status;
        if (text.Length > 0) Clipboard.SetText(text);
    }

    bool EnsureFile()
    {
        if (FilePath != null && File.Exists(FilePath)) return true;
        SetState(ItemState.Failed, "The file was moved or deleted");
        return false;
    }

    void BeginDrag()
    {
        if (!EnsureFile()) return;
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { FilePath! });
        using var image = BuildDragImage(out var offset);
        // Copy (not move) so the file stays in the download folder, like Chrome's download bar.
        DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Link, image, offset, false);
    }

    void ShowMenu(Point at)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Ui, ShowImageMargin = false };
        void Add(string text, Action action) =>
            menu.Items.Add(text, null, (sender, e) => ui.Post(state => action(), null));

        switch (State)
        {
            case ItemState.Done:
                Add("Open", OpenFile);
                Add("Show in folder", ShowInFolder);
                Add("Copy file", CopyFile);
                Add("Trim…", () => TrimRequested?.Invoke(this));
                if (!FitsDiscord) Add($"Shrink to {settings.DiscordMb} MB for Discord", () => ShrinkRequested?.Invoke(this));
                break;
            case ItemState.Failed:
                Add("Retry", Start);
                Add("Copy error details", CopyLog);
                break;
            default:
                Add("Cancel", Cancel);
                break;
        }
        if (url != null) Add("Copy video link", () => Clipboard.SetText(url));
        if (!IsActive)
        {
            menu.Items.Add(new ToolStripSeparator());
            Add("Remove from list", () => RemoveRequested?.Invoke(this));
        }
        menu.Closed += (sender, e) => ui.Post(state => menu.Dispose(), null);
        menu.Show(this, at);
    }

    // ================================================================ layout + painting

    int S(int value) => LogicalToDeviceUnits(value);
    bool Running => State is ItemState.Starting or ItemState.Running or ItemState.Finishing;
    bool IsDone => State == ItemState.Done;
    Rectangle ThumbRect() => IsDone ? new(S(10), S(13), S(100), S(56)) : new(S(10), S(10), S(82), S(46));
    int TextX => ThumbRect().Right + S(12);
    int Reserve => Running ? S(44) : S(74);
    Rectangle CloseRect() => new(Width - S(8) - S(28), IsDone ? S(6) : (Height - S(28)) / 2, S(28), S(28));
    Rectangle FolderRect() { var c = CloseRect(); return new(c.Left - S(2) - S(28), c.Top, S(28), S(28)); }
    Rectangle BarRect() => new(TextX, S(52), Math.Max(0, Width - TextX - Reserve), S(4));
    bool ShowsClose => Running || hovering;
    bool ShowsFolder => IsDone && hovering;

    string DiscordLabel => FitsDiscord ? "Fits Discord" : $"Shrink to {settings.DiscordMb} MB";
    Rectangle TrimPill() => Pill(TextX, "Trim");
    Rectangle DiscordPill() => Pill(TrimPill().Right + S(6), DiscordLabel);

    Rectangle Pill(int x, string text)
    {
        int textW = TextRenderer.MeasureText(text, Theme.UiSmall, Size.Empty, TextFormatFlags.NoPadding).Width;
        return new Rectangle(x, S(50), S(10) + S(14) + S(5) + textW + S(11), S(24));
    }

    Zone HitTest(Point p)
    {
        if (!ClientRectangle.Contains(p)) return Zone.None;
        if (ShowsClose && CloseRect().Contains(p)) return Zone.Close;
        if (ShowsFolder && FolderRect().Contains(p)) return Zone.Folder;
        if (IsDone && TrimPill().Contains(p)) return Zone.Trim;
        if (IsDone && !FitsDiscord && DiscordPill().Contains(p)) return Zone.Shrink;
        return Zone.Body;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool clickable = State is ItemState.Done or ItemState.Failed;
        Theme.FillRound(g, hovering && clickable && hoverZone == Zone.Body ? Theme.SurfaceHover : Theme.Surface,
            new RectangleF(0, 0, Width, Height), S(8));
        DrawThumb(g, ThumbRect(), forDrag: false);

        int right = Width - Reserve;
        Theme.DrawText(g, Title, Theme.UiBold, new Rectangle(TextX, S(8), right - TextX, S(22)), Theme.Text);

        if (IsDone) PaintDoneDetails(g, right);
        else PaintStatus(g, right);

        if (ShowsFolder) DrawButton(g, FolderRect(), Glyphs.Folder, hoverZone == Zone.Folder);
        if (ShowsClose) DrawButton(g, CloseRect(), Glyphs.Close, hoverZone == Zone.Close);
    }

    void PaintStatus(Graphics g, int right)
    {
        var rect = new Rectangle(TextX, S(30), right - TextX, S(18));
        string text = status;
        Color color = Theme.Muted;
        if (State == ItemState.Failed)
        {
            Theme.DrawGlyph(g, Glyphs.Error, new Rectangle(rect.X, rect.Y, S(14), rect.Height), Theme.Error, Theme.IconsSmall);
            rect = new Rectangle(rect.X + S(19), rect.Y, rect.Width - S(19), rect.Height);
            if (hovering) text = "Click to retry  ·  right-click for details";
            else color = Theme.Error;
        }
        Theme.DrawText(g, text, Theme.UiSmall, rect, color);
        if (Running) DrawBar(g);
    }

    /// <summary>Size first (that's what matters for sharing), then length and resolution, then the action pills.</summary>
    void PaintDoneDetails(Graphics g, int right)
    {
        int x = TextX, y = S(29), h = S(18);
        Theme.DrawGlyph(g, Glyphs.Check, new Rectangle(x, y, S(14), h), Theme.Success, Theme.IconsSmall);
        x += S(19);
        string size = Fmt.Size(FileSize);
        int sizeW = TextRenderer.MeasureText(g, size, Theme.UiSmallBold, Size.Empty, TextFormatFlags.NoPadding).Width;
        Theme.DrawText(g, size, Theme.UiSmallBold, new Rectangle(x, y, sizeW + S(2), h), Theme.Text);
        x += sizeW + S(2);
        var details = new List<string>();
        if (Info != null)
        {
            details.Add(Fmt.Clock(Info.Duration));
            if (Info.HasVideo && Info.Height > 0) details.Add($"{Info.Height}p");
        }
        if (audio) details.Add("MP3");
        if (details.Count > 0)
            Theme.DrawText(g, "  ·  " + string.Join("  ·  ", details), Theme.UiSmall, new Rectangle(x, y, right - x, h), Theme.Muted);

        DrawPill(g, TrimPill(), Glyphs.Cut, "Trim", Theme.Text, hoverZone == Zone.Trim, filled: true);
        if (FitsDiscord) DrawPill(g, DiscordPill(), Glyphs.Check, DiscordLabel, Theme.Success, false, filled: false);
        else DrawPill(g, DiscordPill(), Glyphs.Shrink, DiscordLabel, Theme.Text, hoverZone == Zone.Shrink, filled: true);
    }

    void DrawPill(Graphics g, Rectangle r, string glyph, string text, Color color, bool hot, bool filled)
    {
        if (filled) Theme.FillRound(g, hot ? Theme.Pressed : Theme.SurfaceHover, r, r.Height / 2f);
        Theme.DrawGlyph(g, glyph, new Rectangle(r.X + S(10), r.Y, S(14), r.Height), color, Theme.IconsSmall);
        int tx = r.X + S(10) + S(14) + S(5);
        Theme.DrawText(g, text, Theme.UiSmall, new Rectangle(tx, r.Y, r.Right - tx, r.Height), color);
    }

    void DrawBar(Graphics g)
    {
        var r = BarRect();
        if (r.Width <= 0) return;
        float radius = r.Height / 2f;
        Theme.FillRound(g, Theme.Border, r, radius);
        if (State == ItemState.Running && percent >= 0)
        {
            Theme.FillRound(g, Theme.Accent, new RectangleF(r.X, r.Y, Math.Max(r.Height, (float)(r.Width * percent)), r.Height), radius);
            return;
        }
        // Indeterminate: a segment sliding across the track.
        float seg = r.Width * 0.3f;
        float x = r.X - seg + animPhase * (r.Width + seg);
        using var clip = Theme.RoundRect(r, radius);
        var saved = g.Save();
        g.SetClip(clip);
        Theme.FillRound(g, Theme.Accent, new RectangleF(x, r.Y, seg, r.Height), radius);
        g.Restore(saved);
    }

    void DrawButton(Graphics g, Rectangle r, string glyph, bool hot)
    {
        if (hot) Theme.FillRound(g, Theme.Pressed, r, S(6));
        Theme.DrawGlyph(g, glyph, r, hot ? Theme.Text : Theme.Muted);
    }

    void DrawThumb(Graphics g, Rectangle r, bool forDrag)
    {
        DrawThumbImage(g, r, forDrag);
        if (!audio) return;
        // Small "MP3" tag in the corner so songs are easy to tell apart from videos.
        using var badgeFont = new Font(Theme.UiSmallBold.FontFamily, 7f, FontStyle.Bold);
        var badge = new Rectangle(r.Right - S(32), r.Bottom - S(17), S(29), S(14));
        Theme.FillRound(g, Color.FromArgb(220, 20, 20, 22), badge, S(4));
        using var white = new SolidBrush(Theme.Text);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("MP3", badgeFont, white, badge, center);
    }

    void DrawThumbImage(Graphics g, Rectangle r, bool forDrag)
    {
        using var path = Theme.RoundRect(r, S(5));
        if (thumb == null)
        {
            using var bg = new SolidBrush(Theme.Thumb);
            g.FillPath(bg, path);
            using var fg = new SolidBrush(Theme.Muted);
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Glyphs.Play, Theme.Icons, fg, r, center);
            return;
        }
        Bitmap img;
        if (forDrag) img = Thumbs.Cover(thumb, r.Size, dimWhites: true);
        else
        {
            if (thumbCache == null || thumbCache.Size != r.Size)
            {
                thumbCache?.Dispose();
                thumbCache = Thumbs.Cover(thumb, r.Size, dimWhites: false);
            }
            img = thumbCache;
        }
        try { Thumbs.FillRounded(g, img, r, path); }
        finally { if (forDrag) img.Dispose(); }
    }

    Bitmap BuildDragImage(out Point cursorOffset)
    {
        int w = S(300), h = S(58), pad = S(6);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        bmp.SetResolution(DeviceDpi, DeviceDpi);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        Theme.FillRound(g, Theme.SurfaceHover, new RectangleF(0, 0, w, h), S(8));

        var tr = new Rectangle(pad, pad, (h - 2 * pad) * 16 / 9, h - 2 * pad);
        DrawThumb(g, tr, forDrag: true);

        int tx = tr.Right + S(10);
        using var fmt = new StringFormat(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
        };
        using var titleBrush = new SolidBrush(Theme.Text);
        using var mutedBrush = new SolidBrush(Theme.Muted);
        g.DrawString(Title, Theme.UiBold, titleBrush, new RectangleF(tx, S(7), w - tx - S(10), S(22)), fmt);
        g.DrawString($"{Extension.ToUpperInvariant()}  ·  {Fmt.Size(FileSize)}", Theme.UiSmall, mutedBrush, new RectangleF(tx, S(29), w - tx - S(10), S(20)), fmt);

        cursorOffset = new Point(tr.Left + tr.Width / 2, tr.Top + tr.Height / 2);
        return bmp;
    }

    // ================================================================ mouse

    protected override void OnMouseEnter(EventArgs e)
    {
        hovering = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovering = false;
        hoverZone = Zone.None;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!hovering) { hovering = true; Invalidate(); }
        var zone = HitTest(e.Location);
        if (zone != hoverZone) { hoverZone = zone; Invalidate(); }
        bool clickable = zone is Zone.Folder or Zone.Close or Zone.Trim or Zone.Shrink ||
                         (zone == Zone.Body && State is ItemState.Done or ItemState.Failed);
        Cursor = clickable ? Cursors.Hand : Cursors.Default;

        if (mouseDown && downZone == Zone.Body && IsDone && e.Button == MouseButtons.Left)
        {
            var drag = SystemInformation.DragSize;
            if (Math.Abs(e.X - downPos.X) * 2 >= drag.Width || Math.Abs(e.Y - downPos.Y) * 2 >= drag.Height)
            {
                mouseDown = false;
                BeginDrag();
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            mouseDown = true;
            downPos = e.Location;
            downZone = HitTest(e.Location);
        }
        else if (e.Button == MouseButtons.Right)
        {
            ShowMenu(e.Location);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !mouseDown) return;
        mouseDown = false;
        var zone = HitTest(e.Location);
        if (zone != downZone) return;
        switch (zone)
        {
            case Zone.Close:
                if (IsActive) Cancel();
                else RemoveRequested?.Invoke(this);
                break;
            case Zone.Folder: ShowInFolder(); break;
            case Zone.Trim: TrimRequested?.Invoke(this); break;
            case Zone.Shrink: ShrinkRequested?.Invoke(this); break;
            case Zone.Body when IsDone: OpenFile(); break;
            case Zone.Body when State == ItemState.Failed: Start(); break;
        }
    }

    // ================================================================ preview helpers

    internal static MediaCard Demo(Settings settings, string title, ItemState state, double percent, string status,
        Bitmap? thumb, long size = 0, MediaInfo? info = null, bool audio = false)
    {
        var card = new MediaCard(settings, title, "https://youtu.be/demo", thumb, audio)
        {
            percent = percent, FileSize = size, Info = info, FilePath = "demo.mp4",
        };
        card.SetState(state, status);
        return card;
    }

    internal Bitmap DemoDragImage() => BuildDragImage(out _);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            anim.Dispose();
            thumb?.Dispose();
            thumbCache?.Dispose();
        }
        base.Dispose(disposing);
    }
}

static class Thumbs
{
    /// <summary>Scales <paramref name="src"/> to fill <paramref name="size"/> (cropping the overflow).</summary>
    public static Bitmap Cover(Image src, Size size, bool dimWhites)
    {
        var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Max(bmp.Width / (float)src.Width, bmp.Height / (float)src.Height);
        float sw = bmp.Width / scale, sh = bmp.Height / scale;
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);
        // The shell drag image treats pure white as transparent; nudge it down a hair.
        if (dimWhites) attrs.SetColorMatrix(new ColorMatrix { Matrix00 = 0.985f, Matrix11 = 0.985f, Matrix22 = 0.985f });
        g.DrawImage(src, new Rectangle(Point.Empty, bmp.Size), (src.Width - sw) / 2, (src.Height - sh) / 2, sw, sh, GraphicsUnit.Pixel, attrs);
        return bmp;
    }

    /// <summary>Paints an already-sized image into a rounded shape with smooth (anti-aliased) corners.</summary>
    public static void FillRounded(Graphics g, Image img, Rectangle r, GraphicsPath path)
    {
        using var brush = new TextureBrush(img, WrapMode.Clamp);
        brush.TranslateTransform(r.X, r.Y);
        g.FillPath(brush, path);
    }
}
