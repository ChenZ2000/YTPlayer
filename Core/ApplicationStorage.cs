using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTPlayer.Models;
using YTPlayer.Models.Auth;
using YTPlayer.Utils;

namespace YTPlayer.Core
{
    internal enum ApplicationStorageMode
    {
        Portable,
        CurrentUser
    }

    internal enum ApplicationStorageMigrationStage
    {
        SourceFilesPrepared,
        SourceFilesReleased,
        TargetDirectoryCreated,
        ReferencesUpdated,
        FilesMigrated,
        Validated
    }

    internal sealed class ApplicationStorageMigrationResult
    {
        public ApplicationStorageMigrationResult(
            ApplicationStorageMode mode,
            string sourceDirectory,
            string targetDirectory,
            string accountFilePath,
            string configFilePath)
        {
            Mode = mode;
            SourceDirectory = sourceDirectory;
            TargetDirectory = targetDirectory;
            AccountFilePath = accountFilePath;
            ConfigFilePath = configFilePath;
        }

        public ApplicationStorageMode Mode { get; }
        public string SourceDirectory { get; }
        public string TargetDirectory { get; }
        public string AccountFilePath { get; }
        public string ConfigFilePath { get; }
    }

    internal sealed class ApplicationStorageMigrationException : Exception
    {
        public ApplicationStorageMigrationException(string stepName, Exception innerException, string? rollbackError)
            : base(BuildMessage(stepName, innerException, rollbackError), innerException)
        {
            StepName = stepName;
            RollbackError = rollbackError;
        }

        public string StepName { get; }
        public string? RollbackError { get; }

        private static string BuildMessage(string stepName, Exception innerException, string? rollbackError)
        {
            string message = innerException.Message;
            if (!string.IsNullOrWhiteSpace(rollbackError))
            {
                message += Environment.NewLine + "回滚失败: " + rollbackError;
            }

            return "步骤“" + stepName + "”失败: " + message;
        }
    }

    internal static class ApplicationStorage
    {
        internal const string AccountFileName = "account.json";
        internal const string ConfigFileName = "config.json";
        private const string StoragePointerFileName = "YTPlayer.storage.json";

        private static bool _initialized;
        private static ApplicationStorageMode _currentMode;
        private static string? _currentDataDirectory;

        internal static object SyncRoot { get; } = new object();

        internal static string PortableDataDirectory
        {
            get
            {
                try
                {
                    return Path.GetFullPath(PathHelper.ApplicationRootDirectory);
                }
                catch
                {
                    return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory);
                }
            }
        }

        internal static string CurrentUserDataDirectory
        {
            get
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(userProfile))
                {
                    userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(userProfile))
                {
                    userProfile = PortableDataDirectory;
                }

