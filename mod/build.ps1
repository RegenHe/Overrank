param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $projectDir
$gameRoot = Split-Path -Parent (Split-Path -Parent $projectDir)
$managedDir = Join-Path $gameRoot 'Overcooked2_Data\Managed'
$bepInExCore = Join-Path $gameRoot 'BepInEx\core'
$localDotnetHome = Join-Path $gameRoot 'tmp\dotnet-home'
$outputDir = Join-Path $projectDir ("bin\" + $Configuration)
$output = Join-Path $outputDir 'Overrank.dll'
$deployed = Join-Path $gameRoot 'BepInEx\plugins\Overrank.dll'
$deployedPdb = Join-Path $gameRoot 'BepInEx\plugins\Overrank.pdb'
$buildTempDir = Join-Path $projectRoot 'tmp\build'
$buildEnvironment = Join-Path $buildTempDir 'build.env'

function Read-DotEnv([string]$Path) {
    $values = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $values }
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }
        $separator = $trimmed.IndexOf('=')
        if ($separator -le 0) { continue }
        $key = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1).Trim().Trim('"').Trim("'")
        $values[$key] = $value
    }
    return $values
}

$envPath = Join-Path $projectRoot '.env'
if (-not (Test-Path -LiteralPath $envPath)) {
    $envPath = Join-Path $projectRoot '.env.example'
}
$buildValues = Read-DotEnv $envPath
$scheme = if ($buildValues.ContainsKey('OVERRANK_SCHEME')) { $buildValues['OVERRANK_SCHEME'] } else { 'http' }
$publicHost = if ($buildValues.ContainsKey('OVERRANK_PUBLIC_HOST')) { $buildValues['OVERRANK_PUBLIC_HOST'] } elseif ($buildValues.ContainsKey('OVERRANK_HOST')) { $buildValues['OVERRANK_HOST'] } else { '127.0.0.1' }
$port = if ($buildValues.ContainsKey('OVERRANK_PORT')) { $buildValues['OVERRANK_PORT'] } else { '3005' }
$serverUrl = if ($buildValues.ContainsKey('OVERRANK_SERVER_URL')) { $buildValues['OVERRANK_SERVER_URL'].TrimEnd('/') } else { $scheme + '://' + $publicHost + ':' + $port }
$defaultApiKey = if ($buildValues.ContainsKey('OVERRANK_API_KEY')) { $buildValues['OVERRANK_API_KEY'] } else { '' }

$env:DOTNET_CLI_HOME = $localDotnetHome
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$sdkRows = @(dotnet --list-sdks)
$lastSdk = $sdkRows[$sdkRows.Count - 1]
if ($lastSdk -notmatch '^([^ ]+) \[(.+)\]$') {
    throw "Could not find the Roslyn compiler: $lastSdk"
}
$csc = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'

New-Item -ItemType Directory -Force -Path $localDotnetHome | Out-Null
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
New-Item -ItemType Directory -Force -Path $buildTempDir | Out-Null
$embeddedLines = @(
    'OVERRANK_SERVER_URL=' + $serverUrl
    'OVERRANK_API_KEY=' + $defaultApiKey
)
[IO.File]::WriteAllText(
    $buildEnvironment,
    ($embeddedLines -join [Environment]::NewLine),
    (New-Object Text.UTF8Encoding($false)))

$references = @(
    (Join-Path $managedDir 'mscorlib.dll'),
    (Join-Path $managedDir 'System.dll'),
    (Join-Path $managedDir 'System.Core.dll'),
    (Join-Path $bepInExCore 'BepInEx.dll'),
    (Join-Path $bepInExCore '0Harmony.dll'),
    (Join-Path $managedDir 'Assembly-CSharp.dll'),
    (Join-Path $managedDir 'UnityEngine.dll'),
    (Join-Path $managedDir 'UnityEngine.CoreModule.dll'),
    (Join-Path $managedDir 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managedDir 'UnityEngine.JSONSerializeModule.dll'),
    (Join-Path $managedDir 'UnityEngine.UnityWebRequestModule.dll')
)
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) { throw "Missing reference: $reference" }
}

$sources = @(Get-ChildItem -LiteralPath $projectDir -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$compilerArgs = @(
    $csc,
    '/noconfig',
    '/nostdlib+',
    '/target:library',
    '/deterministic+',
    '/langversion:7.3',
    ("/out:" + $output),
    ("/resource:" + $buildEnvironment + ',Overrank.BuildEnvironment')
)
if ($Configuration -eq 'Release') {
    $compilerArgs += @('/optimize+', '/debug-')
} else {
    $compilerArgs += @('/optimize-', '/debug:portable')
}
$compilerArgs += $references | ForEach-Object { '/reference:' + $_ }
$compilerArgs += $sources

& dotnet @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath $output -Destination $deployed -Force
if (Test-Path -LiteralPath $deployedPdb) { Remove-Item -LiteralPath $deployedPdb -Force }
Write-Host "Built:    $output"
Write-Host "Deployed: $deployed"
Write-Host "Server:   $serverUrl (from $envPath)"
