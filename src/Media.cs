using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RedDrop;

sealed record MediaInfo(double Duration, int Width, int Height, double Fps, bool HasAudio, bool HasVideo = true)
{
    public double Aspect => Width > 0 && Height > 0 ? Width / (double)Height : 16 / 9.0;

    public static async Task<MediaInfo?> ProbeAsync(string path)
    {
        var ffprobe = Tools.Find("ffprobe.exe");
        if (ffprobe == null || !File.Exists(path)) return null;
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries",
                     "format=duration:stream=codec_type,width,height,avg_frame_rate:stream_disposition=attached_pic", "-of", "json", path })
            psi.ArgumentList.Add(arg);
        try
        {
            using var p = Process.Start(psi)!;
            _ = p.StandardError.ReadToEndAsync();
            var json = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            double duration = root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)
                ? Fmt.Num(d.GetString() ?? "") : 0;
            int width = 0, height = 0;
            double fps = 0;
            bool audio = false;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    // An MP3's cover art shows up as a one-frame "video" stream; it isn't a video.
                    bool coverArt = s.TryGetProperty("disposition", out var disposition) &&
                                    disposition.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1;
                    if (type == "audio") audio = true;
                    else if (type == "video" && !coverArt && height == 0)
                    {
                        width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        fps = ParseRate(s.TryGetProperty("avg_frame_rate", out var r) ? r.GetString() : null);
                    }
                }
            }
            return duration > 0 ? new MediaInfo(duration, width, height, fps, audio, HasVideo: height > 0) : null;
        }
        catch { return null; }
    }

    static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length != 2) return Fmt.Num(rate);
        double den = Fmt.Num(parts[1]);
        return den > 0 ? Fmt.Num(parts[0]) / den : 0;
    }
}

/// <summary>One ffmpeg job: cut a range out of <see cref="Input"/>, optionally squeezed under a size limit.</summary>
sealed record EncodeJob(string Input, string Output, double Start, double Duration, bool ForDiscord,
    double LimitBytes, MediaInfo Info, long InputBytes)
{
    public bool IsWhole => Start <= 0.01 && Math.Abs(Duration - Info.Duration) < 0.05;
}

static class Encoder
{
    const int MinVideoKbps = 250; // below this H.264 turns to mush; better to ask for a shorter clip
    const int LowestAudioKbps = 48;

    public sealed record Plan(int VideoKbps, int AudioKbps, int Height, int Fps);

    /// <summary>Aim a little under the limit: MP4 overhead and rate-control drift eat a few percent.</summary>
    static double TargetFor(double limitBytes) => limitBytes * 0.95;

    /// <summary>Longest clip that still fits the limit at watchable quality.</summary>
    public static double MaxSeconds(double limitBytes) =>
        TargetFor(limitBytes) * 8 / 1000 * 0.98 / (MinVideoKbps + LowestAudioKbps);

    /// <summary>Size of the same range at the source's own bitrate.</summary>
    public static double EstimateBytes(EncodeJob job) => job.InputBytes / job.Info.Duration * job.Duration;

    /// <summary>The range is already small enough at source quality, so no squeezing is needed.</summary>
    public static bool FitsAsIs(EncodeJob job) => EstimateBytes(job) * 1.05 <= TargetFor(job.LimitBytes);

    /// <summary>How the range gets squeezed under the limit; null if it's too long to look decent.</summary>
    public static Plan? DiscordPlan(EncodeJob job) => PlanFor(job.Duration, TargetFor(job.LimitBytes), job.Info);

