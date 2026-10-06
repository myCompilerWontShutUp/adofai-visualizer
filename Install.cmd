<# :
@echo off
setlocal
rem Double-click to install the ADOFAI Visualizer. This file is read twice: cmd runs these lines, which hand the whole
rem file to PowerShell, and PowerShell skips them as a comment and runs the rest. Nothing has to be typed anywhere.
set "VISUALIZER_SETUP=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([ScriptBlock]::Create([IO.File]::ReadAllText($env:VISUALIZER_SETUP)))"
set "code=%errorlevel%"
echo.
pause
exit /b %code%
#>

# ---- the PowerShell part ---------------------------------------------------------------------------------------------
# Gets the visualizer onto this computer the same way every copy is kept up to date: git takes the repository and
# build.ps1 builds it, so the console's update keeps working afterwards. What it may add outside the copy is asked for
# first: git and the .NET SDK (through winget, when they are missing) and the Start menu and desktop shortcuts.
# uninstall.ps1 (Uninstall.cmd) takes the copy and its shortcuts away again; git and the SDK stay, as other programs may
# use them.
$ErrorActionPreference = 'Continue'
$repository = 'https://github.com/myCompilerWontShutUp/adofai-visualizer.git'
$name = 'ADOFAI Visualizer'
$here = Split-Path -Parent $env:VISUALIZER_SETUP

function Fail([string]$why) { Write-Host ''; Write-Host $why; exit 1 }
function Ask([string]$question, [bool]$yes) {
    $answer = Read-Host ('{0} [{1}]' -f $question, $(if ($yes) { 'Y/n' } else { 'y/N' }))
    if (-not $answer -or -not $answer.Trim()) { return $yes }
    return ($answer.Trim() -match '^(?i)(y|yes)$')
}
# A program winget just installed is not on this window's PATH until it is read again.
function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}
function IsCopy([string]$folder) {
    (Test-Path -LiteralPath (Join-Path $folder 'AGENTS.md')) -and (Test-Path -LiteralPath (Join-Path $folder 'Visualizer.sln'))
}
# global.json asks for 9.0.305 or a later 9.0 SDK.
function HasSdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    foreach ($line in @(dotnet --list-sdks 2>$null)) {
        if ($line -match '^(\d+)\.(\d+)\.(\d+)\s') {
            $version = [version]('{0}.{1}.{2}' -f $Matches[1], $Matches[2], $Matches[3])
            if ($version -ge [version]'9.0.305' -and $version -lt [version]'9.1') { return $true }
        }
    }
    return $false
}
function Get-Tool([string]$what, [string]$id, [scriptblock]$present, [string]$page) {
    if (& $present) { return }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Fail "$what is needed and is not on this computer, and winget is not there to install it. Install it from $page, then run Install.cmd again."
    }
    if (-not (Ask "$what is needed and is not on this computer. Install it with winget (Windows may ask for permission)?" $true)) {
        Fail 'Nothing was installed.'
    }
    winget install --id $id --exact --source winget --accept-package-agreements --accept-source-agreements
    Refresh-Path
    if (-not (& $present)) { Fail "$what was installed but this window cannot see it yet. Close this window and run Install.cmd again." }
}

Write-Host "$name setup"
Write-Host ''
Get-Tool 'git' 'Git.Git' { [bool](Get-Command git -ErrorAction SilentlyContinue) } 'https://git-scm.com/download/win'
Get-Tool 'The .NET 9 SDK' 'Microsoft.DotNet.SDK.9' { HasSdk } 'https://dotnet.microsoft.com/download/dotnet/9.0'

# ---- where ---------------------------------------------------------------------------------------------------------
if (IsCopy $here) {
    $folder = $here
    Write-Host "This copy is used as it is: $folder"
}
else {
    # Not under Documents: it is often synced by OneDrive, and the build caches do not belong there.
    $default = Join-Path $env:USERPROFILE 'AdofaiVisualizer'
    $typed = Read-Host "Folder to install into (Enter for $default)"
    $folder = if ($typed -and $typed.Trim()) { [IO.Path]::GetFullPath($typed.Trim().Trim('"')) } else { $default }
    if (IsCopy $folder) { Write-Host "A copy is already there and is used as it is." }
    elseif ((Test-Path -LiteralPath $folder) -and @(Get-ChildItem -LiteralPath $folder -Force).Count -gt 0) {
        Fail "$folder already holds other files, so nothing was put there. Choose an empty or new folder."
    }
    else {
        Write-Host 'Downloading. The first time, a GitHub sign-in window may open (the repository is private).'
        git clone $repository $folder
        if ($LASTEXITCODE -ne 0 -or -not (IsCopy $folder)) {
            Fail 'It could not be downloaded. Check the network and the GitHub sign-in, then run Install.cmd again.'
        }
    }
}

# ---- build ---------------------------------------------------------------------------------------------------------
Write-Host 'Building. The first time takes a minute or two.'
$log = Join-Path $folder '.local\install-build.log'
New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
$built = $true
try { & (Join-Path $folder 'build.ps1') -Configuration Release *> $log } catch { $built = $false; Add-Content -LiteralPath $log -Value $_ }
$exe = Join-Path $folder 'src\Visualizer.App\bin\Release\net9.0-windows\Visualizer.App.exe'
if (-not $built -or -not (Test-Path -LiteralPath $exe)) {
    Get-Content -LiteralPath $log -Tail 15 | ForEach-Object { Write-Host "  $_" }
    Fail "The build failed; the whole log is in $log."
}
Write-Host 'Built.'

# ---- shortcuts -----------------------------------------------------------------------------------------------------
$shell = New-Object -ComObject WScript.Shell
function Shortcut([string]$path, [string]$target, [string]$workingDirectory) {
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $target
    $link.WorkingDirectory = $workingDirectory
    $link.IconLocation = "$exe,0"
    $link.Save()
}
if (Ask 'Add it to the Start menu?' $true) {
    $menu = Join-Path ([Environment]::GetFolderPath('Programs')) $name
    New-Item -ItemType Directory -Force -Path $menu | Out-Null
    Shortcut (Join-Path $menu "$name.lnk") $exe $folder
    # Uninstall.cmd steps out of the folder itself; starting it from outside keeps its window from holding the folder.
    Shortcut (Join-Path $menu "Uninstall $name.lnk") (Join-Path $folder 'Uninstall.cmd') (Split-Path $folder)
}
if (Ask 'Add a shortcut on the desktop?' $false) {
    Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) "$name.lnk") $exe $folder
}

Write-Host ''
Write-Host "Installed in $folder."
Write-Host '  Updates:   in the app, open the console (Ctrl+T) and type update.'
Write-Host '  Game link: in the same console, type install linker.'
Write-Host "  Removing:  Uninstall $name in the Start menu, or Uninstall.cmd in that folder."
if (Ask 'Start it now?' $true) { Start-Process -FilePath $exe -WorkingDirectory $folder }
exit 0
