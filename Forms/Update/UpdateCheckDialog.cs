using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Update;

namespace YTPlayer.Forms
{
    public partial class UpdateCheckDialog : Form
    {
        private readonly UpdateServiceClient _updateClient;
        private CancellationTokenSource? _cts;
        private UpdateDialogState _state = UpdateDialogState.Checking;
        private UpdatePlan? _pendingPlan;
        private UpdateCheckResult? _lastResult;

        public UpdateCheckDialog()
        {
            InitializeComponent();
            currentVersionLabel.Text = $"当前版本：v{VersionInfo.Version}";
            _updateClient = new UpdateServiceClient(UpdateConstants.DefaultEndpoint, "YTPlayer", VersionInfo.Version);
        }

        public Func<UpdatePlan, bool>? UpdateLauncher { get; set; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            BeginCheck();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_state == UpdateDialogState.Checking && _cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                cancelButton.Enabled = false;
                statusLabel.Text = "正在取消，请稍候...";
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
                _cts?.Cancel();
                _cts?.Dispose();
                _updateClient?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void BeginCheck()
        {
            _state = UpdateDialogState.Checking;
            _pendingPlan = null;
            _lastResult = null;
            retryButton.Visible = false;
            retryButton.Enabled = false;
            updateButton.Visible = false;
            cancelButton.Text = "取消";
            cancelButton.Enabled = true;
            statusLabel.Text = "正在检查更新...";
            progressBar.Value = 0;
            progressBar.Style = ProgressBarStyle.Marquee;
            SetResultText("正在检查更新，请稍候…");
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _ = RunCheckAsync(_cts.Token);
        }

        private async Task RunCheckAsync(CancellationToken token)
        {
            try
            {
                var result = await _updateClient.CheckForUpdatesAsync(VersionInfo.Version, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                _lastResult = result;

                var asset = UpdateFormatting.SelectPreferredAsset(result.Response.Data?.Assets);
                bool updateAvailable = result.Response.Data?.UpdateAvailable == true && asset != null;

                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Value = 100;

                if (updateAvailable)
                {
                    var plan = UpdatePlan.FromResponse(result.Response, asset!, VersionInfo.Version);
                    _pendingPlan = plan;
                    ShowUpdateAvailable(plan, result);
                }
                else
                {
                    ShowUpToDate(result);
                }
            }
            catch (OperationCanceledException)
            {
                if (_state == UpdateDialogState.Checking)
                {
                    _state = UpdateDialogState.Cancelled;
                    statusLabel.Text = "检查已取消";
                    cancelButton.Text = "关闭";
                    cancelButton.Enabled = true;
                    retryButton.Visible = true;
                    retryButton.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        private void ShowUpdateAvailable(UpdatePlan plan, UpdateCheckResult result)
        {
            _state = UpdateDialogState.ReadyToInstall;
            string versionLabel = UpdateFormatting.FormatVersionLabel(plan, result?.Response?.Data?.Latest?.SemanticVersion);
            statusLabel.Text = $"有新版本 {versionLabel}";

            updateButton.Visible = true;
            updateButton.Enabled = true;
            cancelButton.Text = "取消";
            cancelButton.Enabled = true;
            retryButton.Visible = false;

            var summaryLines = new List<string>
            {
                $"当前版本：v{VersionInfo.Version}",
                $"最新版本：{versionLabel}",
                $"更新包：{plan.AssetName}（{UpdateFormatting.FormatSize(plan.AssetSize)}）"
            };

            if (!string.IsNullOrWhiteSpace(plan.ReleaseTitle))
            {
                summaryLines.Add($"标题：{plan.ReleaseTitle}");
            }

            if (plan.PublishedAt.HasValue)
            {
                summaryLines.Add($"发布时间：{plan.PublishedAt.Value:yyyy-MM-dd HH:mm}");
            }

            if (!string.IsNullOrWhiteSpace(plan.ReleasePage))
            {
                summaryLines.Add($"发布页：{plan.ReleasePage}");
            }

            summaryLines.Add("选择“立即更新”开始安装或稍后再说。");
            SetResultText(summaryLines);
        }

        private void ShowUpToDate(UpdateCheckResult? result)
        {
            _state = UpdateDialogState.UpToDate;
            statusLabel.Text = "您的版本已经是最新的了！";
            cancelButton.Text = "关闭";
            cancelButton.Enabled = true;
            retryButton.Visible = false;
            updateButton.Visible = false;

            string latestText = result?.Response?.Data?.Latest != null
                ? $"最新版本：{UpdateFormatting.FormatVersionLabel(null, result.Response.Data.Latest.SemanticVersion)}"
                : "未获取到最新版本信息。";

            SetResultText(
                $"当前版本：v{VersionInfo.Version}",
                latestText,
                "您使用的是最新版本！");
        }

        private void ShowError(string message)
        {
            _state = UpdateDialogState.Error;
            statusLabel.Text = $"检查更新失败：{message}";
            cancelButton.Text = "关闭";
            cancelButton.Enabled = true;
            retryButton.Visible = true;
            retryButton.Enabled = true;
            updateButton.Visible = false;
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;
            SetResultText($"检查更新失败：{message}", "请检查网络连接或稍后再试。");
        }

        private void retryButton_Click(object sender, EventArgs e)
        {
            BeginCheck();
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            if (_state == UpdateDialogState.Checking && _cts != null && !_cts.IsCancellationRequested)
            {
                cancelButton.Enabled = false;
                statusLabel.Text = "正在取消，请稍候...";
                _cts.Cancel();
                return;
            }

            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void updateButton_Click(object sender, EventArgs e)
        {
            if (_pendingPlan == null)
            {
                return;
            }

            if (UpdateLauncher == null)
            {
                MessageBox.Show(this, "未配置更新安装程序。", "无法启动更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            updateButton.Enabled = false;
            cancelButton.Enabled = false;
            bool started = false;

            try
            {
                started = UpdateLauncher.Invoke(_pendingPlan);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"启动更新程序失败：{ex.Message}", "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            if (started)
            {
                statusLabel.Text = "更新程序已启动，应用即将关闭...";
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                updateButton.Enabled = true;
                cancelButton.Enabled = true;
            }
        }

        private void SetResultText(params string[] lines)
        {
            if (lines == null || lines.Length == 0)
            {
                resultTextBox.Clear();
                return;
            }

            string content = string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            resultTextBox.Text = content;
            resultTextBox.SelectionStart = 0;
            resultTextBox.SelectionLength = 0;
        }

        private void SetResultText(IEnumerable<string> lines)
        {
            if (lines == null)
            {
                SetResultText(Array.Empty<string>());
                return;
            }

            SetResultText(lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
        }

        private enum UpdateDialogState
        {
            Checking,
            ReadyToInstall,
            UpToDate,
            Error,
            Cancelled
        }
    }
}
