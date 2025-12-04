using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using UnblockNCM.Core.Logging;

namespace UnblockNCM.Core.Utils
{
    public static class ProcessRunner
    {
        public static async Task<(int exitCode, string stdout, string stderr)> RunAsync(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            var tcs = new TaskCompletionSource<int>();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderr.AppendLine(e.Data);
            };
            process.Exited += (_, __) =>
            {
                tcs.TrySetResult(process.ExitCode);
            };

            if (!process.Start())
                throw new InvalidOperationException($"Failed to start process {fileName}");

            Log.Info("process", $"running {fileName} {arguments}");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exit = await tcs.Task.ConfigureAwait(false);
            process.Close();
            return (exit, stdout.ToString(), stderr.ToString());
        }
    }
}
