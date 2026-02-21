using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    internal static class IconAssetProvider
    {
        private const string RootEnvVar = "YTPLAYER_ROOT";
        private const string RootMarkerName = "YTPlayer.exe";
        private const string IconRelativePath = "libs\\assets\\Icon.png";

        private static readonly object IconLock = new object();
        private static Icon? _cachedIcon;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static string ResolveIconPngPath()
        {
            string root = ResolveApplicationRoot();
            return Path.Combine(root, IconRelativePath);
        }

        public static Icon GetAppIcon()
        {
            lock (IconLock)
            {
                if (_cachedIcon != null)
                {
                    return _cachedIcon;
                }

                _cachedIcon = LoadIconFromPng(ResolveIconPngPath()) ?? SystemIcons.Application;
                return _cachedIcon;
            }
        }

        public static bool TryApplyFormIcon(Form? form)
        {
            if (form == null || form.IsDisposed)
            {
                return false;
            }

            try
            {
                form.Icon = GetAppIcon();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Icon? LoadIconFromPng(string pngPath)
        {
            if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
            {
                return null;
            }

            IntPtr hIcon = IntPtr.Zero;
            try
            {
                using Image source = Image.FromFile(pngPath);
                int targetSize = ResolveTargetSize(source);
                using Bitmap bitmap = new Bitmap(source, new Size(targetSize, targetSize));
                hIcon = bitmap.GetHicon();
                using Icon unsafeIcon = Icon.FromHandle(hIcon);
                return (Icon)unsafeIcon.Clone();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hIcon != IntPtr.Zero)
                {
                    _ = DestroyIcon(hIcon);
                }
            }
        }

        private static int ResolveTargetSize(Image source)
        {
            int maxSide = Math.Max(source.Width, source.Height);
            if (maxSide <= 0)
            {
                return 32;
            }

            if (maxSide >= 256)
            {
                return 256;
            }

            if (maxSide >= 128)
            {
                return 128;
            }

            if (maxSide >= 64)
            {
                return 64;
            }

            return 32;
        }

        private static string ResolveApplicationRoot()
        {
            try
            {
                string? fromEnv = Environment.GetEnvironmentVariable(RootEnvVar);
                if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
                {
                    return Path.GetFullPath(fromEnv);
                }
            }
            catch
            {
            }

            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
                string candidate = Path.GetFullPath(baseDir);

                if (File.Exists(Path.Combine(candidate, RootMarkerName)))
                {
                    return candidate;
                }

                string trimmed = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string leaf = Path.GetFileName(trimmed);
                if (string.Equals(leaf, "libs", StringComparison.OrdinalIgnoreCase))
                {
                    string? parent = Path.GetDirectoryName(trimmed);
                    if (!string.IsNullOrWhiteSpace(parent) &&
                        File.Exists(Path.Combine(parent, RootMarkerName)))
                    {
                        return parent;
                    }
                }

                return candidate;
            }
            catch
            {
                return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory);
            }
        }
    }
}
