using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RedDrop;

/// <summary>
/// Optional updates from this app's GitHub releases.
/// Privacy: nothing happens unless the user turned the weekly check on or picked "Check for updates".
/// A check is one HTTPS request to api.github.com with no IDs or usage data. Nothing is installed without asking,
/// and the downloaded exe must match the SHA-256 published with the release, or it is thrown away.
/// </summary>
static class Updater
{
    public sealed record Release(Version Version, string Tag, string ExeUrl, string? Sha256, string? Sha256Url, string PageUrl, string Notes);

    const long MaxExeBytes = 200L * 1024 * 1024;
    public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(7);

    public static bool Available => Brand.UpdateRepo.Length > 0;
    public static Version Current => Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));
    public static string CurrentText => Short(Current);
    public static string ReleasesPage => $"https://github.com/{Brand.UpdateRepo}/releases";

    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    static string OldPath => ExePath + ".old";
    static string DownloadPath => ExePath + ".download";

    public static string Short(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);

    static Version Normalize(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

    static HttpClient NewClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Brand.Id + "-updater"); // GitHub rejects requests without one
        return http;
    }

    /// <summary>The newest published release if it is newer than this copy; null when up to date.</summary>
    public static Task<Release?> CheckAsync(CancellationToken ct = default) => CheckAsync(Brand.UpdateRepo, Brand.Id + ".exe", Current, ct);

    internal static async Task<Release?> CheckAsync(string repo, string exeName, Version current, CancellationToken ct = default)
    {
        using var http = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        string tag = Text(root, "tag_name");
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
        var version = Normalize(parsed);
        if (version <= Normalize(current)) return null;

        string downloads = $"/{repo}/releases/download/";
        string? exeUrl = null, digest = null, shaUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                string name = Text(asset, "name"), url = Text(asset, "browser_download_url");
                // Only files attached to this repo's releases on github.com.
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
                    !uri.AbsolutePath.StartsWith(downloads, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(exeName, StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = url;
                    string d = Text(asset, "digest"); // GitHub's own "sha256:<hex>" for the uploaded file
                    if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = d[7..].ToLowerInvariant();
                }
                else if (name.Equals(exeName + ".sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
            }
        if (exeUrl == null) return null;

        string notes = Text(root, "body").Trim();
        if (notes.Length > 400) notes = notes[..400].TrimEnd() + "…";
        string page = Text(root, "html_url");
        return new Release(version, tag, exeUrl, digest, shaUrl, page.Length > 0 ? page : $"https://github.com/{repo}/releases", notes);
    }

    /// <summary>
    /// Downloads the new exe next to this one, checks it, and swaps the files. A running exe can't be
    /// overwritten but can be renamed, so: current → .old, new → current. Then starts the new copy;
    /// the caller should exit right after.
    /// </summary>
    public static async Task InstallAsync(Release release, CancellationToken ct = default)
    {
        string expected = release.Sha256 ?? await FetchSha256Async(release.Sha256Url, ct)
            ?? throw new InvalidOperationException("This release has no checksum, so it can't be checked. Download it from the releases page instead.");

        try
        {
            using var http = NewClient();
            using var response = await http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxExeBytes) throw new InvalidDataException("The download is unexpectedly large.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await SaveCheckedAsync(input, expected, ct);
            Apply(release.Version);
        }
        finally
        {
            try { File.Delete(DownloadPath); } catch { }
        }
    }

    /// <summary>Writes the new exe next to this one, refusing it unless its SHA-256 is the published one.</summary>
    internal static async Task SaveCheckedAsync(Stream input, string expectedSha256, CancellationToken ct = default)
    {
        await using var output = new FileStream(DownloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxExeBytes) throw new InvalidDataException("The download is unexpectedly large.");
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await output.FlushAsync(ct);
        string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        if (actual != expectedSha256.ToLowerInvariant())
            throw new InvalidDataException("The download doesn't match the release's checksum, so it was thrown away.");
    }

    /// <summary>Swaps the checked download in and starts it; the caller exits right after.</summary>
    internal static void Apply(Version expected)
    {
        // Belt and braces: it has to be this app, and the version the release says.
        var info = FileVersionInfo.GetVersionInfo(DownloadPath);
        if (!string.Equals(info.ProductName, Brand.Name, StringComparison.Ordinal) ||
            !Version.TryParse(info.FileVersion, out var fileVersion) || Normalize(fileVersion) != Normalize(expected))
            throw new InvalidDataException("The downloaded file isn't the expected version of " + Brand.Name + ".");

        File.Move(ExePath, OldPath, overwrite: true);
        try { File.Move(DownloadPath, ExePath); }
        catch { File.Move(OldPath, ExePath); throw; }

        Log.Write($"Updated {CurrentText} -> {Short(Normalize(expected))}; restarting");
        Process.Start(new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            ArgumentList = { "--after-update", Environment.ProcessId.ToString() },
        })?.Dispose();
    }

    static async Task<string?> FetchSha256Async(string? url, CancellationToken ct)
    {
        if (url == null) return null;
        using var http = NewClient();
        string text = await http.GetStringAsync(url, ct);
        var match = Regex.Match(text, @"\b[0-9a-fA-F]{64}\b");
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    /// <summary>Waits (up to 15 s) for the copy that just installed this update to exit.</summary>
    public static bool WaitForExit(int pid)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(15_000);
        }
        catch (ArgumentException) { /* already gone */ }
        catch (InvalidOperationException) { }
        return true;
    }

    /// <summary>Removes the previous exe left behind by an update (it can only go once that copy has exited).</summary>
    public static void CleanUp()
    {
        for (int attempt = 0; attempt < 10 && File.Exists(OldPath); attempt++)
        {
            try { File.Delete(OldPath); }
            catch { Thread.Sleep(300); }
        }
    }

    public static void OpenReleasesPage(string? url = null)
    {
        try { Process.Start(new ProcessStartInfo(url ?? ReleasesPage) { UseShellExecute = true }); } catch { }
    }

    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