    /// <summary>Bitrate split and resolution that land a clip on <paramref name="targetBytes"/>; null if it can't look decent.</summary>
    public static Plan? PlanFor(double seconds, double targetBytes, MediaInfo info)
    {
        double totalKbps = targetBytes * 8 / 1000 / seconds * 0.98;
        int audio = !info.HasAudio ? 0 : totalKbps >= 1500 ? 128 : totalKbps >= 600 ? 96 : totalKbps >= 300 ? 64 : LowestAudioKbps;
        int video = (int)(totalKbps - audio);
        if (video < MinVideoKbps) return null;
        // Fewer pixels at low bitrates looks far better than a blocky full-resolution picture.
        int cap = video >= 3000 ? 1080 : video >= 1400 ? 720 : video >= 650 ? 480 : video >= 320 ? 360 : 240;
        int height = info.Height > 0 ? Math.Min(info.Height, cap) : cap;
        height -= height % 2;
        int fps = info.Fps > 31 && video < 2000 ? 30 : 0;
        return new Plan(video, audio, height, fps);
    }

    public static async Task RunAsync(EncodeJob job, Action<double, string> progress, CancellationToken ct)
    {
        var ffmpeg = Tools.Find("ffmpeg.exe")
            ?? throw new InvalidOperationException("ffmpeg not found. Install it with: winget install yt-dlp.FFmpeg");
        string temp = Path.Combine(Path.GetDirectoryName(job.Output)!,
            Path.GetFileNameWithoutExtension(job.Output) + ".part" + Path.GetExtension(job.Output));
        try
        {
            if (!job.Info.HasVideo)
            {
                await EncodeAudio(ffmpeg, job, temp, progress, ct);
            }
            else if (!job.ForDiscord)
            {
                await Ffmpeg(ffmpeg, ClipArgs(job, temp), job.Duration, p => progress(p, "Saving clip"), ct);
            }
            else
            {
                if (FitsAsIs(job))
                {
                    // Small enough already: keep full quality, and only squeeze if the re-encode came out too big.
                    await Ffmpeg(ffmpeg, ClipArgs(job, temp), job.Duration, p => progress(p, "Saving for Discord"), ct);
                    if (new FileInfo(temp).Length <= job.LimitBytes)
                    {
                        File.Move(temp, job.Output, overwrite: true);
                        return;
                    }
                    File.Delete(temp);
                }
                double target = TargetFor(job.LimitBytes);
                for (int attempt = 1; ; attempt++)
                {
                    var plan = PlanFor(job.Duration, target, job.Info)
                        ?? throw new InvalidOperationException($"Too long for {Fmt.Size(job.LimitBytes)}. Trim it to under {Fmt.Clock(MaxSeconds(job.LimitBytes))}");
                    string label = $"Shrinking to {plan.Height}p";
                    string passlog = Path.Combine(Path.GetTempPath(), $"{Brand.Id.ToLowerInvariant()}-{Guid.NewGuid():N}");
                    try
                    {
                        // Two-pass: the first pass measures the video so the second can hit the size exactly.
                        await Ffmpeg(ffmpeg, PassArgs(job, plan, 1, passlog, "NUL"), job.Duration, p => progress(p * 0.35, label), ct);
                        await Ffmpeg(ffmpeg, PassArgs(job, plan, 2, passlog, temp), job.Duration, p => progress(0.35 + p * 0.65, label), ct);
                    }
                    finally
                    {
                        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(passlog) + "*"))
                            try { File.Delete(f); } catch { }
                    }
                    long size = new FileInfo(temp).Length;
                    if (size <= job.LimitBytes) break;
                    if (attempt == 3) throw new InvalidOperationException($"Couldn't get it under {Fmt.Size(job.LimitBytes)}");
                    Log.Write($"Shrink overshot ({size} > {job.LimitBytes}), retrying smaller");
                    target *= job.LimitBytes * 0.93 / size;
                }
            }
            File.Move(temp, job.Output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { }
        }
    }

    const int MinAudioKbps = 48;

    /// <summary>MP3 bitrate that lands the range just under the limit; null if it would sound too bad.</summary>
    public static int? AudioKbpsFor(EncodeJob job)
    {
        double kbps = TargetFor(job.LimitBytes) * 8 / 1000 / job.Duration * 0.97;
        return kbps < MinAudioKbps ? null : (int)Math.Min(320, kbps);
    }

    public static double MaxAudioSeconds(double limitBytes) => TargetFor(limitBytes) * 8 / 1000 * 0.97 / MinAudioKbps;

    /// <summary>MP3: cuts are lossless stream copies; shrinking lowers the bitrate.</summary>
    static async Task EncodeAudio(string ffmpeg, EncodeJob job, string temp, Action<double, string> progress, CancellationToken ct)
    {
        if (!job.ForDiscord || FitsAsIs(job))
        {
            await Ffmpeg(ffmpeg, AudioArgs(job, temp, null), job.Duration,
                p => progress(p, job.ForDiscord ? "Saving for Discord" : "Saving clip"), ct);
            if (!job.ForDiscord || new FileInfo(temp).Length <= job.LimitBytes) return;
            File.Delete(temp);
        }
        int kbps = AudioKbpsFor(job)
            ?? throw new InvalidOperationException($"Too long for {Fmt.Size(job.LimitBytes)}. Trim it to under {Fmt.Clock(MaxAudioSeconds(job.LimitBytes))}");
        for (int attempt = 1; ; attempt++)
        {
            await Ffmpeg(ffmpeg, AudioArgs(job, temp, kbps), job.Duration, p => progress(p, $"Shrinking to {kbps} kbps"), ct);
            long size = new FileInfo(temp).Length;
            if (size <= job.LimitBytes) return;
            if (attempt == 3 || kbps <= 32) throw new InvalidOperationException($"Couldn't get it under {Fmt.Size(job.LimitBytes)}");
            kbps = Math.Max(32, (int)(kbps * job.LimitBytes * 0.93 / size));
            File.Delete(temp);
        }
    }

    static List<string> AudioArgs(EncodeJob job, string output, int? kbps)
    {
        var args = new List<string> { "-y", "-nostdin", "-hide_banner", "-v", "error", "-progress", "pipe:1", "-nostats" };
        if (!job.IsWhole) args.AddRange(new[] { "-ss", Fmt.Sec(job.Start), "-t", Fmt.Sec(job.Duration) });
        // Keep the cover art and song info; copying the audio (no kbps) makes the cut lossless.
        args.AddRange(new[] { "-i", job.Input, "-map", "0:a:0", "-map", "0:v?", "-map_metadata", "0", "-c:v", "copy" });
        args.AddRange(kbps == null ? new[] { "-c:a", "copy" } : new[] { "-c:a", "libmp3lame", "-b:a", $"{kbps}k" });
        args.AddRange(new[] { "-id3v2_version", "3", output });
        return args;
    }

    static List<string> Input(EncodeJob job, bool withAudio)
    {
        var args = new List<string> { "-y", "-nostdin", "-hide_banner", "-v", "error", "-progress", "pipe:1", "-nostats" };
        if (!job.IsWhole) args.AddRange(new[] { "-ss", Fmt.Sec(job.Start), "-t", Fmt.Sec(job.Duration) });
        args.AddRange(new[] { "-i", job.Input, "-map", "0:v:0" });
        if (withAudio && job.Info.HasAudio) args.AddRange(new[] { "-map", "0:a:0" });
        return args;
    }

    static List<string> PassArgs(EncodeJob job, Encoder.Plan plan, int pass, string passlog, string output)
    {
        var args = Input(job, withAudio: pass == 2);
        var filters = new List<string>();
        if (job.Info.Height > 0 && plan.Height < job.Info.Height) filters.Add($"scale=-2:{plan.Height}:flags=lanczos");
        if (plan.Fps > 0) filters.Add($"fps={plan.Fps}");
        if (filters.Count > 0) args.AddRange(new[] { "-vf", string.Join(",", filters) });
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-b:v", $"{plan.VideoKbps}k", "-pix_fmt", "yuv420p",
                              "-pass", pass.ToString(), "-passlogfile", passlog });
        if (pass == 1)
        {
            args.AddRange(new[] { "-an", "-f", "null", output });
        }
        else
        {
            if (job.Info.HasAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", $"{plan.AudioKbps}k" });
            args.AddRange(new[] { "-movflags", "+faststart", output });
        }
        return args;
    }

    static List<string> ClipArgs(EncodeJob job, string output)
    {
        // High quality (CRF 19), capped at the source bitrate so the clip is about the size the trim window promised.
        int srcKbps = Math.Max(300, (int)(job.InputBytes * 8 / 1000.0 / job.Info.Duration));
        var args = Input(job, withAudio: true);
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "19",
                              "-maxrate", $"{srcKbps * 11 / 10}k", "-bufsize", $"{srcKbps * 2}k", "-pix_fmt", "yuv420p" });
        if (job.Info.HasAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });
        args.AddRange(new[] { "-movflags", "+faststart", output });
        return args;
    }

    static async Task Ffmpeg(string exe, List<string> args, double duration, Action<double> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = new Process { StartInfo = psi };
        string lastError = "";
        p.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null && e.Data.StartsWith("out_time_us=") && long.TryParse(e.Data.AsSpan(12), out var us) && duration > 0)
                progress(Math.Clamp(us / 1e6 / duration, 0, 1));
        };
        p.ErrorDataReceived += (sender, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data; };
        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // keep the PC responsive while encoding
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
            await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) throw new InvalidOperationException(lastError.Length > 0 ? lastError : $"ffmpeg failed (exit code {p.ExitCode})");
    }
}

