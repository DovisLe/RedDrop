using System.Diagnostics;
using Microsoft.Win32;

namespace RedDrop;

/// <summary>Runs in the tray: owns the widget, the global shortcut and the menu.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly Settings settings = Settings.Load();
    readonly ContextMenuStrip menu = new()
    {
        Renderer = new DarkRenderer(),
        Font = Theme.Ui,
        ShowCheckMargin = true,
        ShowImageMargin = false,
    };
    readonly NotifyIcon tray;
    readonly WidgetForm widget;
    Updater.Release? pendingUpdate;
    bool checkingUpdates;
    Action? balloonAction;

    public TrayApp(bool background, WaitHandle showSignal, bool updated = false)
    {
        widget = new WidgetForm(settings, menu);
        _ = widget.Handle; // the hidden window receives the hotkey
        widget.ItemFinished += OnItemFinished;
        widget.VisibleChanged += (_, _) => { if (widget.Visible) MaybeCheckForUpdates(); };

        tray = new NotifyIcon
        {
            Icon = AppIcon.Get(SystemInformation.SmallIconSize.Width),
            Text = TrayText,
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) widget.Toggle(); };
        tray.BalloonTipClicked += (_, _) =>
        {
            var action = balloonAction ?? widget.ShowWidget;
            balloonAction = null;
            action();
        };
        menu.Opening += (_, _) => BuildMenu();
        BuildMenu();

        // A second launch (e.g. double-clicking the exe again) just brings the widget up.
        ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => widget.BeginInvoke(new Action(widget.ShowWidget)), null, Timeout.Infinite, false);

        // After sleep or unlock, take the shortcut again in case Windows dropped it.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        bool hotkeyOk = widget.TrySetHotkey(settings.Hotkey);
        if (!background) widget.ShowWidget();
        if (!hotkeyOk)
            Notify("Shortcut unavailable", $"{settings.Hotkey} is used by another app. {Brand.Name} will keep trying; or right-click the tray icon to pick another.");

        widget.BeginInvoke(new Action(Startup.MigrateLegacyRunEntry));
        widget.UpdateIdle(); // started hidden: settle into efficiency mode right away

        if (updated) Notify($"Updated to {Updater.CurrentText}", $"{Brand.Name} is up to date. Your settings were kept.");
        else if (Updater.Available && settings.UpdateCheck) _ = CheckForUpdatesLater();
    }

    // ---------------------------------------------------------------- updates (opt-in, at most weekly)

    async Task CheckForUpdatesLater()
    {
        await Task.Delay(TimeSpan.FromMinutes(2)); // one-shot, so sign-in isn't slowed down
        MaybeCheckForUpdates();
    }

    void MaybeCheckForUpdates()
    {
        if (!Updater.Available || !settings.UpdateCheck || checkingUpdates || pendingUpdate != null) return;
        if (DateTime.Now - settings.LastUpdateCheck < Updater.CheckEvery) return;
        _ = CheckForUpdates(manual: false);
    }

    async Task CheckForUpdates(bool manual)
    {
        if (checkingUpdates) return;
        checkingUpdates = true;
        try
        {
            var release = await Updater.CheckAsync();
            settings.LastUpdateCheck = DateTime.Now;
            settings.Save();
            if (release != null)
            {
                pendingUpdate = release;
                Log.Write($"Update available: {Updater.Short(release.Version)}");
                if (manual) OfferUpdate();
                else Notify("Update available", $"{Brand.Name} {Updater.Short(release.Version)} is out. Click here to install it.", OfferUpdate);
            }
            else if (manual)
            {
                MessageBox.Show(Owner, $"You have the newest version ({Updater.CurrentText}).", Brand.Name);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Update check failed: " + ex.Message);
            if (manual)
                MessageBox.Show(Owner, "Couldn't check for updates: " + ex.Message, Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            checkingUpdates = false;
            widget.UpdateIdle(); // give back the memory the network code used
        }
    }

    void OfferUpdate()
    {
        if (pendingUpdate is not { } release) return;
        if (widget.ActiveCount > 0)
        {
            MessageBox.Show(Owner, "Let the running downloads finish first, then install the update from the tray menu.", Brand.Name);
            return;
        }
        string notes = release.Notes.Length > 0 ? $"\n\nWhat's new:\n{release.Notes}" : "";
        var answer = MessageBox.Show(Owner,
            $"{Brand.Name} {Updater.Short(release.Version)} is available (you have {Updater.CurrentText}).{notes}\n\n" +
            $"Install it now? {Brand.Name} restarts for a moment. Your settings and videos are kept.",
            Brand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer == DialogResult.Yes) _ = InstallUpdate(release);
    }

    async Task InstallUpdate(Updater.Release release)
    {
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            await Updater.InstallAsync(release);
            Log.Write("Exit for update");
            widget.KillAll();
            widget.UnregisterHotkey();
            tray.Visible = false;
            ExitThread();
        }
        catch (Exception ex)
        {
            Log.Write("Update failed: " + ex.Message);
            if (MessageBox.Show(Owner, $"The update didn't install, nothing was changed.\n\n{ex.Message}\n\nOpen the releases page instead?",
                    Brand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                Updater.OpenReleasesPage(release.PageUrl);
        }
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Later(() => widget.RefreshHotkey("resume from sleep"));
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            Later(() => widget.RefreshHotkey(e.Reason.ToString()));
    }

    string TrayText => $"{Brand.Name} ({settings.Hotkey})";

    void BuildMenu()
    {
        menu.Items.Clear();
        if (pendingUpdate is { } update)
            menu.Items.Add(new ToolStripMenuItem($"Install update {Updater.Short(update.Version)}…", null, (_, _) => Later(OfferUpdate))
            {
                ForeColor = Theme.Success,
            });
        menu.Items.Add(new ToolStripMenuItem("Open widget", null, (_, _) => widget.ShowWidget())
        {
            ShortcutKeyDisplayString = settings.Hotkey.ToString(),
            Font = Theme.UiBold,
        });
        menu.Items.Add("Open save folder", null, (_, _) => Shell.OpenFolder(settings.Folder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Change shortcut…", null, (_, _) => Later(ChangeHotkey));
        menu.Items.Add("Change save folder…", null, (_, _) => Later(ChangeFolder));
        var limit = new ToolStripMenuItem($"Discord limit: {settings.DiscordMb} MB");
        foreach (var (mb, label) in new[] { (20, "20 MB (free)"), (50, "50 MB (Nitro Basic)"), (500, "500 MB (Nitro)") })
        {
            limit.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) =>
            {
                settings.DiscordMb = mb;
                settings.Save();
                widget.RefreshCards();
            }) { Checked = settings.DiscordMb == mb });
        }
        menu.Items.Add(limit);
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => Later(ToggleStartup)) { Checked = Startup.IsEnabled });
        menu.Items.Add("Update yt-dlp", null, (_, _) => UpdateYtDlp());
        if (Updater.Available)
        {
            var updates = new ToolStripMenuItem($"{Brand.Name} updates");
            updates.DropDownItems.Add("Check now", null, (_, _) => Later(() => _ = CheckForUpdates(manual: true)));
            updates.DropDownItems.Add(new ToolStripMenuItem("Check once a week", null, (_, _) =>
            {
                settings.UpdateCheck = !settings.UpdateCheck;
                settings.Save();
            }) { Checked = settings.UpdateCheck });
            updates.DropDownItems.Add("Open releases page", null, (_, _) => Updater.OpenReleasesPage());
            menu.Items.Add(updates);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Later(Exit));
    }

    // Let the menu close before opening dialogs.
    void Later(Action action) => widget.BeginInvoke(action);

    IWin32Window? Owner => widget.Visible ? widget : null;

    void OnItemFinished(MediaCard item)
    {
        if (widget.Visible) return;
        if (item.State == ItemState.Done) Notify("Ready", $"{item.Title}\n{Fmt.Size(item.FileSize)}");
        else Notify("Failed", $"{item.Title}\n{item.Error}");
    }

    void Notify(string title, string text, Action? onClick = null)
    {
        balloonAction = onClick;
        tray.ShowBalloonTip(4000, title, text.Length > 200 ? text[..200] : text, ToolTipIcon.None);
    }

    void ChangeHotkey()
    {
        widget.UnregisterHotkey(); // so pressing the current combo gets recorded, not triggered
        using var dialog = new HotkeyForm(settings.Hotkey);
        var result = Owner != null ? dialog.ShowDialog(Owner) : dialog.ShowDialog();
        if (result == DialogResult.OK && dialog.Result != settings.Hotkey)
        {
            if (widget.TrySetHotkey(dialog.Result))
            {
                settings.Hotkey = dialog.Result;
                settings.Save();
            }
            else
            {
                MessageBox.Show(Owner, $"{dialog.Result} is already used by another app. Pick a different shortcut.",
                    Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                widget.TrySetHotkey(settings.Hotkey);
            }
        }
        else
        {
            widget.TrySetHotkey(settings.Hotkey);
        }
        tray.Text = TrayText;
    }

    void ChangeFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Where should downloaded videos be saved?",
            UseDescriptionForTitle = true,
            SelectedPath = settings.Folder,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(Owner) != DialogResult.OK) return;
        settings.Folder = dialog.SelectedPath;
        settings.Save();
    }

    void ToggleStartup()
    {
        try { Startup.Set(!Startup.IsEnabled); }
        catch (Exception ex)
        {
            MessageBox.Show(Owner, "Couldn't change the startup setting: " + ex.Message, Brand.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void UpdateYtDlp()
    {
        var exe = Tools.Find("yt-dlp.exe");
        if (exe == null)
        {
            MessageBox.Show(Owner, "yt-dlp isn't installed. Install it with:\n\nwinget install yt-dlp.yt-dlp", Brand.Name);
            return;
        }
        Process.Start(new ProcessStartInfo("cmd.exe", $"/s /c \"\"{exe}\" -U & echo. & pause\"") { UseShellExecute = true });
    }

    void Exit()
    {
        int active = widget.ActiveCount;
        if (active > 0 && MessageBox.Show(Owner, $"{active} download(s) still running. Cancel them and exit?", Brand.Name,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        Log.Write("Exit from tray menu");
        widget.KillAll();
        widget.UnregisterHotkey();
        tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            tray.Dispose();
            widget.Dispose();
            menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