                return Path.GetFullPath(Path.Combine(userProfile, "Documents", "YTPlayer"));
            }
        }

        internal static string ModeFilePath => Path.Combine(PortableDataDirectory, StoragePointerFileName);

        internal static ApplicationStorageMode CurrentMode
        {
            get
            {
                lock (SyncRoot)
                {
                    EnsureInitializedCore();
                    return _currentMode;
                }
            }
        }

        internal static string CurrentDataDirectory
        {
            get
            {
                lock (SyncRoot)
                {
                    EnsureInitializedCore();
                    return _currentDataDirectory ?? PortableDataDirectory;
                }
            }
        }

        internal static string ConfigFilePath => Path.Combine(CurrentDataDirectory, ConfigFileName);

        internal static string AccountFilePath => Path.Combine(CurrentDataDirectory, AccountFileName);

        internal static bool IsCurrentUserInstalled => CurrentMode == ApplicationStorageMode.CurrentUser;

        internal static void WriteModePointer(ApplicationStorageMode mode, string targetDirectory)
        {
            lock (SyncRoot)
            {
                string modeFilePath = ModeFilePath;
                if (mode == ApplicationStorageMode.Portable)
                {
                    if (File.Exists(modeFilePath))
                    {
                        File.Delete(modeFilePath);
                    }

                    return;
                }

                string directory = string.IsNullOrWhiteSpace(targetDirectory)
                    ? CurrentUserDataDirectory
                    : Path.GetFullPath(targetDirectory);

                string? modeDirectory = Path.GetDirectoryName(modeFilePath);
                if (!string.IsNullOrWhiteSpace(modeDirectory))
                {
                    Directory.CreateDirectory(modeDirectory);
                }

                var pointer = new StoragePointer
                {
                    Mode = "CurrentUser",
                    UserDataDirectory = directory
                };

                string json = JsonConvert.SerializeObject(pointer, Formatting.Indented);
                File.WriteAllText(modeFilePath, json);
            }
        }

        internal static void SetRuntimeMode(ApplicationStorageMode mode, string? dataDirectory)
        {
            lock (SyncRoot)
            {
                _currentMode = mode;
                _currentDataDirectory = mode == ApplicationStorageMode.CurrentUser
                    ? Path.GetFullPath(string.IsNullOrWhiteSpace(dataDirectory) ? CurrentUserDataDirectory : dataDirectory)
                    : PortableDataDirectory;
                _initialized = true;
            }
        }

        private static void EnsureInitializedCore()
        {
            if (_initialized)
            {
                return;
            }

            _currentMode = ApplicationStorageMode.Portable;
            _currentDataDirectory = PortableDataDirectory;

            try
            {
                string modeFilePath = ModeFilePath;
                if (File.Exists(modeFilePath))
                {
                    string json = File.ReadAllText(modeFilePath);
                    var pointer = JsonConvert.DeserializeObject<StoragePointer>(json);
                    if (pointer != null &&
                        string.Equals(pointer.Mode, "CurrentUser", StringComparison.OrdinalIgnoreCase))
                    {
                        _currentMode = ApplicationStorageMode.CurrentUser;
                        _currentDataDirectory = string.IsNullOrWhiteSpace(pointer.UserDataDirectory)
                            ? CurrentUserDataDirectory
                            : Path.GetFullPath(pointer.UserDataDirectory);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ApplicationStorage] 读取存储位置失败，回退便携目录: " + ex.Message);
                _currentMode = ApplicationStorageMode.Portable;
                _currentDataDirectory = PortableDataDirectory;
            }

            _initialized = true;
        }

        private sealed class StoragePointer
        {
            public string? Mode { get; set; }
            public string? UserDataDirectory { get; set; }
        }
    }

    internal static class ApplicationStorageMigrator
    {
        private static readonly string[] ManagedFileNames =
        {
            ApplicationStorage.AccountFileName,
            ApplicationStorage.ConfigFileName
        };

        internal static ApplicationStorageMigrationResult MigrateTo(
            ApplicationStorageMode targetMode,
            Action<ApplicationStorageMigrationStage>? progress = null)
        {
            lock (ApplicationStorage.SyncRoot)
            {
                ApplicationStorageMode originalMode = ApplicationStorage.CurrentMode;
                string originalDirectory = ApplicationStorage.CurrentDataDirectory;
                string targetDirectory = targetMode == ApplicationStorageMode.CurrentUser
                    ? ApplicationStorage.CurrentUserDataDirectory
                    : ApplicationStorage.PortableDataDirectory;

                if (PathsEqual(originalDirectory, targetDirectory) && originalMode == targetMode)
                {
                    ValidateTargetState(targetMode, targetDirectory);
                    ReportProgress(progress, ApplicationStorageMigrationStage.Validated);
                    return BuildResult(targetMode, originalDirectory, targetDirectory);
                }

                string stepName = "准备迁移";
                List<FileSnapshot> snapshots = CaptureSnapshots(originalDirectory, targetDirectory);
                try
                {
                    stepName = "准备源配置文件";
                    EnsureSourceFiles(originalDirectory);
                    ReportProgress(progress, ApplicationStorageMigrationStage.SourceFilesPrepared);

                    stepName = "释放配置文件使用状态";
                    ConfirmManagedFilesReleased(originalDirectory);
                    ReportProgress(progress, ApplicationStorageMigrationStage.SourceFilesReleased);

                    stepName = "创建目标目录";
                    Directory.CreateDirectory(targetDirectory);
                    ReportProgress(progress, ApplicationStorageMigrationStage.TargetDirectoryCreated);

                    stepName = "更新配置引用位置";
                    ApplicationStorage.WriteModePointer(targetMode, targetDirectory);
                    ApplicationStorage.SetRuntimeMode(targetMode, targetDirectory);
                    ReportProgress(progress, ApplicationStorageMigrationStage.ReferencesUpdated);

                    stepName = "迁移配置文件";
                    CopyManagedFiles(originalDirectory, targetDirectory);
                    ReportProgress(progress, ApplicationStorageMigrationStage.FilesMigrated);

                    stepName = "删除旧配置文件";
                    DeleteManagedFiles(originalDirectory, targetDirectory);

                    stepName = "校验迁移结果";
                    ValidateTargetState(targetMode, targetDirectory);
                    ReportProgress(progress, ApplicationStorageMigrationStage.Validated);

                    return BuildResult(targetMode, originalDirectory, targetDirectory);
                }
                catch (Exception ex)
                {
                    string? rollbackError = null;
                    try
                    {
                        RestoreSnapshots(snapshots);
                        ApplicationStorage.SetRuntimeMode(originalMode, originalDirectory);
                    }
                    catch (Exception rollbackEx)
                    {
                        rollbackError = rollbackEx.Message;
                    }

                    CleanupTemporaryFiles(originalDirectory, targetDirectory);
                    throw new ApplicationStorageMigrationException(stepName, ex, rollbackError);
                }
            }
        }

        private static ApplicationStorageMigrationResult BuildResult(
            ApplicationStorageMode mode,
            string sourceDirectory,
            string targetDirectory)
        {
            return new ApplicationStorageMigrationResult(
                mode,
                sourceDirectory,
                targetDirectory,
                Path.Combine(targetDirectory, ApplicationStorage.AccountFileName),
                Path.Combine(targetDirectory, ApplicationStorage.ConfigFileName));
        }

        private static void EnsureSourceFiles(string sourceDirectory)
        {
            Directory.CreateDirectory(sourceDirectory);

            string configPath = Path.Combine(sourceDirectory, ApplicationStorage.ConfigFileName);
            if (!File.Exists(configPath))
            {
                ConfigModel config = ConfigManager.Instance.CreateDefaultConfig();
                WriteJson(configPath, config);
            }

            string accountPath = Path.Combine(sourceDirectory, ApplicationStorage.AccountFileName);
            if (!File.Exists(accountPath))
            {
                var state = new AccountState
                {
                    IsLoggedIn = false
                };
                WriteJson(accountPath, state);
            }
        }

        private static void ConfirmManagedFilesReleased(string directory)
        {
            foreach (string fileName in ManagedFileNames)
            {
                string path = Path.Combine(directory, fileName);
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }
            }
        }

        private static void CopyManagedFiles(string sourceDirectory, string targetDirectory)
        {
            foreach (string fileName in ManagedFileNames)
            {
                string sourcePath = Path.Combine(sourceDirectory, fileName);
                string targetPath = Path.Combine(targetDirectory, fileName);
                if (PathsEqual(sourcePath, targetPath))
                {
                    continue;
                }

                string tempPath = targetPath + ".migration.tmp";
                File.Copy(sourcePath, tempPath, true);
                File.Copy(tempPath, targetPath, true);
                File.Delete(tempPath);
            }
        }

        private static void DeleteManagedFiles(string sourceDirectory, string targetDirectory)
        {
            foreach (string fileName in ManagedFileNames)
            {
                string sourcePath = Path.Combine(sourceDirectory, fileName);
                string targetPath = Path.Combine(targetDirectory, fileName);
                if (PathsEqual(sourcePath, targetPath))
                {
                    continue;
                }

                if (File.Exists(sourcePath))
                {
                    File.Delete(sourcePath);
                }
            }
        }

        private static void ValidateTargetState(ApplicationStorageMode expectedMode, string targetDirectory)
        {
            if (ApplicationStorage.CurrentMode != expectedMode)
            {
                throw new InvalidOperationException("配置引用位置未切换到目标模式。");
            }

            if (!PathsEqual(ApplicationStorage.CurrentDataDirectory, targetDirectory))
            {
                throw new InvalidOperationException("配置引用位置未切换到目标目录。");
            }

            string accountPath = Path.Combine(targetDirectory, ApplicationStorage.AccountFileName);
            string configPath = Path.Combine(targetDirectory, ApplicationStorage.ConfigFileName);
            ValidateJsonFile(accountPath, ApplicationStorage.AccountFileName);
            ValidateJsonFile(configPath, ApplicationStorage.ConfigFileName);
            ConfirmManagedFilesReleased(targetDirectory);

            _ = ConfigManager.Instance.Load();
            _ = new AccountStateStore().Load();
        }

        private static void ValidateJsonFile(string path, string displayName)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("目标文件不存在: " + displayName, path);
            }

            string json = File.ReadAllText(path);
            _ = JToken.Parse(json);
        }

        private static void WriteJson<T>(string path, T value)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Include
            };
            File.WriteAllText(path, JsonConvert.SerializeObject(value, settings));
        }

        private static List<FileSnapshot> CaptureSnapshots(string sourceDirectory, string targetDirectory)
        {
            var paths = new List<string>
            {
                ApplicationStorage.ModeFilePath
            };

            foreach (string fileName in ManagedFileNames)
            {
                paths.Add(Path.Combine(sourceDirectory, fileName));
                paths.Add(Path.Combine(targetDirectory, fileName));
            }

            var snapshots = new List<FileSnapshot>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string fullPath = Path.GetFullPath(path);
                if (!seen.Add(fullPath))
                {
                    continue;
                }

                snapshots.Add(FileSnapshot.Capture(fullPath));
            }

            return snapshots;
        }

        private static void RestoreSnapshots(IEnumerable<FileSnapshot> snapshots)
        {
            foreach (FileSnapshot snapshot in snapshots)
            {
                snapshot.Restore();
            }
        }

        private static void CleanupTemporaryFiles(string sourceDirectory, string targetDirectory)
        {
            foreach (string directory in new[] { sourceDirectory, targetDirectory })
            {
                foreach (string fileName in ManagedFileNames)
                {
                    TryDelete(Path.Combine(directory, fileName + ".migration.tmp"));
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void ReportProgress(
            Action<ApplicationStorageMigrationStage>? progress,
            ApplicationStorageMigrationStage stage)
        {
            try
            {
                progress?.Invoke(stage);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ApplicationStorage] 迁移进度回调失败: " + ex.Message);
            }
        }

        private sealed class FileSnapshot
        {
            private FileSnapshot(string path, bool exists, byte[]? content)
            {
                Path = path;
                Exists = exists;
                Content = content;
            }

            private string Path { get; }
            private bool Exists { get; }
            private byte[]? Content { get; }

            public static FileSnapshot Capture(string path)
            {
                if (!File.Exists(path))
                {
                    return new FileSnapshot(path, false, null);
                }

                return new FileSnapshot(path, true, File.ReadAllBytes(path));
            }

            public void Restore()
            {
                if (!Exists)
                {
                    if (File.Exists(Path))
                    {
                        File.Delete(Path);
                    }

                    return;
                }

                string? directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(Path, Content ?? Array.Empty<byte>());
            }
        }
    }
}
