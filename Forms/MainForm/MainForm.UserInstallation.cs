using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;

namespace YTPlayer
{
    public partial class MainForm
    {
        private const string InstallToCurrentUserMenuText = "安装到当前 Windows 用户";
        private const string PackagePortableMenuText = "打包为便携版";

        private ToolStripMenuItem? _userInstallationMenuItem;
        private bool _storageMigrationInProgress;

        private void InitializeUserInstallationFeature()
        {
            AddUserInstallationMenuEntry();
            UpdateUserInstallationMenuEntry();
        }

        private void AddUserInstallationMenuEntry()
        {
            if (fileMenuItem == null || _userInstallationMenuItem != null)
            {
                return;
            }

            _userInstallationMenuItem = new ToolStripMenuItem
            {
                Name = "userInstallationMenuItem"
            };
            _userInstallationMenuItem.Click += UserInstallationMenuItem_Click;

            int insertIndex = fileMenuItem.DropDownItems.IndexOf(refreshMenuItem);
            if (insertIndex < 0)
            {
                insertIndex = fileMenuItem.DropDownItems.IndexOf(downloadManagerMenuItem);
                if (insertIndex >= 0)
                {
                    insertIndex++;
                }
            }

            if (insertIndex < 0)
            {
                insertIndex = fileMenuItem.DropDownItems.Count;
            }

            fileMenuItem.DropDownItems.Insert(insertIndex, _userInstallationMenuItem);
        }

        private void UpdateUserInstallationMenuEntry()
        {
            if (_userInstallationMenuItem == null)
            {
                return;
            }

            UpdateUserInstallationMenuText(ApplicationStorage.CurrentMode);
            _userInstallationMenuItem.Enabled = !_storageMigrationInProgress;
        }

        private void UpdateUserInstallationMenuText(ApplicationStorageMode mode)
        {
            if (_userInstallationMenuItem == null)
            {
                return;
            }

            string text = mode == ApplicationStorageMode.CurrentUser
                ? PackagePortableMenuText
                : InstallToCurrentUserMenuText;

            if (!string.Equals(_userInstallationMenuItem.Text, text, StringComparison.Ordinal))
            {
                _userInstallationMenuItem.Text = text;
            }
        }

