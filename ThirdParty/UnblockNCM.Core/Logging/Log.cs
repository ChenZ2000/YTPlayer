using System;
using System.Diagnostics;

namespace UnblockNCM.Core.Logging
{
    /// <summary>
    /// Lightweight logger used across the unblock core. It writes to Console and Debug listeners.
    /// </summary>
    public static class Log
    {
        public enum Level
        {
            Trace = 0,
            Debug = 1,
            Info = 2,
            Warn = 3,
            Error = 4,
        }

        private static Level _minLevel = Level.Info;

        public static Level MinLevel
        {
            get => _minLevel;
            set => _minLevel = value;
        }

        private static void Write(Level level, string scope, string message, Exception ex = null)
        {
            if (level < _minLevel) return;
            var prefix = $"[{DateTime.Now:HH:mm:ss}][{level}]";
            var scopePart = string.IsNullOrEmpty(scope) ? string.Empty : $"({scope}) ";
            var line = $"{prefix}{scopePart}{message}";
            if (ex != null) line += $" :: {ex}";
            Console.WriteLine(line);
            System.Diagnostics.Debug.WriteLine(line);
        }

        public static void Trace(string scope, string message) => Write(Level.Trace, scope, message);
        public static void Debug(string scope, string message) => Write(Level.Debug, scope, message);
        public static void Info(string scope, string message) => Write(Level.Info, scope, message);
        public static void Warn(string scope, string message, Exception ex = null) => Write(Level.Warn, scope, message, ex);
        public static void Error(string scope, string message, Exception ex = null) => Write(Level.Error, scope, message, ex);
    }
}
