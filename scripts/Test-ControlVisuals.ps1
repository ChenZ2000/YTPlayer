param(
    [string]$MSBuildPath = 'E:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe',
    [string]$SeekFixture,
    [string]$RecoveryFixture
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$reportRoot = Join-Path $repoRoot 'artifacts\controls'
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
Push-Location $repoRoot
try {
    & "$PSScriptRoot\Test-DialogLayout.ps1" -MSBuildPath $MSBuildPath -ReportRoot (Join-Path $reportRoot 'regression')
    foreach ($suite in @('Playback', 'Navigation', 'ListenTogether', 'Recognition')) {
        & $MSBuildPath "Tests\$suite\$suite.Tests.csproj" /restore /t:Build /p:Configuration=Debug /nologo /v:minimal /fl "/flp:logfile=$reportRoot\$suite-build.log;verbosity=normal"
        if ($LASTEXITCODE -ne 0) { throw "$suite test build failed" }
        & "Tests\$suite\bin\Debug\net10.0-windows\$suite.Tests.exe" *> (Join-Path $reportRoot "$suite.txt")
        if ($LASTEXITCODE -ne 0) { throw "$suite regression failed. See $reportRoot\$suite.txt" }
    }
    foreach ($fixture in @(@{ Path = $SeekFixture; Argument = '--seek-tests'; Name = 'seek' }, @{ Path = $RecoveryFixture; Argument = '--recovery-tests'; Name = 'recovery' })) {
        if ([string]::IsNullOrWhiteSpace($fixture.Path)) { continue }
        $fixturePath = (Resolve-Path -LiteralPath $fixture.Path).Path
        & Tests\Playback\bin\Debug\net10.0-windows\Playback.Tests.exe $fixture.Argument $fixturePath *> (Join-Path $reportRoot ($fixture.Name + '.txt'))
        if ($LASTEXITCODE -ne 0) { throw ($fixture.Name + ' regression failed') }
    }
    Write-Host "PASS: controls, layout, playback, navigation, listen-together and recognition. Reports: $reportRoot"
}
finally { Pop-Location }
