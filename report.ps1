<#
.SYNOPSIS
    The notes the user leaves for agents, from the terminal.

.DESCRIPTION
    The console's `report` editor writes notes into the app store so that problems found while no agent is around are not
    lost. An agent cannot use the app's console, so this is the same thing from the command line, and it is the one way
    the done mark is set: run it rather than editing the files, so every agent leaves the mark in the same shape.

    The mark lives beside the note (reports/done.mark), never inside it, so the user's own words can never be mistaken
    for it. Writing a note again in the app opens it, which is how a note that was done comes back for the next version.

    A note stands in one of three places: open (waiting), done (taken care of, in the version written beside it), and
    asked (looked at but not settled — the reason says what is still needed). The reason is written in the language the
    app's settings are set to, since the user is who reads it; `list` and `open` print which language that is.

.EXAMPLE
    .\report.ps1 list              # every note, open ones first
    .\report.ps1 open              # only the ones still waiting — start here
    .\report.ps1 read "magic circle"
    .\report.ps1 state "magic circle"
    .\report.ps1 done "magic circle"      # after the fix is built and tested
    .\report.ps1 asked "first tile missing error" -Why "어느 곡·슬롯인지 알려 주셔야 재현됩니다."
    .\report.ps1 undone "magic circle"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('list', 'open', 'read', 'state', 'done', 'undone', 'asked')]
    [string]$Action,
    [Parameter(Position = 1)]
    [string]$Title,
    # The version the note was taken care of in. Taken from Directory.Build.props when it is not given.
    [string]$Version,
    # Why it is not settled, for 'asked'. Written in the language the app's settings are set to (user rule).
    [string]$Why
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# The same store the app uses: the one every copy on this PC shares, unless a data root was chosen in the settings.
# Until the app has moved a copy's own .local\app-data there, that older folder is still where the notes are.
$store = Join-Path $env:LOCALAPPDATA 'AdofaiVisualizer'
$old = Join-Path $root '.local\app-data'
if (-not (Test-Path -LiteralPath $store) -and (Test-Path -LiteralPath $old)) { $store = $old }
$pointer = Join-Path $store 'data-root.txt'
if (Test-Path -LiteralPath $pointer) {
    $chosen = (Get-Content -LiteralPath $pointer -Raw).Trim()
    if ($chosen) { $store = $chosen }
}
$reports = Join-Path $store 'reports'
$donePath = Join-Path $reports 'done.mark'
$writtenPath = Join-Path $reports 'written.mark'

# The language the user reads: reasons are written in it (user rule).
function Get-Language {
    $settings = Join-Path $store 'settings.json'
    if (Test-Path -LiteralPath $settings) {
        $match = [regex]::Match((Get-Content -LiteralPath $settings -Raw -Encoding UTF8), '"Language"\s*:\s*"([^"]+)"')
        if ($match.Success) { return $match.Groups[1].Value }
    }
    return 'korean'
}

function Read-Marks([string]$path) {
    $map = @{}
    if (Test-Path -LiteralPath $path) {
        foreach ($line in Get-Content -LiteralPath $path -Encoding UTF8) {
            $tab = $line.IndexOf("`t")
            if ($tab -gt 0) { $map[$line.Substring(0, $tab)] = $line.Substring($tab + 1) }
        }
    }
    return $map
}

function Save-Marks([string]$path, [hashtable]$map) {
    $lines = $map.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Name + "`t" + $_.Value }
    [IO.File]::WriteAllLines($path, [string[]]$lines, [Text.UTF8Encoding]::new($false))
}

function Split-Mark([string]$mark) {
    if (!$mark) { return @{ State = 'open'; Version = ''; Why = '' } }
    $parts = $mark -split "`t"
    if ($parts[0] -notin @('open', 'done', 'asked')) { return @{ State = 'done'; Version = $parts[0]; Why = '' } }
    return @{
        State   = $parts[0]
        Version = if ($parts.Count -gt 1) { $parts[1] } else { '' }
        Why     = if ($parts.Count -gt 2) { $parts[2] } else { '' }
    }
}

