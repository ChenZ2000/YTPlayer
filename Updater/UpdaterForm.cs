using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Update;

namespace YTPlayer.Updater
{
    internal partial class UpdaterForm : Form
    {
        private readonly UpdatePlan _plan;
        private readonly UpdaterOptions _options;
        private readonly string _sessionDirectory;
        private readonly string _packageDirectory;
        private readonly string _extractionDirectory;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly UpdateServiceClient _updateClient;
        private UpdaterState _state = UpdaterState.Initializing;
        private bool _mainRestarted = false;
        private string? _downloadedPackagePath;

        private const int DownloadStart = 5;
        private const int DownloadEnd = 55;
        private const int ExtractEnd = 70;
        private const int WaitEnd = 80;
        private const int ApplyEnd = 92;
        private const int VerifyEnd = 97;
        private const int RelaunchEnd = 100;

        public UpdaterForm(UpdatePlan plan, UpdaterOptions options)
        {
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));
            _options = options ?? throw new ArgumentNullException(nameof(options));

            _sessionDirectory = Path.GetDirectoryName(options.PlanFile) ?? Path.GetTempPath();
            _packageDirectory = Path.Combine(_sessionDirectory, "package");
            _extractionDirectory = Path.Combine(_sessionDirectory, "extracted");

            Directory.CreateDirectory(_packageDirectory);
            Directory.CreateDirectory(_extractionDirectory);

            InitializeComponent();

            string formattedVersion = GetFormattedVersion();
            headingLabel.Text = $"正在更新至 {formattedVersion}";
            versionLabel.Text = $"目标版本：{formattedVersion}";