/// <summary>
/// Pulls single frames out of a video with ffmpeg, on demand, one at a time, newest request first.
/// "Quick" frames are the nearest keyframe (instant, used while dragging); exact frames decode up to the
/// precise time. Only alive while the trim window is open.
/// </summary>
sealed class FrameGrabber : IDisposable
{
    const int Capacity = 40;
    readonly string file, ffmpeg;
    readonly double maxTime;
    readonly int height;
    readonly Dictionary<(int Tenth, bool Exact), Bitmap> cache = new();
    readonly Queue<(int, bool)> age = new();
    readonly LinkedList<(int Tenth, bool Exact)> wanted = new();
    readonly object gate = new();
    Process? current;
    bool busy, disposed;

    public event Action? FrameReady;

    public FrameGrabber(string file, double duration, int height)
    {
        this.file = file;
        this.height = height - height % 2;
        maxTime = Math.Max(0, duration - 0.1);
        ffmpeg = Tools.Find("ffmpeg.exe") ?? "";
    }

    static int Key(double seconds) => (int)Math.Round(seconds * 10);

    /// <summary>
    /// The frame at <paramref name="seconds"/> if it's cached; otherwise the nearest cached one while it loads.
    /// With <paramref name="exact"/> = false (dragging) a quick keyframe is fetched first, then the exact frame
    /// if the handle stays put.
    /// </summary>
    public Bitmap? Get(double seconds, bool exact = true)
    {
        int tenth = Key(Math.Clamp(seconds, 0, maxTime));
        if (cache.TryGetValue((tenth, true), out var hit)) return hit;
        if (!exact && cache.TryGetValue((tenth, false), out var quick))
        {
            Request((tenth, true), urgent: false); // sharpen it if the user lingers here
            return quick;
        }
        Request((tenth, exact), urgent: true);
        Bitmap? best = null;
        int bestDistance = int.MaxValue;
        foreach (var (key, bmp) in cache)
        {
            int distance = Math.Abs(key.Tenth - tenth) * 2 + (key.Exact ? 0 : 1);
            if (distance < bestDistance) { bestDistance = distance; best = bmp; }
        }
        return bestDistance <= 200 ? best : null;
    }