function Get-Notes {
    if (!(Test-Path -LiteralPath $reports)) { return @() }
    $done = Read-Marks $donePath
    $written = Read-Marks $writtenPath
    Get-ChildItem -LiteralPath $reports -Filter '*.txt' | ForEach-Object {
        $name = [IO.Path]::GetFileNameWithoutExtension($_.Name)
        $mark = Split-Mark $(if ($done.ContainsKey($name)) { $done[$name] } else { '' })
        [pscustomobject]@{
            Title   = $name
            Written = if ($written.ContainsKey($name)) { $written[$name] } else { '' }
            State   = $mark.State
            Done    = $mark.Version
            Why     = $mark.Why
            Lines   = @(Get-Content -LiteralPath $_.FullName -Encoding UTF8).Count
            Path    = $_.FullName
        }
    }
}

function Find-Note([string]$name) {
    if (!$name) { throw "A note title is needed: .\report.ps1 $Action '<title>'" }
    $note = Get-Notes | Where-Object { $_.Title -ieq $name }
    if (!$note) { throw "There is no note called '$name'. Run .\report.ps1 list to see them." }
    return $note
}

switch ($Action) {
    'list' {
        $notes = Get-Notes | Sort-Object @{ Expression = { $_.State -eq 'done' } }, Title
        if (!$notes) { "No notes are written."; break }
        "Reasons are written in: $(Get-Language)"
        $notes | ForEach-Object {
            $state = if ($_.State -eq 'done') { "done $($_.Done)" } else { $_.State }
            '{0,-34} {1,-8} {2,-12} {3} lines' -f $_.Title, ($(if ($_.Written) { $_.Written } else { '-' })), $state, $_.Lines
            if ($_.Why) { '{0,-34} {1}' -f '', $_.Why }
        }
        "{0} of {1} still open." -f @($notes | Where-Object { $_.State -ne 'done' }).Count, $notes.Count
    }
    'open' {
        $notes = Get-Notes | Where-Object { $_.State -ne 'done' } | Sort-Object Title
        if (!$notes) { "Every note is done."; break }
        "Reasons are written in: $(Get-Language)"
        foreach ($note in $notes) {
            "== $($note.Title)  [$($note.State)]  (written for $(if ($note.Written) { $note.Written } else { '-' }))"
            if ($note.Why) { "   ? $($note.Why)" }
            Get-Content -LiteralPath $note.Path -Encoding UTF8 | ForEach-Object { "   $_" }
        }
    }
    'read' { Get-Content -LiteralPath (Find-Note $Title).Path -Encoding UTF8 }
    'state' {
        $note = Find-Note $Title
        switch ($note.State) {
            'done' { "done $($note.Done)" }
            'asked' { if ($note.Why) { "asked: $($note.Why)" } else { 'asked' } }
            default { 'open' }
        }
    }
    'asked' {
        $note = Find-Note $Title
        if (!$Why) { throw "Say what is needed, in $(Get-Language): .\report.ps1 asked '<title>' -Why '<reason>'" }
        $done = Read-Marks $donePath
        $done[$note.Title] = "asked`t`t" + ($Why -replace "[`t`r`n]", ' ').Trim()
        Save-Marks $donePath $done
        "'$($note.Title)' is waiting on an answer."
    }
    'done' {
        $note = Find-Note $Title
        if (!$Version) {
            $props = Join-Path $root 'Directory.Build.props'
            if (Test-Path -LiteralPath $props) {
                $match = [regex]::Match((Get-Content -LiteralPath $props -Raw), '<Version>([^<]+)</Version>')
                if ($match.Success) { $Version = $match.Groups[1].Value }
            }
            if (!$Version) { $Version = 'unknown' }
        }
        $done = Read-Marks $donePath
        $done[$note.Title] = "done`t$Version`t"
        Save-Marks $donePath $done
        "'$($note.Title)' is done in $Version."
    }
    'undone' {
        $note = Find-Note $Title
        $done = Read-Marks $donePath
        if (!$done.Remove($note.Title)) { "'$($note.Title)' was already open."; break }
        Save-Marks $donePath $done
        "'$($note.Title)' is open again."
    }
}
