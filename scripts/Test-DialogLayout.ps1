param(
    [string]$MSBuildPath = 'E:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe',
    [string]$ReportRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReportRoot)) { $ReportRoot = Join-Path $repoRoot 'artifacts\layout\regression' }
$reportRoot = [System.IO.Path]::GetFullPath($ReportRoot)
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$previousOutput = $env:YTPLAYER_LAYOUT_OUTPUT
$previousDpi = $env:YTPLAYER_LAYOUT_DPI
Push-Location $repoRoot
try {
    & $MSBuildPath YTPlayer.sln /t:Build /p:Configuration=Debug /p:Platform=x64 /m /nologo /v:minimal /fl "/flp:logfile=$reportRoot\build.log;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { throw 'Debug solution build failed.' }
    $updaterConfigPath = Join-Path $repoRoot 'bin\Debug\YTPlayer.Updater.exe.config'
    if (!(Test-Path -LiteralPath $updaterConfigPath)) { throw 'Updater configuration is missing beside its executable.' }
    [xml]$updaterConfig = Get-Content -LiteralPath $updaterConfigPath -Encoding UTF8 -Raw
    $dpiSetting = $updaterConfig.SelectSingleNode('/configuration/System.Windows.Forms.ApplicationConfigurationSection/add[@key="DpiAwareness"]')
    if ($null -eq $dpiSetting -or $dpiSetting.value -ne 'PerMonitorV2') { throw 'Packaged updater DPI awareness is incorrect.' }
    & $MSBuildPath Tests\Layout\Layout.Tests.csproj /restore /t:Build /p:Configuration=Debug /m /nologo /v:minimal /fl "/flp:logfile=$reportRoot\test-build.log;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { throw 'Layout test build failed.' }

    # The first process uses the actual monitor DPI. The second uses Windows' 96-DPI
    # virtualization within that test process; no display/registry settings are changed.
    foreach ($dpi in @('native', '96')) {
        $env:YTPLAYER_LAYOUT_DPI = $dpi
        $env:YTPLAYER_LAYOUT_OUTPUT = Join-Path $reportRoot $dpi
        foreach ($framework in @('net48', 'net10.0-windows')) {
            $testExe = Join-Path $repoRoot "Tests\Layout\bin\Debug\$framework\Layout.Tests.exe"
            & $testExe --workers=0 "--result=$reportRoot\$dpi-$framework.xml" "--out=$reportRoot\$dpi-$framework.txt"
            if ($LASTEXITCODE -ne 0) { throw "Layout regression failed: $dpi / $framework. See $reportRoot" }
        }
    }
    Write-Host "PASS: layout, keyboard and accessibility regressions. Reports: $reportRoot"
}
finally {
    $env:YTPLAYER_LAYOUT_OUTPUT = $previousOutput
    $env:YTPLAYER_LAYOUT_DPI = $previousDpi
    Pop-Location
}