        private async void UserInstallationMenuItem_Click(object? sender, EventArgs e)
        {
            if (_storageMigrationInProgress)
            {
                return;
            }

            bool installToCurrentUser = !ApplicationStorage.IsCurrentUserInstalled;
            string prompt = installToCurrentUser
                ? @"本操作会将账户和应用配置迁移到 %userprofile%\Documents\YTPlayer 下，是否继续？"
                : "本操作会将账户和应用配置迁移回应用启动器所在目录，是否继续？";
            string title = installToCurrentUser ? InstallToCurrentUserMenuText : PackagePortableMenuText;

            DialogResult confirm = MessageBox.ShowContinueCancel(
                this,
                prompt,
                title,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            if (confirm != DialogResult.OK)
            {
                return;
            }

            ApplicationStorageMode targetMode = installToCurrentUser
                ? ApplicationStorageMode.CurrentUser
                : ApplicationStorageMode.Portable;

            await RunStorageMigrationAsync(targetMode).ConfigureAwait(true);
        }

        private async Task RunStorageMigrationAsync(ApplicationStorageMode targetMode)
        {
            bool installing = targetMode == ApplicationStorageMode.CurrentUser;
            string inProgressText = installing ? "正在安装..." : "正在打包便携版...";
            string successText = installing ? "安装完成" : "打包便携版完成";
            string failureTitle = installing ? "安装失败" : "打包便携版失败";
            StorageMigrationActivitySnapshot? snapshot = null;
            string currentStep = "准备执行";

            _storageMigrationInProgress = true;
            UpdateUserInstallationMenuEntry();
            AnnounceStatusBarMessage(inProgressText);

            try
            {
                currentStep = "暂停应用活动";
                snapshot = PauseActivityForStorageMigration();
                currentStep = "刷新当前配置文件";
                FlushStorageFilesBeforeMigration();

                currentStep = "执行迁移";
                await Task.Run(() =>
                {
                    ApplicationStorageMigrator.MigrateTo(
                        targetMode,
                        stage => OnStorageMigrationProgress(targetMode, stage));
                }).ConfigureAwait(true);

                _configManager = ConfigManager.Instance;
                _config = _configManager.Load();
                UpdateUserInstallationMenuText(ApplicationStorage.CurrentMode);

                ResumeActivityAfterStorageMigration(snapshot);
                snapshot = null;
                AnnounceStatusBarMessage(successText);
                MessageBox.Show(this, successText, successText, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (ApplicationStorageMigrationException ex)
            {
                Debug.WriteLine("[StorageMigration] " + ex.Message);
                UpdateUserInstallationMenuText(ApplicationStorage.CurrentMode);
                ResumeActivityAfterStorageMigration(snapshot);
                snapshot = null;
                AnnounceStatusBarMessage(failureTitle);
                MessageBox.Show(
                    this,
                    BuildStorageMigrationErrorMessage(failureTitle, ex),
                    failureTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StorageMigration] 未预期异常: " + ex);
                UpdateUserInstallationMenuText(ApplicationStorage.CurrentMode);
                ResumeActivityAfterStorageMigration(snapshot);
                snapshot = null;
                AnnounceStatusBarMessage(failureTitle);
                MessageBox.Show(
                    this,
                    failureTitle + "。\r\n失败步骤：" + currentStep + "\r\n错误信息：" + ex.Message,
                    failureTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                ResumeActivityAfterStorageMigration(snapshot);
                _storageMigrationInProgress = false;
                UpdateUserInstallationMenuEntry();
            }
        }

        private void OnStorageMigrationProgress(ApplicationStorageMode targetMode, ApplicationStorageMigrationStage stage)
        {
            if (stage != ApplicationStorageMigrationStage.ReferencesUpdated || IsDisposed)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(() => UpdateUserInstallationMenuText(targetMode)));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void FlushStorageFilesBeforeMigration()
        {
            try
            {
                PersistWindowLayoutConfig();
                PersistPlaybackState();

                if (_configManager != null && _config != null)
                {
                    _configManager.Save(_config);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StorageMigration] 刷新配置文件失败: " + ex.Message);
                throw new InvalidOperationException("刷新当前配置文件失败: " + ex.Message, ex);
            }
        }

        private StorageMigrationActivitySnapshot PauseActivityForStorageMigration()
        {
            var snapshot = new StorageMigrationActivitySnapshot
            {
                UpdateTimerWasEnabled = _updateTimer?.Enabled == true,
                ScrubKeyTimerWasEnabled = _scrubKeyTimer?.Enabled == true,
                WindowLayoutPersistTimerWasEnabled = _windowLayoutPersistTimer?.Enabled == true,
                PlaylistOrderAutoSaveTimerWasEnabled = _playlistOrderAutoSaveTimer?.Enabled == true,
                SongOrderAutoSaveTimerWasEnabled = _songOrderAutoSaveTimer?.Enabled == true
            };

            _updateTimer?.Stop();
            _scrubKeyTimer?.Stop();
            _windowLayoutPersistTimer?.Stop();
            _playlistOrderAutoSaveTimer?.Stop();
            _songOrderAutoSaveTimer?.Stop();

            return snapshot;
        }

        private void ResumeActivityAfterStorageMigration(StorageMigrationActivitySnapshot? snapshot)
        {
            if (snapshot == null || IsDisposed)
            {
                return;
            }

            try
            {
                if (snapshot.UpdateTimerWasEnabled)
                {
                    _updateTimer?.Start();
                }

                if (snapshot.ScrubKeyTimerWasEnabled)
                {
                    _scrubKeyTimer?.Start();
                }

                if (snapshot.WindowLayoutPersistTimerWasEnabled)
                {
                    _windowLayoutPersistTimer?.Start();
                }

                if (snapshot.PlaylistOrderAutoSaveTimerWasEnabled)
                {
                    _playlistOrderAutoSaveTimer?.Start();
                }

                if (snapshot.SongOrderAutoSaveTimerWasEnabled)
                {
                    _songOrderAutoSaveTimer?.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StorageMigration] 恢复活动失败: " + ex.Message);
            }
        }

        private static string BuildStorageMigrationErrorMessage(
            string failureTitle,
            ApplicationStorageMigrationException ex)
        {
            string detail = ex.InnerException?.Message ?? ex.Message;
            string message = failureTitle + "。\r\n失败步骤：" + ex.StepName + "\r\n错误信息：" + detail;
            if (string.IsNullOrWhiteSpace(ex.RollbackError))
            {
                message += "\r\n已恢复原状。";
            }
            else
            {
                message += "\r\n回滚失败：" + ex.RollbackError;
            }

            return message;
        }

        private sealed class StorageMigrationActivitySnapshot
        {
            public bool UpdateTimerWasEnabled { get; set; }
            public bool ScrubKeyTimerWasEnabled { get; set; }
            public bool WindowLayoutPersistTimerWasEnabled { get; set; }
            public bool PlaylistOrderAutoSaveTimerWasEnabled { get; set; }
            public bool SongOrderAutoSaveTimerWasEnabled { get; set; }
        }
    }
}
