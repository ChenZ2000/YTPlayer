# 自动查找并使用 MSBuild 编译项目
# PowerShell 脚本

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "正在查找 MSBuild..." -ForegroundColor Cyan

# 查找 MSBuild
$vsWherePath = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$msbuildPath = $null

if (Test-Path $vsWherePath) {
    $msbuildPath = & $vsWherePath `
        -latest `
        -requires Microsoft.Component.MSBuild `
        -find "MSBuild\**\Bin\MSBuild.exe" `
        -prerelease | Select-Object -First 1
}

if (-not $msbuildPath) {
    Write-Host "错误: 未找到 MSBuild。" -ForegroundColor Red
    Write-Host ""
    Write-Host "请先安装 Visual Studio 2022 或更新版本，" -ForegroundColor Yellow
    Write-Host "并勾选 “.NET 桌面开发” 工作负载。" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "下载地址: https://visualstudio.microsoft.com/vs/community/" -ForegroundColor Yellow
    exit 1
}

Write-Host "找到 MSBuild: $msbuildPath" -ForegroundColor Green
Write-Host ""

# 解决方案文件
$solutionFile = "YTPlayer.sln"

# 还原 NuGet 包
Write-Host "正在还原 NuGet 包..." -ForegroundColor Cyan
& $msbuildPath /t:Restore $solutionFile /p:Configuration=Release /v:minimal

if ($LASTEXITCODE -ne 0) {
    Write-Host "NuGet 包还原失败！" -ForegroundColor Red
    exit 1
}

Write-Host "NuGet 包还原成功！" -ForegroundColor Green
Write-Host ""

# 编译项目
Write-Host "正在编译项目..." -ForegroundColor Cyan
& $msbuildPath $solutionFile /p:Configuration=Release /v:minimal

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "===============================================" -ForegroundColor Green
    Write-Host "  编译成功！" -ForegroundColor Green
    Write-Host "===============================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "可执行文件位置:" -ForegroundColor Yellow
    Write-Host "  bin\Release\YTPlayer.exe" -ForegroundColor Cyan
    Write-Host ""

    Write-Host ""
    Write-Host "现在可以运行程序了:" -ForegroundColor Yellow
    Write-Host "  .\bin\Release\YTPlayer.exe" -ForegroundColor Cyan
} else {
    Write-Host ""
    Write-Host "编译失败！请检查错误信息。" -ForegroundColor Red
    exit 1
}
