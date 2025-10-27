using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using YTPlayer.Models;
using YTPlayer.Models.Auth;

namespace YTPlayer.Core
{
    /// <summary>
    /// 负责 account.json 的读写，统一管理登录状态和会话数据。
    /// account.json 包含：登录状态、用户信息、Cookie、设备指纹等。
    /// 用户可手动修改 IsLoggedIn 字段来测试不同登录状态。
    /// </summary>
    public class AccountStateStore
    {
        private readonly string _accountFilePath;

        public AccountStateStore()
        {
            string applicationDirectory = GetApplicationDirectory();
            _accountFilePath = Path.Combine(applicationDirectory, "account.json");
        }

        /// <summary>
        /// 加载 account.json。
        /// 如果文件不存在或读取失败，返回未登录状态。
        /// 如果 IsLoggedIn=false，程序将忽略其他账号数据，以未登录模式运行。
        /// </summary>
        public AccountState Load()
        {
            try
            {
                if (!File.Exists(_accountFilePath))
                {
                    System.Diagnostics.Debug.WriteLine("[AccountStateStore] account.json 不存在，返回未登录状态");
                    return CreateEmptyState();
                }

                string json = File.ReadAllText(_accountFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    System.Diagnostics.Debug.WriteLine("[AccountStateStore] account.json 为空，返回未登录状态");
                    return CreateEmptyState();
                }

                var state = JsonConvert.DeserializeObject<AccountState>(json);
                if (state == null)
                {
                    System.Diagnostics.Debug.WriteLine("[AccountStateStore] account.json 反序列化失败，返回未登录状态");
                    return CreateEmptyState();
                }

                // 确保集合不为空
                state.Cookies = state.Cookies ?? new List<CookieItem>();

                // 如果 IsLoggedIn=false，清空敏感数据（允许用户手动设置此标志来测试）
                if (!state.IsLoggedIn)
                {
                    System.Diagnostics.Debug.WriteLine("[AccountStateStore] IsLoggedIn=false，忽略账号数据");
                    // 保留设备指纹，但清空用户和认证信息
                    state.UserId = null;
                    state.Nickname = null;
                    state.AvatarUrl = null;
                    state.VipType = 0;
                    state.Cookie = null;
                    state.MusicU = null;
                    state.CsrfToken = null;
                    state.Cookies.Clear();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[AccountStateStore] 加载登录状态成功：用户={state.Nickname}, ID={state.UserId}, VipType={state.VipType}");
                }

                return state;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AccountStateStore] 读取 account.json 失败: {ex.Message}");
                return CreateEmptyState();
            }
        }

        /// <summary>
        /// 保存 AccountState 到 account.json。
        /// 使用原子写入方式（先写临时文件，再替换），并保留备份。
        /// </summary>
        public void Save(AccountState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            try
            {
                // 更新时间戳
                state.LastUpdated = DateTimeOffset.UtcNow;
                state.Cookies = state.Cookies ?? new List<CookieItem>();

                // 确保目录存在
                string directory = Path.GetDirectoryName(_accountFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 序列化配置
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Include  // 包含 null 值，以便用户清楚看到字段结构
                };

                string json = JsonConvert.SerializeObject(state, settings);

                // 原子写入：先写临时文件
                string tempFile = _accountFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                // 备份旧文件
                if (File.Exists(_accountFilePath))
                {
                    File.Copy(_accountFilePath, _accountFilePath + ".bak", true);
                }

                // 替换为新文件
                File.Copy(tempFile, _accountFilePath, true);
                File.Delete(tempFile);

                if (state.IsLoggedIn)
                {
                    System.Diagnostics.Debug.WriteLine($"[AccountStateStore] 保存登录状态成功：用户={state.Nickname}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[AccountStateStore] 保存未登录状态");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AccountStateStore] 保存 account.json 失败: {ex.Message}");
                throw;  // 抛出异常，让调用者知道保存失败
            }
        }

        /// <summary>
        /// 清空登录状态。
        /// 将 IsLoggedIn 设置为 false，清空用户和认证信息，但保留设备指纹。
        /// 不删除文件，允许保留设备指纹以便下次登录使用。
        /// </summary>
        public void Clear()
        {
            System.Diagnostics.Debug.WriteLine("[AccountStateStore] 清空登录状态");
            try
            {
                // 尝试加载现有状态以保留设备指纹
                AccountState state;
                if (File.Exists(_accountFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_accountFilePath);
                        state = JsonConvert.DeserializeObject<AccountState>(json) ?? CreateEmptyState();
                    }
                    catch
                    {
                        state = CreateEmptyState();
                    }
                }
                else
                {
                    state = CreateEmptyState();
                }

                // 清空登录状态和用户信息，但保留设备指纹
                state.IsLoggedIn = false;
                state.UserId = null;
                state.Nickname = null;
                state.AvatarUrl = null;
                state.VipType = 0;
                state.Cookie = null;
                state.MusicU = null;
                state.CsrfToken = null;
                state.Cookies?.Clear();

                // 保留设备指纹字段：
                // DeviceId, SDeviceId, DeviceMachineId, DeviceOs, DeviceOsVersion,
                // DeviceAppVersion, DeviceBuildVersion, DeviceVersionCode, DeviceUserAgent,
                // DeviceResolution, DeviceChannel, DeviceMobileName, DesktopUserAgent,
                // DeviceMark, DeviceMConfigInfo, MusicA, NmtId, NtesNuid, WnmCid,
                // AntiCheatToken, AntiCheatTokenGeneratedAt, AntiCheatTokenExpiresAt

                Save(state);
                System.Diagnostics.Debug.WriteLine("[AccountStateStore] 登录状态已清空，设备指纹已保留");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AccountStateStore] 清空登录状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查 account.json 是否存在
        /// </summary>
        public bool Exists()
        {
            return File.Exists(_accountFilePath);
        }

        /// <summary>
        /// 获取 account.json 文件路径
        /// </summary>
        public string GetFilePath()
        {
            return _accountFilePath;
        }

        /// <summary>
        /// 创建空的登录状态（未登录）
        /// </summary>
        private static AccountState CreateEmptyState()
        {
            return new AccountState
            {
                IsLoggedIn = false,
                Cookies = new List<CookieItem>()
            };
        }

        /// <summary>
        /// 获取应用程序目录。
        /// 优先返回当前 AppDomain 的 BaseDirectory（即可执行文件所在目录），
        /// 确保 account.json 与程序放置在同一目录，便于便携式部署。
        /// </summary>
        private static string GetApplicationDirectory()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir))
                {
                    return Path.GetFullPath(baseDir);
                }
            }
            catch
            {
                // 忽略 BaseDirectory 获取失败，回退到程序集路径
            }

            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string exeDir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    return Path.GetFullPath(exeDir);
                }
            }
            catch
            {
                // 忽略获取失败，回退到当前工作目录
            }

            return Directory.GetCurrentDirectory();
        }
    }
}
