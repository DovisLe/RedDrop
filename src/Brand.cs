namespace RedDrop;

/// <summary>
/// Everything that names or colours the app, in one place. A local build can swap this file for its own
/// copy (see the BrandDir note in RedDrop.csproj) to make a renamed edition without touching the rest.
/// </summary>
static class Brand
{
    public const string Name = "RedDrop";           // window titles, tray, menus
    public const string Id = "RedDrop";             // exe, mutex, log and temp-file names
    public const string Tagline = "Video link → MP4 / MP3";
    public const string LinkHint = "Paste a video link…";
    public const string DefaultFolder = "RedDrop";  // inside Downloads

    /// <summary>GitHub "owner/repo" whose releases the updater checks. Empty turns the updater off.</summary>
    public const string UpdateRepo = "DovisLe/RedDrop";

    public static readonly Color Accent = Color.FromArgb(255, 59, 48);
    public static readonly Color AccentHover = Color.FromArgb(255, 90, 80);
    public static readonly Color AccentDown = Color.FromArgb(215, 40, 32);
}
