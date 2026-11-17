# RedDrop installer: installs the download tools with winget, copies the app to your user folder,
# adds a Start menu shortcut and (if you want) starts it with Windows. No admin needed except for the
# one-time .NET runtime install, which Windows will ask about.
param([switch]$Uninstall)

$name = 'RedDrop'
$dest = Join-Path $env:LOCALAPPDATA "Programs\$name"
$exe = Join-Path $dest "$name.exe"
$startMenu = [Environment]::GetFolderPath('Programs')
$shortcut = Join-Path $startMenu "$name.lnk"

function Step($text) { Write-Host "`n> $text" -ForegroundColor Cyan }

# Stops only the copy running from this exact file.
function Stop-App($path) {
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $path } | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

function Ask($question) { (Read-Host "`n$question (Y/n)") -notmatch '^[nN]' }

if ($Uninstall) {
    Step "Removing $name"
    if (Test-Path $exe) { Start-Process $exe -ArgumentList '--startup', 'off' -Wait }
    Stop-App $exe
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
    Write-Host "`n$name is removed. Your downloaded videos were not touched." -ForegroundColor Green
    Write-Host 'yt-dlp and ffmpeg are still installed; remove them with:  winget uninstall yt-dlp.yt-dlp yt-dlp.FFmpeg'
    return
}

$source = Join-Path $PSScriptRoot "$name.exe"
if (-not (Test-Path $source)) {
    Write-Host "$name.exe is missing. Unzip the whole folder first, then run `"Install $name`" from inside it." -ForegroundColor Red
    return
}
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Host 'winget is missing. Install "App Installer" from the Microsoft Store, then run this again.' -ForegroundColor Red
    return
}

$wingetArgs = @('--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity', '-e')

Step 'Installing the download tools (yt-dlp and ffmpeg, from their official winget packages)'
winget install --id yt-dlp.yt-dlp @wingetArgs
winget install --id yt-dlp.FFmpeg @wingetArgs

Step 'Checking the .NET 10 Desktop Runtime'
if (Test-Path "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App\10.*") {
    Write-Host 'Already installed.'
} else {
    Write-Host 'Installing it (Windows may ask for permission)...'
    winget install --id Microsoft.DotNet.DesktopRuntime.10 @wingetArgs
}

Step "Copying $name"
Stop-App $exe   # updating an older copy
New-Item -ItemType Directory -Force $dest | Out-Null

# Coming from "YT Drop" (this app's old name)? Keep its settings, then remove it so the two don't clash.
$old = Join-Path $env:LOCALAPPDATA 'Programs\YT Drop'
$oldExe = Join-Path $old 'YTDrop.exe'
if (Test-Path $oldExe) {
    Write-Host 'Replacing YT Drop (the old name of this app). Its settings are kept.'
    Start-Process $oldExe -ArgumentList '--startup', 'off' -Wait
    Stop-App $oldExe
    $oldSettings = Join-Path $old 'settings.ini'
    if ((Test-Path $oldSettings) -and -not (Test-Path (Join-Path $dest 'settings.ini'))) { Copy-Item $oldSettings $dest }
    Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $startMenu 'YT Drop.lnk') -Force -ErrorAction SilentlyContinue
}

Copy-Item $source $exe -Force
Unblock-File $exe   # drop the "downloaded from the internet" mark so Windows doesn't warn on every start

Step 'Adding a Start menu shortcut'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dest
$link.Description = "$name - save a video from a link"
$link.Save()

if (Ask "Start $name automatically when Windows starts, so the shortcut always works?") {
    Start-Process $exe -ArgumentList '--startup', 'on' -Wait
    Write-Host 'It will start with Windows (hidden in the tray). Change this any time from its tray menu.'
}

$updates = Ask "Check GitHub for $name updates once a week? Only the version number is fetched; nothing about you or your files is sent."
Start-Process $exe -ArgumentList '--updates', $(if ($updates) { 'on' } else { 'off' }) -Wait

Start-Process $exe
Write-Host "`nDone! Press Ctrl+Alt+Y anywhere to open $name." -ForegroundColor Green
Write-Host 'Please only download videos you own or have permission to save.'
