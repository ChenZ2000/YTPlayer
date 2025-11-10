using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Update;

namespace YTPlayer.Forms
{
    public partial class UpdateAvailablePromptDialog : Form
    {
        private readonly UpdatePlan _plan;
        private readonly string _versionLabel;

        public UpdateAvailablePromptDialog(UpdatePlan plan, string? versionLabel = null)
        {
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));
            InitializeComponent();

            _versionLabel = string.IsNullOrWhiteSpace(versionLabel)
                ? UpdateFormatting.FormatVersionLabel(plan, null)
                : versionLabel!;

            summaryLabel.Text = $"发现新版本 {_versionLabel}";
            resultTextBox.Text = BuildSummaryText();
            resultTextBox.SelectionStart = 0;
            resultTextBox.SelectionLength = 0;
        }

        public Func<UpdatePlan, bool>? UpdateLauncher { get; set; }

        private string BuildSummaryText()
        {
            var lines = new List<string>
            {
                $"当前版本：v{VersionInfo.Version}",
                $"最新版本：{_versionLabel}",
                $"更新包：{_plan.AssetName}（{UpdateFormatting.FormatSize(_plan.AssetSize)}）"
            };

            if (!string.IsNullOrWhiteSpace(_plan.ReleaseTitle))
            {
                lines.Add($"标题：{_plan.ReleaseTitle}");
            }

            if (_plan.PublishedAt.HasValue)
            {
                lines.Add($"发布时间：{_plan.PublishedAt:yyyy-MM-dd HH:mm}");
            }

            if (!string.IsNullOrWhiteSpace(_plan.ReleasePage))
            {
                lines.Add($"发布页：{_plan.ReleasePage}");
            }

            lines.Add("选择“立即更新”将启动更新程序，或点击“关闭”稍后再说。");
            return string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));
        }

        private void updateButton_Click(object sender, EventArgs e)
        {
            if (UpdateLauncher == null)
            {
                Close();
                return;
            }

            bool started = false;
            try
            {
                started = UpdateLauncher.Invoke(_plan);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"启动更新失败：{ex.Message}", "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (started)
            {
                Close();
            }
        }

        private void closeButton_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
