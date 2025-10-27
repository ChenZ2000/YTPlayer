# 恢复 NuGet 包（包括 Roslyn 编译器）
$ErrorActionPreference = "Continue"

Write-Host "正在恢复 NuGet 包..." -ForegroundColor Cyan

# 检查 nuget.exe 是否存在
$nugetPath = ".\nuget.exe"
if (-not (Test-Path $nugetPath)) {
    Write-Host "下载 nuget.exe..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $nugetPath
}

# 恢复包
Write-Host "恢复包..." -ForegroundColor Yellow
& $nugetPath restore packages.config -PackagesDirectory packages

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ NuGet 包恢复成功" -ForegroundColor Green
} else {
    Write-Host "✗ NuGet 包恢复失败" -ForegroundColor Red
}

exit $LASTEXITCODE
