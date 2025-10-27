# 安装 Visual Studio Build Tools 2022（包含最新 MSBuild）
# 这将安装 .NET 桌面构建工具，支持 C# 7.3 及更高版本

$ErrorActionPreference = "Stop"

Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  安装 Visual Studio Build Tools 2022" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# 下载 Build Tools 安装程序
$installerUrl = "https://aka.ms/vs/17/release/vs_BuildTools.exe"
$installerPath = ".\vs_BuildTools.exe"

Write-Host "[1/3] 下载 Visual Studio Build Tools 安装程序..." -ForegroundColor Yellow
Write-Host "      下载地址: $installerUrl" -ForegroundColor Gray

try {
    # 使用 .NET WebClient 下载（更可靠）
    $webClient = New-Object System.Net.WebClient
    $webClient.DownloadFile($installerUrl, $installerPath)
    Write-Host "      ✓ 下载完成: $installerPath" -ForegroundColor Green
} catch {
    Write-Host "      ✗ 下载失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "请手动下载并安装：" -ForegroundColor Yellow
    Write-Host "  1. 访问: https://visualstudio.microsoft.com/downloads/" -ForegroundColor Yellow
    Write-Host "  2. 下载 'Build Tools for Visual Studio 2022'" -ForegroundColor Yellow
    Write-Host "  3. 运行安装程序，选择 '.NET 桌面生成工具' 工作负载" -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "[2/3] 启动安装程序..." -ForegroundColor Yellow
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  重要：在安装界面中请选择以下组件：" -ForegroundColor Yellow
Write-Host "  ✓ .NET 桌面生成工具 (必选)" -ForegroundColor Green
Write-Host "  ✓ C# 和 Visual Basic Roslyn 编译器" -ForegroundColor Green
Write-Host "  ✓ MSBuild" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "安装大小约: 2-3 GB" -ForegroundColor Gray
Write-Host "安装时间约: 10-20 分钟（取决于网速）" -ForegroundColor Gray
Write-Host ""

# 启动安装程序（静默安装 .NET 桌面构建工具）
Write-Host "正在启动安装程序（自动选择 .NET 桌面构建工具）..." -ForegroundColor Yellow
Write-Host ""

$installArgs = @(
    "--quiet",
    "--wait",
    "--norestart",
    "--add", "Microsoft.VisualStudio.Workload.MSBuildTools",
    "--add", "Microsoft.VisualStudio.Workload.NetCoreBuildTools",
    "--add", "Microsoft.Net.Component.4.8.SDK",
    "--add", "Microsoft.Component.MSBuild",
    "--add", "Microsoft.VisualStudio.Component.Roslyn.Compiler"
)

Write-Host "执行命令: $installerPath $($installArgs -join ' ')" -ForegroundColor Gray
Write-Host ""
Write-Host "请稍候，这可能需要 10-20 分钟..." -ForegroundColor Yellow
Write-Host ""

try {
    $process = Start-Process -FilePath $installerPath -ArgumentList $installArgs -Wait -PassThru

    if ($process.ExitCode -eq 0) {
        Write-Host ""
        Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Green
        Write-Host "  ✓ Visual Studio Build Tools 2022 安装成功！" -ForegroundColor Green
        Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Green
        Write-Host ""

        # 检测 MSBuild 路径
        $msbuildPaths = @(
            "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
            "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
        )

        Write-Host "[3/3] 验证 MSBuild 安装..." -ForegroundColor Yellow
        foreach ($path in $msbuildPaths) {
            if (Test-Path $path) {
                Write-Host "      ✓ 找到 MSBuild: $path" -ForegroundColor Green

                # 获取版本信息
                $version = & $path -version 2>&1 | Select-String "Microsoft.*Build Engine"
                Write-Host "      版本: $version" -ForegroundColor Gray

                Write-Host ""
                Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
                Write-Host "  下一步：运行编译脚本" -ForegroundColor Cyan
                Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
                Write-Host ""
                Write-Host "现在可以运行以下命令编译项目：" -ForegroundColor Yellow
                Write-Host ""
                Write-Host "  Debug 版本:" -ForegroundColor Green
                Write-Host "    powershell -ExecutionPolicy Bypass -File .\Build-Debug.ps1" -ForegroundColor White
                Write-Host ""
                Write-Host "  Release 版本:" -ForegroundColor Green
                Write-Host "    powershell -ExecutionPolicy Bypass -File .\Build.ps1" -ForegroundColor White
                Write-Host ""

                exit 0
            }
        }

        Write-Host "      ⚠ 警告: 未找到 MSBuild.exe" -ForegroundColor Yellow
        Write-Host "      可能需要重启计算机后才能使用" -ForegroundColor Yellow

    } elseif ($process.ExitCode -eq 3010) {
        Write-Host ""
        Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Yellow
        Write-Host "  ✓ 安装成功，但需要重启计算机" -ForegroundColor Yellow
        Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "请重启计算机后再编译项目。" -ForegroundColor Yellow
        exit 0
    } else {
        Write-Host ""
        Write-Host "✗ 安装失败，退出代码: $($process.ExitCode)" -ForegroundColor Red
        Write-Host ""
        Write-Host "请尝试手动安装：" -ForegroundColor Yellow
        Write-Host "  1. 运行: $installerPath" -ForegroundColor Yellow
        Write-Host "  2. 在界面中选择 '.NET 桌面生成工具' 工作负载" -ForegroundColor Yellow
        Write-Host "  3. 点击安装" -ForegroundColor Yellow
        exit 1
    }
} catch {
    Write-Host ""
    Write-Host "✗ 安装过程出错: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    # 清理安装程序
    if (Test-Path $installerPath) {
        Write-Host ""
        Write-Host "清理安装程序..." -ForegroundColor Gray
        Remove-Item $installerPath -Force -ErrorAction SilentlyContinue
    }
}
