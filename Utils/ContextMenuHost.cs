using System;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    /// <summary>
    /// 自定义的上下文菜单宿主窗口
    /// 用于托管托盘图标的 ContextMenuStrip，完全控制窗口生命周期
    /// 防止虚拟窗口被 Alt+F4 关闭导致幽灵进程
    /// </summary>
    internal class ContextMenuHost : Form
    {
        private const int WM_CLOSE = 0x0010;  // Windows 关闭消息

        public ContextMenuHost()
        {
            // ⭐ 关键配置：隐藏窗口，不在任务栏显示
            this.FormBorderStyle = FormBorderStyle.None;      // 无边框
            this.ShowInTaskbar = false;                       // 不在任务栏显示
            this.StartPosition = FormStartPosition.Manual;    // 手动定位
            this.Location = new System.Drawing.Point(-32000, -32000);  // 移到屏幕外
            this.Size = new System.Drawing.Size(1, 1);        // 最小尺寸
            this.Opacity = 0;                                 // 完全透明
            this.ControlBox = false;                          // 禁用控制框
            this.MinimizeBox = false;                         // 禁用最小化按钮
            this.MaximizeBox = false;                         // 禁用最大化按钮

            System.Diagnostics.Debug.WriteLine("[ContextMenuHost] 菜单宿主窗口已创建");
        }

        /// <summary>
        /// 重写窗口过程，拦截关闭消息
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            // ⭐ 关键：拦截 WM_CLOSE 消息，防止窗口被 Alt+F4 关闭
            if (m.Msg == WM_CLOSE)
            {
                System.Diagnostics.Debug.WriteLine("[ContextMenuHost] 拦截 WM_CLOSE 消息，防止窗口被关闭");

                // 不调用 base.WndProc，阻止关闭行为
                // 而是隐藏窗口
                this.Hide();
                return;
            }

            base.WndProc(ref m);
        }

        /// <summary>
        /// 重写 OnFormClosing 事件，取消关闭操作
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[ContextMenuHost] OnFormClosing 被调用，取消关闭操作");

            // ⭐ 取消关闭操作，改为隐藏
            e.Cancel = true;
            this.Hide();

            // 不调用 base.OnFormClosing
        }

        /// <summary>
        /// 显示菜单宿主窗口
        /// </summary>
        public void ShowHost()
        {
            if (!this.Visible)
            {
                System.Diagnostics.Debug.WriteLine("[ContextMenuHost] 显示宿主窗口");
                this.Show();
            }
        }

        /// <summary>
        /// 隐藏菜单宿主窗口
        /// </summary>
        public void HideHost()
        {
            if (this.Visible)
            {
                System.Diagnostics.Debug.WriteLine("[ContextMenuHost] 隐藏宿主窗口");
                this.Hide();
            }
        }
    }
}
