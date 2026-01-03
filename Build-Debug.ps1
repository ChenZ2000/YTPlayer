# 编译 Debug 版本
# PowerShell 脚本

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Continue"

Write-Host ("─" * 75) -ForegroundColor Cyan
Write-Host "  构建 YTPlayer - Debug 版本" -ForegroundColor Cyan
Write-Host ("─" * 75) -ForegroundColor Cyan
Write-Host ""
Write-Host "正在查找 MSBuild..." -ForegroundColor Yellow

# 方法 1: 使用 vswhere.exe（推荐）
$msbuildPath = $null
$vsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

if (Test-Path $vsWhere) {
    $msbuildPath = & $vsWhere `
        -latest `
        -requires Microsoft.Component.MSBuild `
        -find "MSBuild\**\Bin\MSBuild.exe" `
        -prerelease 2>$null | Select-Object -First 1
}

# 方法 2: 手动搜索常见路径（备用方案）
if (-not $msbuildPath) {
    Write-Host "vswhere.exe 未找到，尝试常见安装路径..." -ForegroundColor Yellow

    $searchPaths = @(
        # E: 可能的 VS 2022 安装路径
        "E:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "E:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "E:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "E:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",

        # D: 盘
        "D:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "D:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "D:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "D:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",

        # C: 默认安装路径
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
            Write-Host "      找到自定义路径的 MSBuild" -ForegroundColor Green
            break
        }
    }
}

if (-not $msbuildPath) {
    Write-Host ""
    Write-Host "× 错误: 未找到 MSBuild" -ForegroundColor Red
    Write-Host ""
    Write-Host "请先安装 Visual Studio Build Tools:" -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\install_build_tools.ps1" -ForegroundColor White
    Write-Host ""
    Write-Host "或访问: https://visualstudio.microsoft.com/downloads/" -ForegroundColor Yellow
    Write-Host "选择 “Build Tools for Visual Studio 2022”" -ForegroundColor Yellow
    exit 1
}

Write-Host "✓ 找到 MSBuild: $msbuildPath" -ForegroundColor Green

# 读取 MSBuild 版本
try {
    $version = & $msbuildPath -version 2>&1 | Select-String "(\d+\.\d+\.\d+)" | ForEach-Object { $_.Matches[0].Value }
    if ($version) {
        Write-Host "  版本: $version" -ForegroundColor Gray
    }
} catch {
    Write-Host "  (无法读取版本)" -ForegroundColor Gray
}

Write-Host ""

# 解决方案文件
$solutionFile = "YTPlayer.sln"

# 还原 NuGet 包（统一 x64）
Write-Host "[1/2] 还原 NuGet 包..." -ForegroundColor Yellow
& $msbuildPath /t:Restore $solutionFile /p:Configuration=Debug /p:Platform="x64" /v:minimal /nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host "      × NuGet 包还原失败" -ForegroundColor Red
    exit 1
}

Write-Host "      ✓ NuGet 包还原成功" -ForegroundColor Green
Write-Host ""

# 构建项目（Debug 配置）
Write-Host "[2/2] 开始编译项目 (Debug 配置, x64)..." -ForegroundColor Yellow
Write-Host ""

$startTime = Get-Date
& $msbuildPath $solutionFile /p:Configuration=Debug /p:Platform="x64" /t:Rebuild /v:minimal /nologo /m
$exitCode = $LASTEXITCODE
$elapsed = (Get-Date) - $startTime

Write-Host ""
Write-Host ("─" * 75) -ForegroundColor Cyan

if ($exitCode -eq 0) {
    Write-Host "  ✓ 编译成功" -ForegroundColor Green
    Write-Host ("─" * 75) -ForegroundColor Cyan
    Write-Host ""
    Write-Host "耗时: $($elapsed.TotalSeconds.ToString('0.0')) 秒" -ForegroundColor Gray
    Write-Host ""

    Write-Host "输出文件:" -ForegroundColor Yellow
    if (Test-Path "bin\Debug\YTPlayer.exe") {
        $fileInfo = Get-Item "bin\Debug\YTPlayer.exe"
        Write-Host "  ✓ bin\Debug\YTPlayer.exe (启动器)" -ForegroundColor Green
        Write-Host "    大小: $([math]::Round($fileInfo.Length / 1KB, 1)) KB" -ForegroundColor Gray
        Write-Host "    修改时间: $($fileInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor Gray
    }
    if (Test-Path "bin\Debug\YTPlayer.Updater.exe") {
        $fileInfo = Get-Item "bin\Debug\YTPlayer.Updater.exe"
        Write-Host "  ✓ bin\Debug\YTPlayer.Updater.exe" -ForegroundColor Green
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
    Write-Host "  × 编译失败 (退出代码: $exitCode)" -ForegroundColor Red
    Write-Host ("─" * 75) -ForegroundColor Cyan
    Write-Host ""
    Write-Host "请向上查看详细错误信息。" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}
