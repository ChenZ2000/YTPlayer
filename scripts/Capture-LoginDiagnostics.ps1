#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Both', 'Phone', 'WeChat', 'Official')]
    [string]$Mode = 'Both',
    [string]$MSBuild = 'E:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe',
    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = Join-Path $repoRoot ('artifacts/login-capture/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not (Test-Path -LiteralPath $MSBuild)) { throw "找不到 MSBuild，请使用 -MSBuild 指定路径。" }
$oldDiagnostics = $env:YTPLAYER_LOGIN_DIAGNOSTICS
Push-Location $repoRoot
try {
    Write-Host '正在编译 Debug 登录诊断工具…'
    & $MSBuild 'Tests/Auth/Auth.Tests.csproj' /restore /t:Build /p:Configuration=Debug /p:Platform=x64 /m /nologo /v:normal /warnaserror *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath (Join-Path $output 'build.log') -Tail 30 | Write-Host
        throw 'Debug 编译失败，未启动登录。'
    }
    $env:YTPLAYER_LOGIN_DIAGNOSTICS = Join-Path $output 'protocol.jsonl'
    Write-Host '将使用易听的真实登录窗口及独立测试会话。验证码只需填写在窗口内。'
    Write-Host '默认先测试手机号，再测试微信。失败后可切到“网页登录”对照；关闭窗口进入下一项。'
    Write-Host '日志只记录请求结构、状态码和凭据是否存在，不记录手机号、短信内容、Cookie、令牌或截图。'
    $runner = Join-Path $repoRoot 'Tests/Auth/bin/x64/Debug/net10.0-windows/Auth.Tests.exe'
    if ($SmokeTest) { & $runner '--capture-smoke' }
    else { & $runner '--interactive-capture' $Mode }
    $result = $LASTEXITCODE
    @(
        'YTPlayer login capture',
        ('Mode: ' + $Mode),
        ('SmokeTest: ' + $SmokeTest),
        ('RunnerExitCode: ' + $result),
        'A runner exit code of zero does not by itself prove login success.',
        'See capture-login-completed and account-verified events in protocol.jsonl.',
        'The test session is isolated from the normal player account.'
    ) | Set-Content -LiteralPath (Join-Path $output 'summary.txt') -Encoding utf8
    $shareFiles = @(Join-Path $output 'summary.txt')
    if (Test-Path -LiteralPath $env:YTPLAYER_LOGIN_DIAGNOSTICS) { $shareFiles += $env:YTPLAYER_LOGIN_DIAGNOSTICS }
    $archive = Join-Path $output 'login-diagnostics.zip'
    Compress-Archive -LiteralPath $shareFiles -DestinationPath $archive
    Write-Host "诊断包已生成：$archive"
    Write-Host '诊断包只含协议元数据和运行摘要，不含账号文件、浏览器配置或普通应用日志。'
    if ($result -ne 0) { throw "诊断窗口运行异常（$result），已保留脱敏日志。" }
}
finally {
    $env:YTPLAYER_LOGIN_DIAGNOSTICS = $oldDiagnostics
    Pop-Location
}
