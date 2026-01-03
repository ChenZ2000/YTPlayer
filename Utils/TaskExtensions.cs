using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace YTPlayer.Utils
{
    internal static class TaskExtensions
    {
        public static void SafeFireAndForget(this Task task, string? context = null)
        {
            if (task == null)
            {
                return;
            }

            task.ContinueWith(
                t =>
                {
                    var exception = t.Exception?.GetBaseException();
                    if (exception == null)
                    {
                        return;
                    }

                    string message = string.IsNullOrWhiteSpace(context)
                        ? "Fire-and-forget task failed."
                        : context;

                    DebugLogger.LogException("Task", exception, message);
                },
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
