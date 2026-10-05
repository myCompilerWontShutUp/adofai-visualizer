# Builds the read-only game sync mod (tools/GameSyncMod). It is separate from build.ps1 because it needs the game's own
# assemblies, which not every machine has, and because build.ps1 must keep working without the game installed.
# -Install copies the result into the game's Mods folder; that is the one write outside this project (AGENTS.md rule 2),
# so it never happens unless asked for.
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$localRoot = Join-Path $projectRoot '.local'
$managed = Join-Path $GameDir 'A Dance of Fire and Ice_Data\Managed'
if (!(Test-Path -LiteralPath (Join-Path $managed 'UnityModManager\UnityModManager.dll'))) {
    throw "Unity Mod Manager was not found under '$managed'. Pass -GameDir with the game folder, or install UMM first."
}
$environment = @{
    DOTNET_CLI_HOME = (Join-Path $localRoot 'dotnet')
    NUGET_PACKAGES = (Join-Path $localRoot 'nuget/packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $localRoot 'nuget/http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $localRoot 'nuget/plugins-cache')
    TEMP = (Join-Path $localRoot 'tmp')
    TMP = (Join-Path $localRoot 'tmp')
    LOCALAPPDATA = (Join-Path $localRoot 'appdata/local')
    APPDATA = (Join-Path $localRoot 'appdata/roaming')
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    MSBUILDDISABLENODEREUSE = '1'
}
$original = @{}
try {
    foreach ($name in $environment.Keys) {
        $original[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    foreach ($name in @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH', 'TEMP', 'LOCALAPPDATA', 'APPDATA')) {
        New-Item -ItemType Directory -Path $environment[$name] -Force | Out-Null
    }
    Push-Location $projectRoot
    try {
        dotnet build .\tools\GameSyncMod\GameSyncMod.csproj -c $Configuration --configfile .\NuGet.Config --disable-build-servers -p:UseSharedCompilation=false -p:NuGetAudit=false "-p:GameManagedDir=$managed"
        if ($LASTEXITCODE -ne 0) { throw "Build failed: exit code $LASTEXITCODE" }
        $output = Join-Path $projectRoot "tools/GameSyncMod/bin/$Configuration"
        Write-Host "Mod built in $output"
        if ($Install) {
            $target = Join-Path $GameDir 'Mods\AdofaiVisualizerSync'
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            # Written one file at a time (not Copy-Item on the folder) so nothing else in the game folder is touched.
            foreach ($file in @('AdofaiVisualizerSync.dll', 'Info.json')) {
                $from = Join-Path $output $file
                if (!(Test-Path -LiteralPath $from)) { throw "Missing build output: $from" }
                [IO.File]::WriteAllBytes((Join-Path $target $file), [IO.File]::ReadAllBytes($from))
            }
            Write-Host "Installed into $target. Restart the game, then switch the mod on in Unity Mod Manager (Ctrl+F10)."
        }
        else {
            Write-Host 'Not installed. Re-run with -Install to copy it into the game Mods folder.'
        }
    }
    finally { Pop-Location }
}
finally {
    foreach ($name in $original.Keys) {
        [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process')
    }
}
