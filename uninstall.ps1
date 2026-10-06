<#
.SYNOPSIS
    Takes the visualizer off this computer: the plugins it put into the game, its data, and this copy itself.

.DESCRIPTION
    Double-click Uninstall.cmd (or "Uninstall ADOFAI Visualizer" in the Start menu), or run this in PowerShell, with the
    visualizer closed. The visualizer's own console never runs it (user rule): the app holds its own files while it runs,
    and taking it apart is not a thing to do from inside it.

    Three parts, all shown first and then asked one at a time. Only y removes; Enter alone keeps that part.

      plugins  Every plugin the app installed, removed folder and all from where it went. Today that is the linker, the
               game mod in <game>\Mods\AdofaiVisualizerSync, together with the copy Unity Mod Manager keeps beside it.
               Unity Mod Manager's own Params.xml is not touched: it rewrites its list of mods from the mods it loaded
               every time the game closes (UnityModManager.Param.Save clears ModParams and refills it from modEntries),
               so the linker's line leaves by itself the next time the game is closed.
      data     The songs copied from the workshop, edits, settings and notes: %LOCALAPPDATA%\AdofaiVisualizer, the app's
               own files in a data folder chosen in the settings (anything else there stays), and this copy's older
               .local\app-data, which the app would otherwise copy back the next time it starts.
      app      This folder: the source, the builds and their caches, and the shortcuts that open it (the Start menu
               folder and the desktop shortcut Install.cmd made). git and the .NET SDK stay: other programs may use them.

    Steam's workshop maps, other mods and the game's own files are never touched. No link (junction or symlink) is
    followed: a link is removed as a link, never what it points to.

.PARAMETER Part
    Which parts to offer: plugins, data, app. All three by default.

.PARAMETER GameDir
    The game folder, when Steam does not know it.

.PARAMETER Plan
    Only shows what would go. Changes nothing.

.PARAMETER Force
    Offers to remove this copy even when it holds work of its own (changed files, or commits the repository does not
    have) or when that cannot be checked. It still asks.

.EXAMPLE
    .\uninstall.ps1 -Plan                  # what would go
    .\uninstall.ps1                        # asks for each part
    .\uninstall.ps1 -Part plugins          # only the plugins
#>
param(
    [ValidateSet('plugins', 'data', 'app')]
    [string[]]$Part = @('plugins', 'data', 'app'),
    [string]$GameDir,
    [switch]$Plan,
    [switch]$Force
)
# Every git call is checked by its exit code; 'Stop' would turn git's own stderr into a terminating error. Deleting is
# done through .NET calls inside try/catch, which stop on their own.
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$gameFolderName = 'A Dance of Fire and Ice'
# What the app writes into a data folder. In a folder the user chose only these go; anything else there is the user's.
$storeEntries = @('settings.json', 'data-root.txt', 'last-version.txt', 'songs.json', 'song-ids.json', 'song-marks.json',
    'pins.json', 'map-cache', 'maps', 'library', 'reports')
$storeTemps = @('settings-*.tmp', 'songs-*.tmp', 'song-ids-*.tmp', 'song-marks-*.tmp', 'pins-*.tmp')
# The linker's mod id, for a copy whose plugins folder is gone.
$knownPlugins = @('AdofaiVisualizerSync')
# The Start menu folder Install.cmd makes.
$menuName = 'ADOFAI Visualizer'

function Attributes([string]$path) { try { [IO.File]::GetAttributes($path) } catch { $null } }

# Deletes a file, or a folder and everything in it. A link is deleted as a link and never entered, so nothing outside
# can go. What cannot be deleted (in use, no right) is added to $failed and the rest still goes.
function Remove-Tree([string]$path, [System.Collections.Generic.List[string]]$failed) {
    $attributes = Attributes $path
    if ($null -eq $attributes) { return }
    $folder = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
    try {
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            if ($folder) { [IO.Directory]::Delete($path, $false) } else { [IO.File]::Delete($path) }
            return
        }
        if ($folder) {
            foreach ($child in [IO.Directory]::GetFileSystemEntries($path)) { Remove-Tree $child $failed }
            [IO.Directory]::Delete($path, $false)
        }
        else {
            if (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) { [IO.File]::SetAttributes($path, [IO.FileAttributes]::Normal) }
            [IO.File]::Delete($path)
        }
    }
    catch { $failed.Add($path) }
}

# Files under a path, counted the way Remove-Tree walks (links count as one and are not entered).
function Measure-Tree([string]$path) {
    $attributes = Attributes $path
    if ($null -eq $attributes) { return 0 }
    if (($attributes -band [IO.FileAttributes]::Directory) -eq 0 -or ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return 1 }
    $count = 0
    try { foreach ($child in [IO.Directory]::GetFileSystemEntries($path)) { $count += Measure-Tree $child } } catch { }
    $count
}

