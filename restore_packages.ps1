# 恢复 NuGet 包（PackageReference）
$ErrorActionPreference = "Continue"

Write-Host "正在恢复 NuGet 包..." -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "未找到 dotnet CLI，请先安装 .NET 10 SDK。" -ForegroundColor Red
    exit 1
}

$solutionFile = "YTPlayer.sln"
dotnet restore $solutionFile

if ($LASTEXITCODE -eq 0) {
    Write-Host "? NuGet 包恢复成功" -ForegroundColor Green
} else {
    Write-Host "? NuGet 包恢复失败" -ForegroundColor Red
}

exit $LASTEXITCODE