            string versionForAgent = string.IsNullOrWhiteSpace(_plan.DisplayVersion) ? "0.0.0" : _plan.DisplayVersion;
            _updateClient = new UpdateServiceClient(UpdateConstants.DefaultEndpoint, "YTPlayer.Updater", versionForAgent);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ = RunUpdateWorkflowAsync();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_state == UpdaterState.Running && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                cancelButton.Enabled = false;
                AppendLog("正在取消，请稍候...");
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                components?.Dispose();
                _cts.Cancel();
                _cts.Dispose();
                _updateClient.Dispose();
            }

            base.Dispose(disposing);
        }

        private async Task RunUpdateWorkflowAsync()
        {
            _state = UpdaterState.Running;
            var token = _cts.Token;

            try
            {
                AppendLog("开始更新流程");

                _downloadedPackagePath = await DownloadPackageAsync(token).ConfigureAwait(true);
                string payloadRoot = await ExtractPackageAsync(token).ConfigureAwait(true);
                await EnsureMainProcessExitedAsync(token).ConfigureAwait(true);
                await ApplyUpdateAsync(payloadRoot, token).ConfigureAwait(true);
                await VerifyVersionAsync(token).ConfigureAwait(true);
                await RelaunchMainAsync().ConfigureAwait(true);

                CompleteSuccessfully();
            }
            catch (OperationCanceledException)
            {
                AppendLog("更新已取消");
                await RelaunchMainIfNeededAsync().ConfigureAwait(true);
                ShowCancelledState();
            }
            catch (Exception ex)
            {
                AppendLog($"更新失败：{ex.Message}");
                await RelaunchMainIfNeededAsync().ConfigureAwait(true);
                ShowFailedState(ex.Message);
            }
            finally
            {
                CleanupWorkingDirectories();
            }
        }

        private async Task<string> DownloadPackageAsync(CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(_plan.AssetDownloadUrl))
            {
                throw new InvalidOperationException("更新计划缺少下载地址。");
            }

            UpdateStage("正在连接更新服务器...");
            AppendLog("准备下载最新版本包");

            string fileName = string.IsNullOrWhiteSpace(_plan.AssetName) ? "YTPlayer-update.zip" : _plan.AssetName;
            string destinationPath = Path.Combine(_packageDirectory, fileName);

            var progress = new Progress<UpdateDownloadProgress>(ReportDownloadProgress);
            await _updateClient.DownloadAssetAsync(_plan.AssetDownloadUrl, destinationPath, progress, token).ConfigureAwait(true);

            AppendLog("下载完成");
            UpdateProgress(DownloadEnd);
            return destinationPath;
        }

        private async Task<string> ExtractPackageAsync(CancellationToken token)
        {
            UpdateStage("正在解压更新包...");
            AppendLog("正在解压文件");
            UpdateProgress(DownloadEnd + 2);

            if (Directory.Exists(_extractionDirectory))
            {
                Directory.Delete(_extractionDirectory, true);
            }
            Directory.CreateDirectory(_extractionDirectory);

            if (string.IsNullOrEmpty(_downloadedPackagePath) || !File.Exists(_downloadedPackagePath))
            {
                throw new FileNotFoundException("未找到下载的更新包", _downloadedPackagePath);
            }

            await Task.Run(() => ZipFile.ExtractToDirectory(_downloadedPackagePath, _extractionDirectory), token).ConfigureAwait(true);
            UpdateProgress(ExtractEnd);

            string payloadRoot = ResolvePayloadRoot(_extractionDirectory);
            AppendLog($"解压完成，文件数：{Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories).Length}");
            return payloadRoot;
        }

        private async Task EnsureMainProcessExitedAsync(CancellationToken token)
        {
            if (_options.MainProcessId <= 0)
            {
                UpdateStage("等待主程序关闭...");
                UpdateProgress(WaitEnd);
                return;
            }

            try
            {
                var process = Process.GetProcessById(_options.MainProcessId);
                if (!process.HasExited)
                {
                    UpdateStage("正在请求主程序退出...");
                    AppendLog("请求主程序退出");
                    process.CloseMainWindow();
                    process.Refresh();

                    var waitTask = Task.Run(() => process.WaitForExit(30000), token);
                    bool completed = await waitTask.ConfigureAwait(true);

                    if (!completed || !process.HasExited)
                    {
                        AppendLog("主程序未及时退出，尝试强制结束");
                        process.Kill();
                        process.WaitForExit();
                    }
                }
            }
            catch (ArgumentException)
            {
                AppendLog("主程序已退出");
            }

            UpdateProgress(WaitEnd);
        }

        private async Task ApplyUpdateAsync(string sourceRoot, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                throw new InvalidOperationException("解压目录不存在");
            }

            if (string.IsNullOrWhiteSpace(_options.TargetDirectory) || !Directory.Exists(_options.TargetDirectory))
            {
                throw new DirectoryNotFoundException("安装目录不存在");
            }

            UpdateStage("正在部署新版本...");
            AppendLog("开始复制文件");

            var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
            int total = Math.Max(files.Length, 1);

            for (int i = 0; i < files.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string sourceFile = files[i];
                string relativePath = sourceFile.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destinationFile = Path.Combine(_options.TargetDirectory, relativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                if (File.Exists(destinationFile))
                {
                    File.SetAttributes(destinationFile, FileAttributes.Normal);
                }

                File.Copy(sourceFile, destinationFile, true);

                int value = ApplyStartProgress(i + 1, total);
                UpdateProgress(value);
            }

            AppendLog("文件复制完成");
            UpdateProgress(ApplyEnd);

            await Task.CompletedTask.ConfigureAwait(true);
        }

        private async Task VerifyVersionAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            UpdateStage("正在验证版本...");
            AppendLog("验证 version.txt");

            string versionFile = Path.Combine(_options.TargetDirectory, "version.txt");
            if (!File.Exists(versionFile))
            {
                throw new FileNotFoundException("目标目录缺少 version.txt", versionFile);
            }

            string versionText = await Task.Run(() => File.ReadAllText(versionFile), token).ConfigureAwait(true);
            versionText = versionText.Trim();
            string expected = NormalizeVersionString(string.IsNullOrWhiteSpace(_plan.DisplayVersion) ? _plan.TargetVersion : _plan.DisplayVersion);
            string actual = NormalizeVersionString(versionText);

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"版本验证失败：期望 {expected}，实际 {actual}");
            }

            AppendLog("版本验证通过");
            UpdateProgress(VerifyEnd);
        }

        private async Task RelaunchMainAsync()
        {
            await RelaunchMainIfNeededAsync().ConfigureAwait(true);
            UpdateProgress(RelaunchEnd);
        }

        private async Task RelaunchMainIfNeededAsync()
        {
            if (_mainRestarted)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.MainExecutablePath) || !File.Exists(_options.MainExecutablePath))
            {
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _options.MainExecutablePath,
                    WorkingDirectory = Path.GetDirectoryName(_options.MainExecutablePath),
                    UseShellExecute = false,
                    Arguments = BuildArgumentString(_options.MainArguments)
                };

                Process.Start(startInfo);
                _mainRestarted = true;
                AppendLog("已启动新版本主程序");
            }
            catch (Exception ex)
            {
                AppendLog($"重新启动主程序失败：{ex.Message}");
            }

            await Task.CompletedTask.ConfigureAwait(true);
        }

        private void CompleteSuccessfully()
        {
            _state = UpdaterState.Completed;
            UpdateStage("更新完成，正在退出...");
            AppendLog("更新流程完成");
            cancelButton.Text = "关闭";
            cancelButton.Enabled = true;

            Task.Run(async () =>
            {
                await Task.Delay(1200);
                SafeClose();
            });
        }

        private void ShowFailedState(string message)
        {
            _state = UpdaterState.Failed;
            UpdateStage($"更新失败：{message}");
            cancelButton.Text = "关闭";
            cancelButton.Enabled = true;
        }

        private void ShowCancelledState()
        {
            _state = UpdaterState.Cancelled;
            UpdateStage("更新已取消");
            cancelButton.Text = "关闭";
            cancelButton.Enabled = true;
        }

        private void ReportDownloadProgress(UpdateDownloadProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            if (progress.Percentage.HasValue)
            {
                int range = DownloadEnd - DownloadStart;
                double percentValue = Math.Max(0, Math.Min(100, progress.Percentage.Value));
                int value = DownloadStart + (int)(range * (percentValue / 100d));
                UpdateProgress(value);
                UpdateStage($"正在下载更新包 ({progress.ToHumanReadable()})");
            }
            else
            {
                UpdateStage("正在下载更新包...");
            }
        }

        private void UpdateStage(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(UpdateStage), message);
                return;
            }

            statusLabel.Text = message;
        }

        private void UpdateProgress(int value)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<int>(UpdateProgress), value);
                return;
            }

            value = Math.Max(0, Math.Min(100, value));
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = value;
        }

        private void AppendLog(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), message);
                return;
            }

            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            logListBox.Items.Add(line);
            logListBox.TopIndex = logListBox.Items.Count - 1;
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            if (_state == UpdaterState.Running && !_cts.IsCancellationRequested)
            {
                cancelButton.Enabled = false;
                _cts.Cancel();
                return;
            }

            Close();
        }

        private void SafeClose()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(SafeClose));
                return;
            }

            Close();
        }

        private void CleanupWorkingDirectories()
        {
            TryDeleteDirectory(_packageDirectory);
            TryDeleteDirectory(_extractionDirectory);
        }

        private static string ResolvePayloadRoot(string extractionDirectory)
        {
            var directories = Directory.GetDirectories(extractionDirectory, "*", SearchOption.TopDirectoryOnly);
            var files = Directory.GetFiles(extractionDirectory, "*", SearchOption.TopDirectoryOnly);

            if (directories.Length == 1 && files.Length == 0)
            {
                return directories[0];
            }

            return extractionDirectory;
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch
            {
                // 忽略清理错误
            }
        }

        private static string NormalizeVersionString(string? value)
        {
            string normalized = value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            return normalized.Trim().TrimStart('v', 'V');
        }

        private static string BuildArgumentString(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(" ", args.Select(QuoteArgument));
        }

        private static string QuoteArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg))
            {
                return "\"\"";
            }

            if (arg.IndexOfAny(new[] { ' ', '"' }) < 0)
            {
                return arg;
            }

            return $"\"{arg.Replace("\"", "\\\"")}\"";
        }

        private static int ApplyStartProgress(int currentIndex, int total)
        {
            double fraction = (double)currentIndex / total;
            fraction = Math.Max(0, Math.Min(1, fraction));
            return ExtractEnd + (int)((ApplyEnd - ExtractEnd) * fraction);
        }

        private string GetFormattedVersion()
        {
            string label = _plan.DisplayVersion;
            if (string.IsNullOrWhiteSpace(label))
            {
                label = _plan.TargetTag;
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                return "最新版本";
            }

            return label.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? label : $"v{label}";
        }

        private enum UpdaterState
        {
            Initializing,
            Running,
            Completed,
            Failed,
            Cancelled
        }
    }
}
