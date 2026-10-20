#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$MSBuild,
    [string]$FFmpeg = 'ffmpeg'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $MSBuild) {
    $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($command) { $MSBuild = $command.Source }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $MSBuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
                Select-Object -First 1
        }
    }
}
if (-not $MSBuild -or -not (Test-Path -LiteralPath $MSBuild)) {
    throw 'MSBuild was not found. Supply -MSBuild with the Visual Studio MSBuild.exe path.'
}
$ffmpegCommand = Get-Command $FFmpeg -ErrorAction Stop
$output = Join-Path $repoRoot 'artifacts/verification'
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Invoke-Checked([string]$Name, [string]$Executable, [string[]]$ArgumentList) {
    Write-Host "Running $Name..."
    $log = Join-Path $output ($Name + '.log')
    & $Executable @ArgumentList *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $log -Tail 40 | Write-Host
        throw "$Name failed (exit $LASTEXITCODE). See $log"
    }
    Get-Content -LiteralPath $log -Tail 6 | Write-Host
}

Push-Location $repoRoot
try {
    # Serialize builds: each test project builds the same production application.
    $buildArgs = @('/restore', '/t:Build', '/p:Configuration=Debug', '/p:Platform=x64', '/m', '/nologo', '/v:normal', '/warnaserror')
    Invoke-Checked 'debug-build' $MSBuild (@('YTPlayer.sln') + $buildArgs)
    foreach ($suite in @('ListenTogether', 'Recognition', 'Playback')) {
        Invoke-Checked ($suite + '-build') $MSBuild (@("Tests/$suite/$suite.Tests.csproj") + $buildArgs)
        Invoke-Checked ($suite + '-tests') (Join-Path $repoRoot "Tests/$suite/bin/x64/Debug/net10.0-windows/$suite.Tests.exe") @()
    }
    $fixture = Join-Path $output 'seek-fixture.mp3'
    $flacFixture = Join-Path $output 'recovery-fixture.flac'
    try {
        Invoke-Checked 'seek-fixture' $ffmpegCommand.Source @('-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i',
            'sine=frequency=440:sample_rate=44100:duration=360', '-ac', '2', '-c:a', 'libmp3lame', '-b:a', '192k', '-y', $fixture)
        Invoke-Checked 'seek-tests' (Join-Path $repoRoot 'Tests/Playback/bin/x64/Debug/net10.0-windows/Playback.Tests.exe') @('--seek-tests', $fixture)
        Invoke-Checked 'recovery-fixture' $ffmpegCommand.Source @('-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i',
            'sine=frequency=440:sample_rate=44100:duration=6', '-ac', '2', '-c:a', 'libmp3lame', '-b:a', '192k', '-y', $fixture)
        Invoke-Checked 'recovery-tests' (Join-Path $repoRoot 'Tests/Playback/bin/x64/Debug/net10.0-windows/Playback.Tests.exe') @('--recovery-tests', $fixture)
        Invoke-Checked 'flac-recovery-fixture' $ffmpegCommand.Source @('-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i',
            'anoisesrc=sample_rate=48000:duration=6:seed=123', '-ac', '2', '-sample_fmt', 's32', '-c:a', 'flac', '-y', $flacFixture)
        Invoke-Checked 'flac-recovery-tests' (Join-Path $repoRoot 'Tests/Playback/bin/x64/Debug/net10.0-windows/Playback.Tests.exe') @('--recovery-tests', $flacFixture)
    }
    finally {
        # Exact synthetic file created by this script; no recursive deletion.
        if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Force }
        if (Test-Path -LiteralPath $flacFixture) { Remove-Item -LiteralPath $flacFixture -Force }
    }
    Write-Host "All offline protocol regressions passed. Logs: $output"
}
finally { Pop-Location }
