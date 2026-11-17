RedDrop - save a video from a link, then drag it anywhere
=========================================================

INSTALL
  1. Right-click the zip -> "Extract All..." (don't run it from inside the zip).
  2. Double-click "Install RedDrop.bat".
     - If Windows says "Windows protected your PC", click "More info" -> "Run anyway".
       (The app isn't signed with a paid certificate; that's all the warning means.)
     - It installs yt-dlp + ffmpeg (the download tools) and the .NET runtime with winget,
       Microsoft's official installer. Windows may ask permission once for .NET.
     - Had "YT Drop" (the old name)? The installer replaces it and keeps your settings.
  3. Press Ctrl+Alt+Y anywhere.

USE
  - Ctrl+Alt+Y opens the panel. Paste a link, press Enter. MP4 or MP3 (audio only).
  - When it's done, drag the file into a chat, Explorer, an editor... or click it to play.
  - Trim: cut a part out (drag the handles, you see the frames, Play clip plays it with sound).
  - Shrink: makes a copy small enough for Discord's upload limit (20 MB free).
  - Files go to Downloads\RedDrop. Clips and shrunk copies never replace the original.
  - Settings (shortcut, folder, size limit, start with Windows, updates): the gear button,
    or right-click the tray icon.
  - Esc hides it. It uses no CPU while hidden.

UNINSTALL
  Double-click "Uninstall RedDrop.bat". Your downloaded files are kept.

Only download videos you own, that are openly licensed (e.g. Creative Commons),
or that you have permission to save. Sites like YouTube only allow downloading
where they offer it or the owner agrees. RedDrop isn't affiliated with any of them.
Free and open source (MIT licence), with no warranty.

Privacy: no tracking. The only network use is yt-dlp downloading your links and the
optional weekly update check (you choose in the installer), which only asks GitHub for
the newest version number.