# The app's own files in a data folder: its entries and its half-written temp files. The folder goes too once empty.
function Remove-StoreFiles([string]$store, [System.Collections.Generic.List[string]]$failed) {
    if ($null -eq (Attributes $store)) { return }
    foreach ($name in $storeEntries) { Remove-Tree ([IO.Path]::Combine($store, $name)) $failed }
    foreach ($pattern in $storeTemps) {
        foreach ($temp in [IO.Directory]::GetFiles($store, $pattern)) { Remove-Tree $temp $failed }
    }
    if ([IO.Directory]::GetFileSystemEntries($store).Count -eq 0) { Remove-Tree $store $failed }
}

# The app's own entries present in a folder the user chose.
function StoreFiles([string]$store) {
    if ($null -eq (Attributes $store)) { return @() }
    @($storeEntries | Where-Object { $null -ne (Attributes ([IO.Path]::Combine($store, $_))) }) +
        @($storeTemps | ForEach-Object { [IO.Directory]::GetFiles($store, $_) } | ForEach-Object { Split-Path $_ -Leaf })
}

# The mod ids of the plugins this copy ships: every plugins\<name>\Info.json names the Mods folder it goes into.
function PluginIds {
    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($id in $knownPlugins) { $ids.Add($id) }
    $folder = Join-Path $root 'plugins'
    if ($null -eq (Attributes $folder)) { return $ids }
    foreach ($plugin in [IO.Directory]::GetDirectories($folder)) {
        $info = Join-Path $plugin 'Info.json'
        if ($null -eq (Attributes $info)) { continue }
        try { $id = ([IO.File]::ReadAllText($info) | ConvertFrom-Json).Id } catch { continue }
        # A plain name only: it is joined under the game's Mods folder.
        if ($id -is [string] -and $id -match '^[A-Za-z0-9_-][A-Za-z0-9_.-]*$' -and -not $ids.Contains($id)) { $ids.Add($id) }
    }
    $ids
}