    public Bitmap? CloneExact(double seconds) =>
        cache.TryGetValue((Key(Math.Clamp(seconds, 0, maxTime)), true), out var bmp) ? new Bitmap(bmp) : null;

    void Request((int, bool) key, bool urgent)
    {
        if (!urgent && wanted.Contains(key)) return;
        wanted.Remove(key);
        if (urgent) wanted.AddFirst(key); else wanted.AddLast(key);
        while (wanted.Count > 3) wanted.RemoveLast(); // stale drag positions aren't worth decoding
        Pump();
    }

    async void Pump()
    {
        while (!busy && !disposed && wanted.Count > 0 && ffmpeg.Length > 0)
        {
            var key = wanted.First!.Value;
            wanted.RemoveFirst();
            if (cache.ContainsKey(key) || (!key.Exact && cache.ContainsKey((key.Tenth, true)))) continue;
            busy = true;
            var frame = await Task.Run(() => Grab(key.Tenth / 10.0, key.Exact));
            busy = false;
            if (disposed) { frame?.Dispose(); return; }
            if (frame != null)
            {
                Store(key, frame);
                FrameReady?.Invoke();
            }
        }
    }

    void Store((int, bool) key, Bitmap frame)
    {
        cache[key] = frame;
        age.Enqueue(key);
        while (cache.Count > Capacity && age.Count > 0)
        {
            var old = age.Dequeue();
            if (old != key && cache.Remove(old, out var stale)) stale.Dispose();
        }
    }

