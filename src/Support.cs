using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace RedDrop;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(28, 28, 31);
    public static readonly Color Surface = Color.FromArgb(40, 40, 44);
    public static readonly Color SurfaceHover = Color.FromArgb(50, 50, 55);
    public static readonly Color Pressed = Color.FromArgb(68, 68, 74);
    public static readonly Color Thumb = Color.FromArgb(58, 58, 64);
    public static readonly Color Border = Color.FromArgb(64, 64, 70);
    public static readonly Color Text = Color.FromArgb(242, 242, 245);
    public static readonly Color Muted = Color.FromArgb(150, 150, 160);
    public static readonly Color Accent = Brand.Accent;
    public static readonly Color AccentHover = Brand.AccentHover;
    public static readonly Color AccentDown = Brand.AccentDown;
    public static readonly Color Success = Color.FromArgb(72, 199, 116);
    public static readonly Color Error = Color.FromArgb(255, 112, 102);

    static readonly string IconFamily =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static readonly Font Ui = new("Segoe UI", 9.75f);
    public static readonly Font UiSmall = new("Segoe UI", 8.75f);
    public static readonly Font UiBold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font UiSmallBold = new("Segoe UI Semibold", 8.75f);
    public static readonly Font Input = new("Segoe UI", 10.5f);
    public static readonly Font Icons = new(IconFamily, 10f);
    public static readonly Font IconsSmall = new(IconFamily, 8.5f);

    const TextFormatFlags TextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
    const TextFormatFlags GlyphFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d < 1)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
    {
        using var brush = new SolidBrush(color);
        using var path = RoundRect(r, radius);
        g.FillPath(brush, path);
    }

    public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color color,
        TextFormatFlags extra = TextFormatFlags.Default) =>
        TextRenderer.DrawText(g, text, font, r, color, TextFlags | extra);

    public static void DrawGlyph(Graphics g, string glyph, Rectangle r, Color color, Font? font = null) =>
        TextRenderer.DrawText(g, glyph, font ?? Icons, r, color, GlyphFlags);
}

static class Glyphs
{
    public const string Folder = "\uE8B7", Settings = "\uE713", Close = "\uE8BB", Download = "\uE896",
        Check = "\uE73E", Error = "\uE783", Play = "\uE768", Stop = "\uE71A", Cut = "\uE8C6", Shrink = "\uE73F";
}

static class Native
{
    public const int WM_HOTKEY = 0x0312, WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2, EM_SETCUEBANNER = 0x1501;
    public const int CS_DROPSHADOW = 0x00020000, WS_EX_TOOLWINDOW = 0x00000080;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    [DllImport("user32.dll")] static extern short GetKeyState(int nVirtKey);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    public static bool IsKeyDown(Keys key) => (GetKeyState((int)key) & 0x8000) != 0;

    /// <summary>Windows 11 rounded corners + subtle border on a borderless window (no-op on Windows 10).</summary>
    public static void StyleWindow(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        int border = ColorTranslator.ToWin32(Theme.Border);
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }

    public static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }
}

readonly record struct Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, Keys Key)
{
    public static readonly Hotkey Default = new(true, true, false, false, Keys.Y);

    public uint Modifiers =>
        (Ctrl ? Native.MOD_CONTROL : 0u) | (Alt ? Native.MOD_ALT : 0u) |
        (Shift ? Native.MOD_SHIFT : 0u) | (Win ? Native.MOD_WIN : 0u);

    public bool IsValid =>
        Key != Keys.None && (Ctrl || Alt || Shift || Win || Key is >= Keys.F1 and <= Keys.F24 || Key == Keys.Pause);

    public List<string> Parts()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        if (Key != Keys.None) parts.Add(KeyName(Key));
        return parts;
    }

    public override string ToString() => string.Join("+", Parts());

    public string Serialize()
    {
        var parts = Parts();
        if (Key != Keys.None) parts[^1] = Key.ToString();
        return string.Join("+", parts);
    }

    public static bool TryParse(string text, out Hotkey hotkey)
    {
        bool ctrl = false, alt = false, shift = false, win = false;
        Keys key = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Keys.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, true, out key)) key = Keys.None;
                    break;
            }
        }
        hotkey = new Hotkey(ctrl, alt, shift, win, key);
        return hotkey.IsValid;
    }

    static string KeyName(Keys k) => k switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (k - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num " + (char)('0' + (k - Keys.NumPad0)),
        Keys.Oemtilde => "`", Keys.OemMinus => "-", Keys.Oemplus => "=",
        Keys.OemOpenBrackets => "[", Keys.OemCloseBrackets => "]", Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'", Keys.Oemcomma => ",", Keys.OemPeriod => ".",
        Keys.OemQuestion => "/", Keys.OemPipe => "\\",
        Keys.Next => "PageDown", Keys.Prior => "PageUp", Keys.Return => "Enter", Keys.Back => "Backspace",
        _ => k.ToString(),
    };
}