# Game folders to look in: the one given, every Steam library Steam lists, and the one the map folder in the settings
# sits in (the same way the app finds it).
function GameFolders([string]$mapRoot) {
    if ($GameDir) { return @([IO.Path]::GetFullPath($GameDir)) }
    $libraries = New-Object System.Collections.Generic.List[string]
    $steam = $null
    try { $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath } catch { }
    foreach ($steamHome in @($steam, 'C:\Program Files (x86)\Steam')) {
        if (-not $steamHome) { continue }
        $steamHome = $steamHome -replace '/', '\'
        $libraries.Add($steamHome)
        $list = [IO.Path]::Combine($steamHome, 'steamapps', 'libraryfolders.vdf')
        if ($null -ne (Attributes $list)) {
            foreach ($match in [regex]::Matches([IO.File]::ReadAllText($list), '"path"\s+"([^"]+)"')) { $libraries.Add(($match.Groups[1].Value -replace '\\\\', '\')) }
        }
    }
    for ($dir = $mapRoot; $dir; $dir = Split-Path $dir -Parent) {
        if ((Split-Path $dir -Leaf) -eq 'steamapps') { $libraries.Add((Split-Path $dir -Parent)); break }
    }
    # [IO.Path]::Combine, not Join-Path: a library on a drive that is not there makes Join-Path fail.
    $libraries | ForEach-Object { [IO.Path]::GetFullPath([IO.Path]::Combine($_, 'steamapps', 'common', $gameFolderName)).TrimEnd('\') } |
        Where-Object { $null -ne (Attributes $_) } | Sort-Object -Unique
}

# An installed plugin folder that is ours: named after one of our ids, and its Info.json (when there) says so.
function InstalledPlugins([string[]]$games, [string[]]$ids) {
    foreach ($game in $games) {
        foreach ($id in $ids) {
            $folder = [IO.Path]::Combine($game, 'Mods', $id)
            if ($null -eq (Attributes $folder)) { continue }
            $info = [IO.Path]::Combine($folder, 'Info.json')
            $version = ''
            if ($null -ne (Attributes $info)) {
                try { $manifest = [IO.File]::ReadAllText($info) | ConvertFrom-Json } catch { $manifest = $null }
                if ($manifest -and $manifest.Id -ne $id) { continue }
                if ($manifest -and $manifest.Version) { $version = " $($manifest.Version)" }
            }
            [pscustomobject]@{ Id = $id; Folder = $folder; Version = $version }
        }
    }
}

# Whether this copy holds work the repository does not: changed or new files, or commits never pushed.
function OwnWork {
    if ($null -eq (Attributes (Join-Path $root '.git')) -or -not (Get-Command git -ErrorAction SilentlyContinue)) { return 'cannot be checked (no git here)' }
    Push-Location -LiteralPath $root
    try {
        $changed = @(git status --porcelain=v1 --untracked-files=all 2>$null).Count
        if ($LASTEXITCODE -ne 0) { return 'cannot be checked (git could not read it)' }
        $upstream = git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null
        $range = if ($LASTEXITCODE -eq 0 -and $upstream) { "$upstream..HEAD" } else { 'HEAD' }
        $ahead = [int](git rev-list --count $range 2>$null)
        $parts = @()
        if ($changed -gt 0) { $parts += "$changed changed file(s)" }
        if ($ahead -gt 0) { $parts += "$ahead commit(s) the repository does not have" }
        if ($parts.Count -gt 0) { return ($parts -join ', ') }
        return $null
    }
    finally { Pop-Location }
}

# The shortcuts in those folders that open something in this copy: the ones Install.cmd made (Start menu and desktop),
# or any made by hand, which would only point at nothing once the copy is gone.
function OwnShortcuts([string[]]$places) {
    $shell = New-Object -ComObject WScript.Shell
    foreach ($place in $places) {
        if (-not $place -or $null -eq (Attributes $place)) { continue }
        foreach ($link in [IO.Directory]::GetFiles($place, '*.lnk')) {
            try { $target = $shell.CreateShortcut($link).TargetPath } catch { continue }
            if ($target -and $target.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $link }
        }
    }
}

# Removes those shortcuts, and the Start menu folder Install.cmd made once nothing is left in it.
function Remove-Shortcuts([string[]]$links, [System.Collections.Generic.List[string]]$failed) {
    foreach ($link in $links) {
        Remove-Tree $link $failed
        $folder = Split-Path $link -Parent
        if ((Split-Path $folder -Leaf) -eq $menuName -and $null -ne (Attributes $folder) -and [IO.Directory]::GetFileSystemEntries($folder).Count -eq 0) {
            Remove-Tree $folder $failed
        }
    }
}

function Ask([string]$question) {
    $answer = Read-Host "$question [y/N]"
    return ($answer -and $answer.Trim() -match '^(?i)(y|yes)$')
}

function Report([string]$what, [System.Collections.Generic.List[string]]$failed) {
    if ($failed.Count -eq 0) { Write-Output "  $what removed."; return }
    Write-Output "  $what removed, except $($failed.Count) item(s) still in use:"
    $failed | Select-Object -First 5 | ForEach-Object { Write-Output "    $_" }
}

# Dot-sourced (the tests do this to try the functions on their own folders): define the functions and stop here.
if ($MyInvocation.InvocationName -eq '.') { return }

# ---- what is here ----------------------------------------------------------------------------------------------------
$isCopy = $null -ne (Attributes (Join-Path $root 'AGENTS.md')) -and $null -ne (Attributes (Join-Path $root 'Visualizer.sln'))
$shared = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AdofaiVisualizer'
$pointer = Join-Path $shared 'data-root.txt'
$chosen = if ($null -ne (Attributes $pointer)) { [IO.File]::ReadAllText($pointer).Trim() } else { '' }
if ($chosen -and -not [IO.Path]::IsPathRooted($chosen)) { $chosen = '' }
$settingsFile = [IO.Path]::Combine($(if ($chosen) { $chosen } else { $shared }), 'settings.json')
$mapRoot = ''
if ($null -ne (Attributes $settingsFile)) {
    try { $mapRoot = [string]([IO.File]::ReadAllText($settingsFile) | ConvertFrom-Json).MapRootDirectory } catch { }
}
$old = Join-Path $root '.local\app-data'

$plugins = @(InstalledPlugins @(GameFolders $mapRoot) @(PluginIds))
$stores = @()
if ($null -ne (Attributes $shared)) { $stores += [pscustomobject]@{ Path = $shared; Whole = $true; Note = "$(Measure-Tree $shared) file(s)" } }
if ($null -ne (Attributes "$shared.moving")) { $stores += [pscustomobject]@{ Path = "$shared.moving"; Whole = $true; Note = 'left from an unfinished move' } }
if ($chosen -and @(StoreFiles $chosen).Count -gt 0) {
    $stores += [pscustomobject]@{ Path = $chosen; Whole = $false; Note = "the data folder chosen in the settings: only $(@(StoreFiles $chosen) -join ', ')" }
}
if ($isCopy -and $null -ne (Attributes $old)) { $stores += [pscustomobject]@{ Path = $old; Whole = $true; Note = 'the older store of this copy' } }
$work = if ($isCopy) { OwnWork } else { $null }
$shortcuts = if ($isCopy) {
    @(OwnShortcuts @([IO.Path]::Combine([Environment]::GetFolderPath('Programs'), $menuName), [Environment]::GetFolderPath('Desktop')))
} else { @() }

Write-Output 'The visualizer on this computer:'
if ($Part -contains 'plugins') {
    if ($plugins.Count -eq 0) { Write-Output '  plugins  none installed' }
    foreach ($plugin in $plugins) { Write-Output ("  plugins  {0}{1} in {2} ({3} file(s))" -f $plugin.Id, $plugin.Version, $plugin.Folder, (Measure-Tree $plugin.Folder)) }
}
if ($Part -contains 'data') {
    if ($stores.Count -eq 0) { Write-Output '  data     none' }
    foreach ($store in $stores) { Write-Output ("  data     {0} ({1})" -f $store.Path, $store.Note) }
}
if ($Part -contains 'app') {
    if (-not $isCopy) { Write-Output "  app      $root is not a copy of the visualizer, so it is not offered" }
    else {
        # Not counted: the build caches alone hold tens of thousands of files.
        Write-Output "  app      $root (this copy, with its builds and caches)"
        foreach ($link in $shortcuts) { Write-Output "           shortcut $link" }
        if ($work) { Write-Output "           it holds work of its own: $work$(if (-not $Force) { ' - kept unless -Force' })" }
    }
}
if ($Plan) { Write-Output 'Nothing was removed (-Plan).'; exit 0 }

# ---- what stands in the way ------------------------------------------------------------------------------------------
# A running app holds its files and writes its data; a running game holds the linker it loaded.
if (@(Get-Process -Name 'Visualizer.App' -ErrorAction SilentlyContinue).Count -gt 0 -and ($Part -contains 'data' -or $Part -contains 'app')) {
    Write-Output 'The visualizer is running. Close it first; nothing was removed.'
    exit 1
}
if (@(Get-Process -Name $gameFolderName -ErrorAction SilentlyContinue).Count -gt 0 -and $Part -contains 'plugins' -and $plugins.Count -gt 0) {
    Write-Output 'The game is running and has the plugins loaded. Close it first; nothing was removed.'
    exit 1
}

Write-Output 'Only y removes a part; Enter alone keeps it.'
$problems = 0

# ---- plugins (first: the data part removes the settings that say where the game is) ---------------------------------
if ($Part -contains 'plugins') {
    foreach ($plugin in $plugins) {
        if (-not (Ask "Remove the plugin $($plugin.Id) from $($plugin.Folder)?")) { Write-Output '  Kept.'; continue }
        $failed = New-Object System.Collections.Generic.List[string]
        Remove-Tree $plugin.Folder $failed
        Report $plugin.Id $failed
        if ($failed.Count -gt 0) { $problems++ }
        else { Write-Output "  Unity Mod Manager drops it from its own list the next time the game is closed." }
    }
}

# ---- data -----------------------------------------------------------------------------------------------------------
if ($Part -contains 'data' -and $stores.Count -gt 0) {
    if (Ask 'Remove your data: the copied songs, edits, settings and notes? This cannot be undone.') {
        foreach ($store in $stores) {
            $failed = New-Object System.Collections.Generic.List[string]
            if ($store.Whole) { Remove-Tree $store.Path $failed } else { Remove-StoreFiles $store.Path $failed }
            Report $store.Path $failed
            if ($failed.Count -gt 0) { $problems++ }
        }
        Write-Output '  Another copy of the visualizer on this computer that still has its own .local\app-data brings that back when it starts.'
    }
    else { Write-Output '  Kept.' }
}

# ---- this copy ------------------------------------------------------------------------------------------------------
if ($Part -contains 'app' -and $isCopy) {
    if ($work -and -not $Force) {
        Write-Output "  This copy holds work of its own ($work), so it was kept. -Force offers to remove it anyway."
    }
    elseif (Ask "Remove this copy of the visualizer, $root$(if ($work) { ', and the work of its own with it' })?") {
        # Step out of the folder, this session and the process alike, so neither holds it open.
        $parent = Split-Path $root -Parent
        Set-Location -LiteralPath $parent
        [Environment]::CurrentDirectory = $parent
        $failed = New-Object System.Collections.Generic.List[string]
        Remove-Shortcuts $shortcuts $failed
        if ($shortcuts.Count -gt 0) { Report "$($shortcuts.Count) shortcut(s)" $failed; $failed.Clear() }
        Remove-Tree $root $failed
        if ($failed.Count -eq 1 -and $failed[0] -eq $root -and [IO.Directory]::GetFileSystemEntries($root).Count -eq 0) {
            Write-Output "  Everything in it is gone; the empty folder is still open in another window (a terminal or an editor). Close that and delete $root."
        }
        else {
            Report $root $failed
            if ($failed.Count -gt 0) { $problems++ }
        }
        if ($Part -notcontains 'data' -and $stores.Count -gt 0) { Write-Output "  Your data is still in $($stores[0].Path)." }
        Write-Output '  git and the .NET SDK stay, since other programs may use them; Windows Settings > Apps removes them.'
    }
    else { Write-Output '  Kept.' }
}
exit ([int]($problems -gt 0))
