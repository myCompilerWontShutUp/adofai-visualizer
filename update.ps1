<#
.SYNOPSIS
    Keeps this computer's copy on a version of the repository: checks it, takes it, rebuilds, and can check and put back
    the files of the version it is on.

.DESCRIPTION
    Versions are published from one computer with git (an editor such as VS Code does it); every other copy runs this to
    take them. The console's `update`, `scannow` and `install -reinstall` run this very script, so the terminal and the
    app do exactly the same thing.

    Only the repository is touched. The app store (songs, settings and notes) lives outside it, under
    %LOCALAPPDATA%\AdofaiVisualizer, so an update never changes them.

    The app cannot rebuild itself while it is running (its own Release folder is locked), so the app hands this script
    its process id: the script waits for the app to close, builds, and starts the new build.

    A version is found by the commit that set it in Directory.Build.props, so no tags are needed: a version's last
    commit is the one just before the next version was set.

.PARAMETER Check
    Only looks: what this copy is, what the target is, and what stands in the way. Changes nothing.

.PARAMETER Version
    The version to go to: latest (the default) or one written as 1.0.0. Another version than the latest needs -Force.

.PARAMETER Force
    Goes to the target even when this copy has changes or commits of its own; they are thrown away.

.PARAMETER Reinstall
    Puts parts of the version this copy is on back, with no download: source (the repository's files), app (the build)
    and linker (the game mod, which needs -LinkerGame).

.PARAMETER Scan
    Lists the files that differ from the version this copy is on. Changes nothing.

.PARAMETER LinkerGame
    The game folder whose linker is built and written again after the app is built (an installed linker follows updates).

.PARAMETER NoBuild
    Takes the version without building, for a computer that builds by hand.

.PARAMETER WaitForProcessId
    Waits for that process to end before changing anything, which is how the app updates itself.

.PARAMETER Restart
    Starts the app again once the work is done.

.PARAMETER Progress
    For the app: prints `step <0..1>` lines for its progress bar and `state`/`scan` lines it reads, instead of sentences.

.EXAMPLE
    .\update.ps1 -Check                         # am I on the latest version?
    .\update.ps1                                # take it and rebuild
    .\update.ps1 -Force -Version 1.0.0          # go to 1.0.0, throwing away this copy's own changes
    .\update.ps1 -Reinstall source,app          # put this version's files back and build it again
#>
param(
    [switch]$Check,
    [string]$Version = 'latest',
    [switch]$Force,
    [string[]]$Reinstall,
    [switch]$Scan,
    [string]$LinkerGame,
    [switch]$NoBuild,
    [int]$WaitForProcessId,
    [switch]$Restart,
    [switch]$Progress
)
# Every git call is checked by its exit code; 'Stop' would turn git's own stderr into a terminating error.
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot

function Step([double]$fraction) { if ($Progress) { Write-Output ("step {0}" -f $fraction) } }
# A line for the app, or the same thing as a sentence for a person.
function Tell([string]$state, [string]$sentence) { if ($Progress) { Write-Output "state $state" } elseif ($sentence) { Write-Output $sentence } }
function Fail([string]$why) { Write-Output $why; exit 1 }
function VersionIn([string]$text) { if ($text -match '<Version>([^<]+)</Version>') { $Matches[1] } else { '?' } }
function VersionAt([string]$commit) { VersionIn ((git show ("{0}:Directory.Build.props" -f $commit)) -join "`n") }

# The last commit of a version: the parent of the commit that set the next one, or the tip when it is the newest.
function Find-Version([string]$wanted, [string]$tip) {
    $newer = $null
    foreach ($commit in @(git log --format=%H -G '<Version>' $tip -- Directory.Build.props)) {
        if ((VersionAt $commit) -eq $wanted) {
            if ($newer) { return (git rev-parse ("{0}^" -f $newer)) }
            return (git rev-parse $tip)
        }
        $newer = $commit
    }
    return $null
}

function Build-App {
    if ($NoBuild) { Write-Output 'Not built, as asked. Run build.ps1 when you want it.'; return }
    $build = Join-Path $root 'build.ps1'
    if (-not (Test-Path $build)) { Write-Output 'There is no build.ps1 here, so nothing was built.'; return }
    try { & $build -Configuration Release | Out-Null } catch { Fail ('The version is in place but did not build: {0}' -f $_.Exception.Message) }
    if ($LASTEXITCODE -ne 0) { Fail 'The version is in place but did not build. Run build.ps1 -Configuration Release to see why.' }
    Write-Output 'Built.'
}

# The linker follows the app (user rule): built from this version and written into the game's own mod folder.
function Install-Linker {
    if (-not $LinkerGame) { return }
    try {
        & (Join-Path $root 'build-mod.ps1') -Configuration Release -GameDir $LinkerGame -Install | Out-Null
        Write-Output 'The linker is built from this version and written into the game again.'
    }
    catch { Write-Output ('The linker could not be updated ({0}). Close the game and run install -reinstall linker.' -f $_.Exception.Message) }
}

function Wait-App {
    if ($WaitForProcessId -gt 0) {
        # The app's own Release folder is locked while it runs, so wait for it to close before building.
        try { Wait-Process -Id $WaitForProcessId -Timeout 120 -ErrorAction Stop } catch { }
    }
}

function Start-App {
    $app = Join-Path $root 'src\Visualizer.App\bin\Release\net9.0-windows\Visualizer.App.exe'
    if ($Restart -and (Test-Path $app)) { Start-Process -FilePath $app -WorkingDirectory $root }
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git was not found on this computer.' }
if (-not (Test-Path (Join-Path $root '.git'))) { Fail 'This copy did not come from the repository, so there is nothing to update.' }

Push-Location $root
try {
    # An empty repository has no HEAD yet; that says nothing a person needs to read.
    $here = (git rev-parse --short HEAD 2>$null)
    $mine = VersionIn (Get-Content (Join-Path $root 'Directory.Build.props') -Raw)

    if ($Scan) {
        # What differs from the commit this copy is on; nothing is fetched or changed.
        Tell "mine $mine $here" ("This copy is {0} ({1})." -f $mine, $here)
        $lines = @(git status --porcelain=v1 --untracked-files=all)
        if ($LASTEXITCODE -ne 0) { Fail 'The repository could not be read.' }
        foreach ($line in $lines) {
            $code = $line.Substring(0, 2); $path = $line.Substring(3)
            $kind = if ($code -eq '??') { 'extra' } elseif ($code.Contains('D')) { 'missing' } else { 'changed' }
            if ($Progress) { Write-Output ("scan {0} {1}" -f $kind, $path) } else { Write-Output ("{0,-8} {1}" -f $kind, $path) }
        }
        if (-not $Progress -and $lines.Count -eq 0) { Write-Output 'Every file is as this version has it.' }
        exit 0
    }

    if ($Reinstall) {
        $parts = @($Reinstall | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
        Wait-App
        Step 0.2
        if ($parts -contains 'source') {
            # Back to the files of the commit this copy is on. Ignored folders (.local, bin, obj) are left alone.
            git reset --hard --quiet HEAD
            if ($LASTEXITCODE -ne 0) { Fail 'The files could not be put back.' }
            git clean -fd --quiet
            Write-Output ('The files of {0} ({1}) are back.' -f $mine, $here)
        }
        Step 0.4
        if ($parts -contains 'source' -or $parts -contains 'app') { Build-App }
        Step 0.85
        if ($parts -contains 'linker') { Install-Linker }
        Start-App
        Step 1
        exit 0
    }

    $branch = (git rev-parse --abbrev-ref HEAD)
    if (-not (git remote)) { Fail 'This copy has no remote, so there is nowhere to update from.' }
    Step 0.15
    git fetch --quiet origin $branch
    if ($LASTEXITCODE -ne 0) { Fail 'The repository could not be reached. Check the network or the sign-in.' }
    Step 0.4

    $tip = "origin/$branch"
    if ($Version -eq 'latest') { $target = (git rev-parse $tip); $wanted = VersionAt $tip }
    else { $target = Find-Version $Version $tip; $wanted = $Version }
    $changed = @(git status --porcelain=v1 --untracked-files=no).Count
    $unpublished = [int](git rev-list --count ("{0}..HEAD" -f $tip))
    Tell "mine $mine $here" $null
    if (-not $target) {
        Tell "missing $wanted" ("There is no version {0} on the repository." -f $wanted)
        if ($Check) { Step 1; exit 0 }
        exit 1
    }
    $behind = [int](git rev-list --count ("HEAD..{0}" -f $target))
    $same = ((git rev-parse HEAD) -eq $target)
    Tell "target $wanted $(git rev-parse --short $target)" $null
    Tell "behind $behind" $null
    Tell "same $([int]$same)" $null
    Tell "changed $changed" $null
    Tell "unpublished $unpublished" $null

    if ($Check) {
        Step 1
        if (-not $Progress) {
            if ($same -and $changed -eq 0) { Write-Output ("This copy is {0} ({1}), the version asked for." -f $mine, $here) }
            elseif ($behind -eq 0 -and $Version -eq 'latest') { Write-Output ("This copy is already the latest version ({0}, {1})." -f $mine, $here) }
            else { Write-Output ("{0} is there to take; this copy is {1}." -f $wanted, $mine) }
            if ($changed -gt 0) { Write-Output ("This copy has {0} changed file(s) of its own." -f $changed) }
            if ($unpublished -gt 0) { Write-Output ("This copy has {0} commit(s) the repository does not." -f $unpublished) }
        }
        exit 0
    }

    if (-not $Force) {
        if ($Version -ne 'latest') { Fail ('Going to a chosen version needs -Force: .\update.ps1 -Force -Version {0}' -f $Version) }
        if ($behind -eq 0) { Write-Output ("This copy is already the latest version ({0})." -f $mine); exit 0 }
        # Local work would be lost or would stop the merge, so say so instead of guessing.
        if ($changed -gt 0) { Fail 'This copy has changes of its own, so it was not updated. -Force takes the update and throws them away.' }
        if ($unpublished -gt 0) { Fail ('This copy has {0} commit(s) the repository does not, so it cannot fast-forward. -Force throws them away.' -f $unpublished) }
    }

    Wait-App
    Step 0.55
    if ($Force) {
        # The branch itself moves, so a later plain update fast-forwards from here again.
        git reset --hard --quiet $target
        if ($LASTEXITCODE -ne 0) { Fail 'The version could not be put in place; nothing was changed.' }
        git clean -fd --quiet
    }
    else {
        git merge --ff-only --quiet $target
        if ($LASTEXITCODE -ne 0) { Fail 'The update could not be applied; nothing was changed.' }
    }
    Step 0.7
    Write-Output ("Now on {0} ({1})." -f $wanted, (git rev-parse --short HEAD))
    Build-App
    Step 0.9
    Install-Linker
    Start-App
    Step 1
}
finally { Pop-Location }