sealed class Settings
{
    public Hotkey Hotkey = Hotkey.Default;
    public string Folder = Path.Combine(Shell.DownloadsFolder(), Brand.DefaultFolder);
    public int DiscordMb = 20; // free Discord upload limit since Aug 2026 (Nitro Basic 50, Nitro 500)
    public bool Audio;         // download as MP3 instead of MP4
    public bool UpdateCheck;   // weekly check of the GitHub releases; off unless the user turned it on
    public DateTime LastUpdateCheck;

    /// <summary>Decimal megabytes: the stricter reading, so files fit whether Discord means MB or MiB.</summary>
    public double DiscordLimitBytes => DiscordMb * 1_000_000.0;

    static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.ini");

    public static Settings Load()
    {
        var s = new Settings();
        if (!File.Exists(FilePath)) return s;
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq < 0 || line.TrimStart().StartsWith('#')) continue;
                var key = line[..eq].Trim().ToLowerInvariant();
                var value = line[(eq + 1)..].Trim();
                if (key == "hotkey" && Hotkey.TryParse(value, out var hk)) s.Hotkey = hk;
                else if (key == "folder" && value.Length > 0) s.Folder = Environment.ExpandEnvironmentVariables(value);
                else if (key == "discord_mb" && int.TryParse(value, out var mb) && mb > 0) s.DiscordMb = mb;
                else if (key == "format") s.Audio = value.Equals("mp3", StringComparison.OrdinalIgnoreCase);
                else if (key == "update_check") s.UpdateCheck = value.Equals("on", StringComparison.OrdinalIgnoreCase);
                else if (key == "last_update_check" && DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                             DateTimeStyles.None, out var checkedAt)) s.LastUpdateCheck = checkedAt;
            }
        }
        catch { /* fall back to defaults */ }
        return s;
    }

    public void Save()
    {
        try
        {
            File.WriteAllLines(FilePath, new[]
            {
                $"# {Brand.Name} settings",
                $"hotkey={Hotkey.Serialize()}",
                $"folder={Folder}",
                $"discord_mb={DiscordMb}",
                $"format={(Audio ? "mp3" : "mp4")}",
                $"update_check={(UpdateCheck ? "on" : "off")}",
                $"last_update_check={(LastUpdateCheck == default ? "" : LastUpdateCheck.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}",
            });
        }
        catch { /* settings are a convenience; ignore write failures */ }
    }
}

/// <summary>
/// Starts the app at sign-in through a per-user scheduled task. (The HKCU\...\Run entry used earlier
/// was silently skipped by Windows on every boot, so it's migrated away.)
/// </summary>
static class Startup
{
    const string TaskName = Brand.Name;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", LegacyValue = Brand.Id;
    static bool? cached;

    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled => cached ??= Schtasks("/Query", "/TN", TaskName) == 0;

