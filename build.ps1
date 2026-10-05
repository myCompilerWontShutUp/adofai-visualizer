param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Test,
    [string]$ReadOnlyMapRoot,
    [switch]$CompareGameRules
)
$ErrorActionPreference = 'Stop'
if ($ReadOnlyMapRoot -and !$Test) { throw '-ReadOnlyMapRoot requires -Test.' }
if ($CompareGameRules -and (!$Test -or !$ReadOnlyMapRoot)) { throw '-CompareGameRules requires -Test and -ReadOnlyMapRoot.' }
$projectRoot = $PSScriptRoot
$localRoot = Join-Path $projectRoot '.local'
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
        dotnet build .\Visualizer.sln -c $Configuration --configfile .\NuGet.Config --disable-build-servers -p:UseSharedCompilation=false -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw "Build failed: exit code $LASTEXITCODE" }
        if ($Test) {
            $testArguments = @((Join-Path $localRoot 'test-artifacts'))
            if ($ReadOnlyMapRoot) { $testArguments += @('--read-only-map-root', $ReadOnlyMapRoot) }
            if ($CompareGameRules) { $testArguments += '--compare-game-rules' }
            dotnet (Join-Path $projectRoot "tests/Visualizer.Tests/bin/$Configuration/net9.0-windows/Visualizer.Tests.dll") @testArguments
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: exit code $LASTEXITCODE" }
        }
    }
    finally { Pop-Location }
}
finally {
    foreach ($name in $original.Keys) {
        [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process')
    }
}
