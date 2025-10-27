# 自动查找并使用 MSBuild 编译项目
# PowerShell 脚本

Write-Host "正在查找 MSBuild..." -ForegroundColor Cyan

# 查找 MSBuild
$msbuildPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest `
    -requires Microsoft.Component.MSBuild `
    -find MSBuild\**\Bin\MSBuild.exe `
    -prerelease | Select-Object -First 1

if (-not $msbuildPath) {
    Write-Host "错误: 未找到 MSBuild。请安装 Visual Studio 2017 或更高版本。" -ForegroundColor Red
    Write-Host ""
    Write-Host "您可以从以下位置下载：" -ForegroundColor Yellow
    Write-Host "  Visual Studio 2022 Community (免费): https://visualstudio.microsoft.com/vs/community/" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "或者使用 Visual Studio Installer 安装 '.NET 桌面开发' 工作负荷" -ForegroundColor Yellow
    exit 1
}

Write-Host "找到 MSBuild: $msbuildPath" -ForegroundColor Green
Write-Host ""

# 解决方案文件
$solutionFile = "YTPlayer.sln"

# 还原 NuGet 包
Write-Host "正在还原 NuGet 包..." -ForegroundColor Cyan
& $msbuildPath /t:Restore $solutionFile /p:Configuration=Release

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

    # 复制 BASS 库到输出目录
    if (Test-Path "Resources\bass.dll") {
        Copy-Item "Resources\bass.dll" "bin\Release\" -Force
        Write-Host "已复制 bass.dll" -ForegroundColor Green
    }
    if (Test-Path "Resources\bassflac.dll") {
        Copy-Item "Resources\bassflac.dll" "bin\Release\" -Force
        Write-Host "已复制 bassflac.dll" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "现在可以运行程序了:" -ForegroundColor Yellow
    Write-Host "  .\bin\Release\YTPlayer.exe" -ForegroundColor Cyan
} else {
    Write-Host ""
    Write-Host "编译失败！请检查错误信息。" -ForegroundColor Red
    exit 1
}