    Bitmap? Grab(double seconds, bool exact)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error" };
        if (!exact) args.Add("-noaccurate_seek"); // stop at the keyframe instead of decoding up to the exact time
        args.AddRange(new[] { "-ss", Fmt.Sec(seconds), "-i", file, "-frames:v", "1", "-an", "-sn",
                              "-vf", $"scale=-2:{height}", "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "4", "-" });
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        try
        {
            using var p = Process.Start(psi)!;
            lock (gate) current = p;
            _ = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            if (ms.Length == 0) return null;
            ms.Position = 0;
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch { return null; }
        finally { lock (gate) current = null; }
    }

    public void Dispose()
    {
        disposed = true;
        lock (gate) { try { current?.Kill(); } catch { } }
        foreach (var bmp in cache.Values) bmp.Dispose();
        cache.Clear();
    }
}

/// <summary>
/// Plays a stretch of a file in the trim window: sound streamed straight to Windows (48 kHz stereo, a few small
/// buffers), and for videos, small frames decoded in step with that sound. Each part is one ffmpeg process that only
/// runs as fast as playback needs it (the pipe holds it back), and only while something plays.
/// </summary>
sealed class ClipPlayer : IDisposable
{
    const double MaxFps = 30;
    readonly string file, ffmpeg;
    readonly bool hasAudio;
    readonly Size frameSize;
    readonly double fps;
    readonly Stopwatch clock = new();
    SoundStream? sound;
    FrameStream? video;
    double start, length;
    bool playing;

    /// <param name="frameSize">Size of the picture on screen (decoded at exactly that size); empty for sound only.</param>
    public ClipPlayer(string file, string ffmpeg, bool hasAudio, Size frameSize, double sourceFps)
    {
        this.file = file;
        this.ffmpeg = ffmpeg;
        this.hasAudio = hasAudio;
        this.frameSize = frameSize.IsEmpty ? Size.Empty : new Size(frameSize.Width & ~1, frameSize.Height & ~1);
        fps = sourceFps > 1 ? Math.Min(MaxFps, sourceFps) : MaxFps;
        if (!this.frameSize.IsEmpty) Frame = new Bitmap(this.frameSize.Width, this.frameSize.Height, PixelFormat.Format32bppRgb);
    }

    /// <summary>The current picture (updated by <see cref="Present"/>).</summary>
    public Bitmap? Frame { get; }

    bool SoundWorks => sound is { Failed: false };
    public bool IsPlaying => playing && !(SoundWorks ? sound!.Finished : clock.Elapsed.TotalSeconds >= length);

    /// <summary>Where playback is, in seconds into the file. The sound card's own clock drives it, so picture and sound can't drift.</summary>
    public double Position => start + Math.Min(length, SoundWorks ? sound!.Seconds : clock.Elapsed.TotalSeconds);

    public void Play(double from, double seconds)
    {
        Stop();
        start = from;
        length = seconds;
        playing = true;
        if (hasAudio) sound = new SoundStream(Launch(ffmpeg, AudioArgs(file, from, seconds)));
        if (Frame != null) video = new FrameStream(Launch(ffmpeg, VideoArgs(file, from, seconds, frameSize, fps)), frameSize);
        clock.Restart();
    }

    /// <summary>Called by the window's timer: shows the frame that matches the sound. True if the picture changed.</summary>
    public bool Present() => video != null && Frame != null && video.CopyFrameAt((int)((Position - start) * fps), Frame);

    public void Stop()
    {
        playing = false;
        sound?.Dispose();
        sound = null;
        video?.Dispose();
        video = null;
        clock.Reset();
    }

    public void Dispose()
    {
        Stop();
        Frame?.Dispose();
    }

    internal static string[] AudioArgs(string file, double from, double seconds) => new[]
    {
        "-nostdin", "-hide_banner", "-v", "error", "-ss", Fmt.Sec(from), "-t", Fmt.Sec(seconds), "-i", file,
        "-vn", "-sn", "-ac", "2", "-ar", SoundStream.Rate.ToString(), "-f", "s16le", "-c:a", "pcm_s16le", "-",
    };

    /// <summary>Graphics-card decoding when there is one (about half the CPU), shrunk to the on-screen size with a quality filter.</summary>
    internal static string[] VideoArgs(string file, double from, double seconds, Size size, double fps) => new[]
    {
        "-nostdin", "-hide_banner", "-v", "error", "-hwaccel", "auto", "-ss", Fmt.Sec(from), "-t", Fmt.Sec(seconds), "-i", file,
        "-an", "-sn", "-vf", $"fps={Fmt.Sec(fps)},scale={size.Width}:{size.Height}:flags=area",
        "-pix_fmt", "bgra", "-f", "rawvideo", "-",
    };

    internal static Process Launch(string ffmpeg, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; // keep stderr drained so ffmpeg can never block on it
        p.BeginErrorReadLine();
        return p;
    }
}

/// <summary>
/// Streams PCM from ffmpeg to the sound card through waveOut: six 85 ms buffers (~0.5 s queued, ~100 KB in total),
/// refilled as each finishes. Memory stays the same however long the clip is, and the sound starts almost at once.
/// </summary>
sealed class SoundStream : IDisposable
{
    public const int Rate = 48000; // what Windows mixes at, so no extra resampling
    const int BlockAlign = 4, Buffers = 6, BufferBytes = 4096 * BlockAlign;
    const int WHDR_DONE = 1, CALLBACK_EVENT = 0x50000, TIME_SAMPLES = 2, TIME_BYTES = 4, WAVE_MAPPER = -1;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat { public short Tag, Channels; public int SamplesPerSec, AvgBytesPerSec; public short BlockAlign, BitsPerSample, Size; }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader { public IntPtr Data; public int BufferLength, BytesRecorded; public IntPtr User; public int Flags, Loops; public IntPtr Next, Reserved; }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    struct MmTime { [FieldOffset(0)] public int Type; [FieldOffset(4)] public uint Value; }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr device, int deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr device);
    [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr device, ref MmTime time, int size);

    static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    static readonly int LengthOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.BufferLength));
    static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));

    readonly Process decoder;
    readonly AutoResetEvent bufferDone = new(false);
    readonly IntPtr[] headers = new IntPtr[Buffers];
    readonly bool[] queued = new bool[Buffers];
    readonly Thread? feeder;
    IntPtr device;
    volatile bool stopping;

    public volatile bool Finished, Failed;

    public SoundStream(Process decoder)
    {
        this.decoder = decoder;
        var format = new WaveFormat
        {
            Tag = 1, Channels = 2, SamplesPerSec = Rate, AvgBytesPerSec = Rate * BlockAlign, BlockAlign = BlockAlign, BitsPerSample = 16,
        };
        if (waveOutOpen(out device, WAVE_MAPPER, ref format, bufferDone.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT) != 0)
        {
            device = IntPtr.Zero;
            Failed = Finished = true; // no sound device: the player falls back to a plain clock
            try { decoder.Kill(); } catch { }
            return;
        }
        for (int i = 0; i < Buffers; i++)
        {
            headers[i] = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { Data = Marshal.AllocHGlobal(BufferBytes), BufferLength = BufferBytes }, headers[i], false);
            waveOutPrepareHeader(device, headers[i], HeaderSize);
        }
        feeder = new Thread(Feed) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = Brand.Name + " sound" };
        feeder.Start();
    }

    void Feed()
    {
        var input = decoder.StandardOutput.BaseStream;
        var chunk = new byte[BufferBytes];
        bool ended = false;
        try
        {
            while (!stopping)
            {
                int free = -1, busy = 0;
                for (int i = 0; i < Buffers; i++)
                {
                    if (queued[i] && (Marshal.ReadInt32(headers[i], FlagsOffset) & WHDR_DONE) != 0) queued[i] = false;
                    if (queued[i]) busy++;
                    else if (free < 0) free = i;
                }
                if (ended || free < 0)
                {
                    if (ended && busy == 0) { Finished = true; return; }
                    bufferDone.WaitOne(50); // the sound card signals each finished buffer
                    continue;
                }
                int n = input.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false);
                n -= n % BlockAlign;
                if (n <= 0) { ended = true; continue; }
                IntPtr header = headers[free];
                Marshal.Copy(chunk, 0, Marshal.ReadIntPtr(header), n);
                Marshal.WriteInt32(header, LengthOffset, n);
                Marshal.WriteInt32(header, FlagsOffset, Marshal.ReadInt32(header, FlagsOffset) & ~WHDR_DONE);
                queued[free] = true;
                if (waveOutWrite(device, header, HeaderSize) != 0) { Failed = Finished = true; return; }
            }
        }
        catch { Failed = Finished = true; }
    }

    /// <summary>Seconds actually played by the sound card (0 until the first sample is heard).</summary>
    public double Seconds
    {
        get
        {
            if (device == IntPtr.Zero) return 0;
            var time = new MmTime { Type = TIME_SAMPLES };
            if (waveOutGetPosition(device, ref time, 12) != 0) return 0;
            return (time.Type == TIME_BYTES ? time.Value / (double)BlockAlign : time.Value) / Rate;
        }
    }

    public void Dispose()
    {
        stopping = true;
        try { decoder.Kill(); } catch { }
        if (device != IntPtr.Zero) waveOutReset(device); // silence at once and hand every buffer back
        bool feederStopped = feeder == null || feeder.Join(1000);
        if (device != IntPtr.Zero)
        {
            foreach (var header in headers) if (header != IntPtr.Zero) waveOutUnprepareHeader(device, header, HeaderSize);
            waveOutClose(device);
            device = IntPtr.Zero;
        }
        if (feederStopped)
        {
            foreach (var header in headers)
            {
                if (header == IntPtr.Zero) continue;
                Marshal.FreeHGlobal(Marshal.ReadIntPtr(header));
                Marshal.FreeHGlobal(header);
            }
        }
        decoder.Dispose();
        bufferDone.Dispose();
    }
}

