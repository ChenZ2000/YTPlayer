# 编译 Debug 版本
# PowerShell 脚本
$ErrorActionPreference = "Continue"

Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  编译 YTPlayer - Debug 版本" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "正在查找 MSBuild..." -ForegroundColor Yellow

# 方法1: 使用 vswhere.exe (最可靠)
$msbuildPath = $null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vsWhere) {
    $msbuildPath = & $vsWhere `
        -latest `
        -requires Microsoft.Component.MSBuild `
        -find MSBuild\**\Bin\MSBuild.exe `
        -prerelease 2>$null | Select-Object -First 1
}

# 方法2: 手动搜索常见路径（后备方案）
if (-not $msbuildPath) {
    Write-Host "vswhere.exe 未找到，尝试手动搜索..." -ForegroundColor Yellow

    # 优先搜索所有驱动器上的 VS 2022
    $searchPaths = @(
        # E: 驱动器（用户自定义安装位置）
        "E:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "E:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "E:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "E:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",

        # D: 驱动器
        "D:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "D:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "D:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "D:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",

        # C: 驱动器（默认位置）
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",

        # VS 2019
        "E:\Program Files\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
        "D:\Program Files\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($path in $searchPaths) {
        if (Test-Path $path) {
            $msbuildPath = $path
            Write-Host "      找到自定义位置的 MSBuild" -ForegroundColor Green
            break
        }
    }
}

if (-not $msbuildPath) {
    Write-Host ""
    Write-Host "✗ 错误: 未找到 MSBuild" -ForegroundColor Red
    Write-Host ""
    Write-Host "请先安装 Visual Studio Build Tools:" -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\install_build_tools.ps1" -ForegroundColor White
    Write-Host ""
    Write-Host "或手动下载: https://visualstudio.microsoft.com/downloads/" -ForegroundColor Yellow
    Write-Host "选择 'Build Tools for Visual Studio 2022'" -ForegroundColor Yellow
    exit 1
}

Write-Host "✓ 找到 MSBuild: $msbuildPath" -ForegroundColor Green

# 获取版本
try {
    $version = & $msbuildPath -version 2>&1 | Select-String "(\d+\.\d+\.\d+)" | ForEach-Object { $_.Matches[0].Value }
    if ($version) {
        Write-Host "  版本: $version" -ForegroundColor Gray
    }
} catch {
    Write-Host "  (无法获取版本)" -ForegroundColor Gray
}

Write-Host ""

# 解决方案文件
$solutionFile = "YTPlayer.sln"

# 还原 NuGet 包
Write-Host "[1/2] 还原 NuGet 包..." -ForegroundColor Yellow
& $msbuildPath /t:Restore $solutionFile /p:Configuration=Debug /p:Platform="Any CPU" /v:minimal /nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host "      ✗ NuGet 包还原失败！" -ForegroundColor Red
    exit 1
}

Write-Host "      ✓ NuGet 包还原成功" -ForegroundColor Green
Write-Host ""

# 编译项目（Debug 版本，包含调试符号）
Write-Host "[2/2] 编译解决方案 (Debug 配置)..." -ForegroundColor Yellow
Write-Host ""

$startTime = Get-Date
& $msbuildPath $solutionFile /p:Configuration=Debug /p:Platform="Any CPU" /t:Rebuild /v:minimal /nologo /m
$exitCode = $LASTEXITCODE
$elapsed = (Get-Date) - $startTime

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan

if ($exitCode -eq 0) {
    Write-Host "  ✓ 编译成功！" -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "耗时: $($elapsed.TotalSeconds.ToString('0.0')) 秒" -ForegroundColor Gray
    Write-Host ""

    # 复制 BASS 库到输出目录
    $copiedFiles = 0
    if (Test-Path "Resources\bass.dll") {
        Copy-Item "Resources\bass.dll" "bin\Debug\" -Force
        $copiedFiles++
    } elseif (Test-Path "bass.dll") {
        Copy-Item "bass.dll" "bin\Debug\" -Force
        $copiedFiles++
    }

    if (Test-Path "Resources\bassflac.dll") {
        Copy-Item "Resources\bassflac.dll" "bin\Debug\" -Force
        $copiedFiles++
    } elseif (Test-Path "bassflac.dll") {
        Copy-Item "bassflac.dll" "bin\Debug\" -Force
        $copiedFiles++
    }

    if ($copiedFiles -gt 0) {
        Write-Host "已复制 BASS 音频库 ($copiedFiles 个文件)" -ForegroundColor Green
        Write-Host ""
    }

    Write-Host "输出文件:" -ForegroundColor Yellow
    if (Test-Path "bin\Debug\YTPlayer.exe") {
        $fileInfo = Get-Item "bin\Debug\YTPlayer.exe"
        Write-Host "  ✓ bin\Debug\YTPlayer.exe" -ForegroundColor Green
        Write-Host "    大小: $([math]::Round($fileInfo.Length / 1KB, 1)) KB" -ForegroundColor Gray
        Write-Host "    修改时间: $($fileInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor Gray
    }

    Write-Host ""
    Write-Host "运行程序:" -ForegroundColor Yellow
    Write-Host "  .\bin\Debug\YTPlayer.exe" -ForegroundColor White
    Write-Host ""
    Write-Host "调试日志位置:" -ForegroundColor Yellow
    Write-Host "  bin\Debug\Logs\Debug_YYYYMMDD_HHMMSS.log" -ForegroundColor Gray
    Write-Host ""
} else {
    Write-Host "  ✗ 编译失败 (退出代码: $exitCode)" -ForegroundColor Red
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "请检查上面的错误信息" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}