    public static void Set(bool enabled)
    {
        RemoveLegacyRunEntry();
        cached = null;
        if (!enabled)
        {
            Schtasks("/Delete", "/TN", TaskName, "/F");
            Log.Write("Startup: task removed");
            return;
        }
        var xml = Path.Combine(Path.GetTempPath(), $"{Brand.Id.ToLowerInvariant()}-task-{Environment.ProcessId}.xml");
        File.WriteAllText(xml, TaskXml(), Encoding.Unicode);
        try
        {
            int code = Schtasks("/Create", "/TN", TaskName, "/XML", xml, "/F");
            Log.Write($"Startup: task created (schtasks exit {code})");
            if (code != 0) throw new InvalidOperationException($"Task Scheduler refused the task (exit code {code}).");
        }
        finally { File.Delete(xml); }
    }

    public static void MigrateLegacyRunEntry()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            if (key?.GetValue(LegacyValue) == null) return;
        try { Set(true); }
        catch (Exception ex) { Log.Write("Startup: migration failed: " + ex.Message); }
    }

    static void RemoveLegacyRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(LegacyValue, false);
    }

    static int Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && args[0] != "/Query") Log.Write($"schtasks {args[0]}: {stderr.Result.Trim()}");
        return p.ExitCode;
    }

    static string TaskXml()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name)!;
        string exe = SecurityElement.Escape(ExePath)!;
        string dir = SecurityElement.Escape(Path.GetDirectoryName(ExePath)!)!;
        // No battery restrictions and no time limit: the defaults would skip it on battery or kill it after 3 days.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts {Brand.Name} in the tray when you sign in, so its keyboard shortcut works.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{exe}"</Command>
                  <Arguments>--background</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}

/// <summary>Small rolling log next to the exe (reddrop.log), for diagnosing startup / shortcut problems.</summary>
static class Log
{
    static readonly object Gate = new();

    static string FilePath => Path.Combine(AppContext.BaseDirectory, Brand.Id.ToLowerInvariant() + ".log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 256 * 1024) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}

static class Tools
{
    /// <summary>PATH rebuilt from the registry, so tools installed after login (winget) are found.</summary>
    public static string SearchPath()
    {
        var dirs = new List<string>();
        foreach (var value in new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        })
        {
            if (!string.IsNullOrEmpty(value))
                dirs.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
        return string.Join(';', dirs.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static string? Find(string exe)
    {
        foreach (var dir in SearchPath().Split(';'))
        {
            try
            {
                var path = Path.Combine(dir.Trim('"'), exe);
                if (!File.Exists(path)) continue;
                // winget puts symlinks in WinGet\Links; use the real file so "yt-dlp -U" updates the right one.
                return new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
            }
            catch { }
        }
        return null;
    }
}

static class Shell
{
    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string DownloadsFolder() =>
        Native.KnownFolder(DownloadsId) ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start("explorer.exe", $"\"{folder.TrimEnd('\\')}\"");
        }
        catch { }
    }

    public static void ShowInFolder(string file)
    {
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
    }

    public static void Open(string file)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { }
    }
}

static class AppIcon
{
    public static Icon Get(int size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("app.icon.ico");
        return stream != null ? new Icon(stream, size, size) : (Icon)SystemIcons.Application.Clone();
    }
}

static class Fmt
{
    public static string Size(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} B" : $"{bytes:0.0} {units[i]}";
    }

    public static string Eta(double seconds) => seconds switch
    {
        < 1 => "almost done",
        < 60 => $"{Math.Ceiling(seconds):0} s left",
        < 3600 => $"{Math.Ceiling(seconds / 60):0} min left",
        _ => $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} min left",
    };

    public static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Seconds for command lines (always a dot, whatever the Windows language).</summary>
    public static string Sec(double seconds) => Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>0:42, 3:07, 1:02:15 — optionally with tenths (0:42.3).</summary>
    public static string Clock(double seconds, bool tenths = false)
    {
        seconds = Math.Max(0, seconds);
        if (tenths) seconds = Math.Round(seconds, 1);
        var t = TimeSpan.FromSeconds(seconds);
        string text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        return tenths ? text + "." + (int)(t.Milliseconds / 100) : text;
    }

    /// <summary>Filename-safe timestamp: 0m12s, 1h02m15s.</summary>
    public static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m{t.Seconds:00}s" : $"{t.Minutes}m{t.Seconds:00}s";
    }
}