/// <summary>
/// Reads raw frames from ffmpeg on a background thread, keeping at most a few decoded ahead (so ffmpeg runs at
/// playback speed, not flat out). The window takes whichever frame matches the sound; late ones are skipped.
/// </summary>
sealed class FrameStream : IDisposable
{
    const int Ahead = 3;
    readonly Process decoder;
    readonly int width, height, frameBytes;
    readonly Queue<(int Index, byte[] Pixels)> ready = new();
    readonly Stack<byte[]> spare = new(); // reused buffers: no garbage per frame
    readonly object gate = new();
    readonly Thread reader;
    volatile bool stopping;

    public FrameStream(Process decoder, Size size)
    {
        this.decoder = decoder;
        width = size.Width;
        height = size.Height;
        frameBytes = width * height * 4;
        reader = new Thread(Read) { IsBackground = true, Name = Brand.Name + " frames" };
        reader.Start();
    }

    void Read()
    {
        var input = decoder.StandardOutput.BaseStream;
        try
        {
            for (int index = 0; !stopping; index++)
            {
                byte[] buffer;
                lock (gate)
                {
                    while (ready.Count >= Ahead && !stopping) Monitor.Wait(gate, 100);
                    if (stopping) return;
                    buffer = spare.Count > 0 ? spare.Pop() : new byte[frameBytes];
                }
                if (input.ReadAtLeast(buffer, frameBytes, throwOnEndOfStream: false) < frameBytes) return;
                lock (gate) ready.Enqueue((index, buffer));
            }
        }
        catch { }
    }

