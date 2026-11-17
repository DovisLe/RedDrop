using System.Drawing.Imaging;

namespace RedDrop;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ToolStripManager.Renderer = new DarkRenderer(); // dark submenus too

        int preview = Array.IndexOf(args, "--preview");
        if (preview >= 0 && preview + 1 < args.Length)
        {
            RenderPreview(args[preview + 1], args.Skip(preview + 2).ToArray());
            return;
        }
        // Used by the installer: <app>.exe --startup on|off
        int startup = Array.IndexOf(args, "--startup");
        if (startup >= 0 && startup + 1 < args.Length)
        {
            try { Startup.Set(args[startup + 1].Equals("on", StringComparison.OrdinalIgnoreCase)); }
            catch (Exception ex) { Log.Write("--startup failed: " + ex.Message); Environment.ExitCode = 1; }
            return;
        }
        int selfTest = Array.IndexOf(args, "--selftest");
        if (selfTest >= 0 && selfTest + 2 < args.Length)
        {
            SelfTest(args[selfTest + 1], args[selfTest + 2]);
            return;
        }
        int updateTest = Array.IndexOf(args, "--selftest-update");
        if (updateTest >= 0 && updateTest + 3 < args.Length)
        {
            UpdateSelfTest(args[updateTest + 1], args[updateTest + 2], args[updateTest + 3]);
            return;
        }

        // Used by the installer: <app>.exe --updates on|off (the weekly update check is off unless the user says yes).
        int updates = Array.IndexOf(args, "--updates");
        if (updates >= 0 && updates + 1 < args.Length)
        {
            var saved = Settings.Load();
            saved.UpdateCheck = args[updates + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
            saved.Save();
            return;
        }

        // Started by the updater: let the old copy exit first, so this one can take over the shortcut.
        int after = Array.IndexOf(args, "--after-update");
        bool updated = after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out int oldPid) && Updater.WaitForExit(oldPid);

        using var mutex = new Mutex(true, $@"Local\{Brand.Id}.Instance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{Brand.Id}.Show");
        if (!first)
        {
            Log.Write($"Second launch (pid {Environment.ProcessId}) - showing the running instance");
            Native.AllowSetForegroundWindow(-1); // ASFW_ANY: let the running instance take focus
            showSignal.Set();
            return;
        }

        Log.Write($"Started (pid {Environment.ProcessId}, args: {string.Join(' ', args)})");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) LogError(ex); };

        Updater.CleanUp(); // the previous exe an update left behind
        using var app = new TrayApp(args.Contains("--background") || updated, showSignal, updated);
        Application.Run(app);
        Log.Write("Stopped");
    }

    static void LogError(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n"); }
        catch { }
        Log.Write("ERROR " + ex.GetType().Name + ": " + ex.Message);
    }

    /// <summary>Dev aid: runs the real ffmpeg shrink / clip / cancel paths headlessly and writes a report.</summary>
    static void SelfTest(string video, string dir)
    {
        Directory.CreateDirectory(dir);
        var report = new List<string>();
        void Say(string line)
        {
            report.Add(line);
            File.WriteAllLines(Path.Combine(dir, "selftest.txt"), report);
        }
        static T Sync<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

        var info = Sync(() => MediaInfo.ProbeAsync(video));
        long size = new FileInfo(video).Length;
        Say($"input: {Path.GetFileName(video)}  {size:N0} bytes  {info}");
        if (info == null) return;
        Say($"max seconds for 20 MB: {Encoder.MaxSeconds(20e6):0}");

        double d = info.Duration;
        var runs = new (string Name, double Start, double Length, bool Discord, double Limit)[]
        {
            ("whole-discord-20MB", 0, d, true, 20e6),
            ("clip-full-quality", d * 0.15, d * 0.25, false, 20e6),
            ("clip-discord-2MB", d * 0.15, d * 0.25, true, 2e6),
            ("clip-discord-fits-as-is", d * 0.15, d * 0.25, true, 200e6),
            ("clip-discord-squeezed", d * 0.15, d * 0.25, true, 400_000),
            ("whole-discord-too-small", 0, d, true, 150_000),
        };
        foreach (var run in runs)
        {
            var job = new EncodeJob(video, Path.Combine(dir, run.Name + Path.GetExtension(video)), run.Start, run.Length, run.Discord, run.Limit, info, size);
            double lastProgress = 0;
            string lastLabel = "";
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Task.Run(() => Encoder.RunAsync(job, (p, label) => { lastProgress = p; lastLabel = label; }, CancellationToken.None)).GetAwaiter().GetResult();
                var result = Sync(() => MediaInfo.ProbeAsync(job.Output));
                long outSize = new FileInfo(job.Output).Length;
                Say($"{run.Name}: OK  {outSize:N0} bytes (limit {run.Limit:N0}, fits={outSize <= run.Limit})  " +
                    $"len={result?.Duration:0.00}s (asked {run.Length:0.00})  {result?.Width}x{result?.Height}@{result?.Fps:0}  audio={result?.HasAudio}  " +
                    $"progress={lastProgress:0.00} '{lastLabel}'  {clock.Elapsed.TotalSeconds:0.0}s");
            }
            catch (Exception ex) { Say($"{run.Name}: FAILED  {ex.Message}"); }
        }

        // Play clip: the exact decoder commands the player streams from (checked without playing any sound).
        if (Tools.Find("ffmpeg.exe") is { } ffmpeg)
        {
            if (info.HasAudio)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var p = ClipPlayer.Launch(ffmpeg, ClipPlayer.AudioArgs(video, d * 0.1, 3));
                using var pcm = new MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(pcm);
                p.WaitForExit();
                long expected = 3L * SoundStream.Rate * 4;
                Say($"play clip sound: 3 s -> {pcm.Length:N0} bytes (48 kHz stereo needs {expected:N0}) " +
                    $"{(Math.Abs(pcm.Length - expected) <= SoundStream.Rate * 4 / 10 ? "OK" : "FAILED")}  in {clock.ElapsedMilliseconds} ms");
            }
            if (info.HasVideo)
            {
                var frameSize = new Size(320, 180);
                double fps = Math.Min(30, info.Fps > 1 ? info.Fps : 30);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using (var p = ClipPlayer.Launch(ffmpeg, ClipPlayer.VideoArgs(video, d * 0.1, 2, frameSize, fps)))
                using (var raw = new MemoryStream())
                {
                    p.StandardOutput.BaseStream.CopyTo(raw);
                    p.WaitForExit();
                    double frames = raw.Length / (double)(frameSize.Width * frameSize.Height * 4);
                    Say($"play clip video: 2 s -> {frames:0.#} frames of {frameSize.Width}x{frameSize.Height} (expected {2 * fps:0}) " +
                        $"{(Math.Abs(frames - 2 * fps) <= 2 ? "OK" : "FAILED")}  in {clock.ElapsedMilliseconds} ms");
                }
                using var stream = new FrameStream(ClipPlayer.Launch(ffmpeg, ClipPlayer.VideoArgs(video, d * 0.1, 2, frameSize, fps)), frameSize);
                using var bitmap = new Bitmap(frameSize.Width, frameSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                int handed = 0;
                var until = DateTime.Now.AddSeconds(5);
                while (DateTime.Now < until && handed < (int)(2 * fps) - 1)
                {
                    if (stream.CopyFrameAt(handed, bitmap)) handed++;
                    else Thread.Sleep(2);
                }
                Say($"play clip frames: {handed} handed over one by one, in order {(handed >= (int)(2 * fps) - 1 ? "OK" : "FAILED")}");
            }
        }

        var cancelJob = new EncodeJob(video, Path.Combine(dir, "cancelled" + Path.GetExtension(video)), 0, d, true, 20e6, info, size);
        using (var cts = new CancellationTokenSource(1500))
        {
            try
            {
                Task.Run(() => Encoder.RunAsync(cancelJob, (_, _) => { }, cts.Token)).GetAwaiter().GetResult();
                Say("cancel: finished before the cancel fired");
            }
            catch (OperationCanceledException)
            {
                Thread.Sleep(500);
                var leftovers = Directory.GetFiles(dir, "cancelled*").Concat(Directory.GetFiles(Path.GetTempPath(), Brand.Id.ToLowerInvariant() + "-*"));
                Say($"cancel: stopped OK, leftover files: [{string.Join(", ", leftovers.Select(Path.GetFileName))}]");
            }
            catch (Exception ex) { Say($"cancel: {ex.GetType().Name} {ex.Message}"); }
        }
    }

    /// <summary>
    /// Dev aid for the updater: reads a real GitHub release (any repo), then installs a locally built newer exe
    /// over this copy the same way a downloaded one would be: bad checksum and wrong version must be refused.
    /// </summary>
    static void UpdateSelfTest(string repoAndExe, string newExe, string reportFile)
    {
        var report = new List<string>();
        void Say(string line)
        {
            report.Add(line);
            File.WriteAllLines(reportFile, report);
        }
        static void Sync(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

        var parts = repoAndExe.Split(':');
        try
        {
            var found = Task.Run(() => Updater.CheckAsync(parts[0], parts[1], new Version(0, 0))).GetAwaiter().GetResult();
            Say(found == null ? $"check {parts[0]}: no newer release"
                : $"check {parts[0]}: {found.Tag} -> {Updater.Short(found.Version)}  exe={found.ExeUrl}  digest={(found.Sha256 != null ? "yes" : "no")}  sha256 file={(found.Sha256Url != null ? "yes" : "no")}");
            var mine = Task.Run(() => Updater.CheckAsync()).GetAwaiter().GetResult();
            Say($"check own repo ({Brand.UpdateRepo}): {(mine == null ? "nothing newer (or no releases yet)" : Updater.Short(mine.Version))}");
        }
        catch (Exception ex) { Say("check: FAILED " + ex.Message); }

        var version = Version.Parse(System.Diagnostics.FileVersionInfo.GetVersionInfo(newExe).FileVersion!);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(newExe))).ToLowerInvariant();
        try
        {
            using (var input = File.OpenRead(newExe)) Sync(() => Updater.SaveCheckedAsync(input, new string('0', 64)));
            Say("bad checksum: ACCEPTED - FAILED");
        }
        catch (InvalidDataException) { Say("bad checksum: refused OK"); }
        using (var input = File.OpenRead(newExe)) Sync(() => Updater.SaveCheckedAsync(input, sha));
        try
        {
            Updater.Apply(new Version(9, 9, 9));
            Say("wrong version: ACCEPTED - FAILED");
        }
        catch (InvalidDataException) { Say("wrong version: refused OK"); }
        Updater.Apply(version);
        Say($"swapped in {Updater.Short(version)} and started it");
    }

    /// <summary>Dev aid: renders the widget, dialogs, drag image and (given a video) the trim window to PNGs.</summary>
    static void RenderPreview(string dir, string[] media)
    {
        Directory.CreateDirectory(dir);
        using var menu = new ContextMenuStrip();
        using var widget = new WidgetForm(new Settings(), menu);
        widget.AddDemoItems();
        widget.CreateControl();
        Snapshot(widget, Path.Combine(dir, "widget.png"));

        foreach (var file in media)
        {
            var info = Task.Run(() => MediaInfo.ProbeAsync(file)).GetAwaiter().GetResult();
            if (info == null) continue;
            string tag = Path.GetExtension(file).TrimStart('.');
            using var trim = new TrimForm(Path.GetFileNameWithoutExtension(file), file, info, new FileInfo(file).Length,
                new Settings(), info.Duration * 0.2, info.Duration * 0.65, null);
            Snapshot(trim, Path.Combine(dir, $"trim-idle-{tag}.png"), waitForFrames: true);
            if (info.HasVideo) trim.PlaySilentForPreview(info.Duration * 0.3, 6); // real streaming playback, no sound
            else trim.Timeline.Playhead = info.Duration * 0.42;                  // MP3: just the playhead
            Snapshot(trim, Path.Combine(dir, $"trim-playing-{tag}.png"), waitForFrames: true);
            trim.StopForPreview();
            trim.Timeline.SimulateDrag(end: true);
            Snapshot(trim, Path.Combine(dir, $"trim-drag-{tag}.png"), waitForFrames: true);
        }

        using var empty = new WidgetForm(new Settings(), menu);
        empty.CreateControl();
        Snapshot(empty, Path.Combine(dir, "widget-empty.png"));

        using var hotkey = new HotkeyForm(Hotkey.Default);
        hotkey.CreateControl();
        Snapshot(hotkey, Path.Combine(dir, "hotkey.png"));

        using var drag = widget.DemoDragImage();
        drag.Save(Path.Combine(dir, "drag.png"), ImageFormat.Png);
    }

    static void Snapshot(Form form, string path, bool waitForFrames = false)
    {
        // Child controls only paint when the form is visible, so show it off-screen for a moment.
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.Show();
        form.Refresh();
        // Off-screen windows get no WM_PAINT, so render to a scratch bitmap to drive painting (and frame requests).
        var until = DateTime.Now.AddSeconds(waitForFrames ? 4 : 0);
        using (var scratch = new Bitmap(form.Width, form.Height))
            do
            {
                form.DrawToBitmap(scratch, new Rectangle(Point.Empty, form.Size));
                Application.DoEvents();
                Thread.Sleep(100);
            } while (DateTime.Now < until);
        Capture(form, path);
        form.Hide();
    }

    static void Capture(Control control, string path)
    {
        using var bmp = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bmp, new Rectangle(Point.Empty, control.Size));
        bmp.Save(path, ImageFormat.Png);
    }
}