    /// <summary>Copies the newest decoded frame at or before <paramref name="wanted"/> into the bitmap.</summary>
    public bool CopyFrameAt(int wanted, Bitmap target)
    {
        byte[]? pick = null;
        lock (gate)
        {
            while (ready.Count > 0 && ready.Peek().Index <= wanted)
            {
                var (_, pixels) = ready.Dequeue();
                if (pick != null) spare.Push(pick);
                pick = pixels;
            }
            Monitor.PulseAll(gate);
        }
        if (pick == null) return false;
        var data = target.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            if (data.Stride == width * 4) Marshal.Copy(pick, 0, data.Scan0, frameBytes);
            else for (int y = 0; y < height; y++) Marshal.Copy(pick, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally { target.UnlockBits(data); }
        lock (gate) spare.Push(pick);
        return true;
    }

    public void Dispose()
    {
        stopping = true;
        lock (gate) Monitor.PulseAll(gate);
        try { decoder.Kill(); } catch { }
        reader.Join(500);
        decoder.Dispose();
    }
}

static class Waveform
{
    /// <summary>One-off picture of the audio's loudness over time (used as the trim track for MP3s).</summary>
    public static Task<Bitmap?> RenderAsync(string file, int width, int height) => Task.Run(() =>
    {
        var ffmpeg = Tools.Find("ffmpeg.exe");
        if (ffmpeg == null) return null;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string color = $"0x{Theme.Muted.R:X2}{Theme.Muted.G:X2}{Theme.Muted.B:X2}";
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-v", "error", "-i", file, "-filter_complex",
                                    // sqrt scale + peaks keep quiet parts visible (linear looks like a flat line)
                                    $"aformat=channel_layouts=mono,showwavespic=s={width}x{height}:colors={color}:scale=sqrt:filter=peak",
                                    "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "-" })
            psi.ArgumentList.Add(arg);
        try
        {
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _ = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            if (ms.Length == 0) return null;
            ms.Position = 0;
            using var img = Image.FromStream(ms);
            return (Bitmap?)new Bitmap(img);
        }
        catch { return null; }
    });
}

/// <summary>Keeps the idle tray app as close to free as possible.</summary>
static class Efficiency
{
    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
    [DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr process);

    static readonly IntPtr CurrentProcess = new(-1);
    const int ProcessPowerThrottling = 4;
    const uint ExecutionSpeed = 1;

    /// <summary>Windows "efficiency mode" (EcoQoS): when on, Windows runs the app on efficient cores at low clocks.</summary>
    public static void SetIdle(bool idle)
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = idle ? ExecutionSpeed : 0 };
        SetProcessInformation(CurrentProcess, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }

    /// <summary>Releases memory the hidden app isn't using.</summary>
    public static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        EmptyWorkingSet(CurrentProcess);
    }
}
