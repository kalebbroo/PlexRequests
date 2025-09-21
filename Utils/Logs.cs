using System.Collections.Concurrent;
using System.Text;

namespace PlexRequestsHosted.Utils;

/// <summary>Central internal logging handler.</summary>
public static class Logs
{
    /// <summary>Thread lock to prevent messages overlapping.</summary>
    public static readonly object ConsoleLock = new();

    /// <summary>Path to the current log file.</summary>
    public static string? LogFilePath;

    /// <summary>Queue of logs to save to file.</summary>
    public static ConcurrentQueue<string> LogsToSave = new();

    /// <summary>Thread for the loop that saves logs to file.</summary>
    public static Thread? LogSaveThread = null;

    /// <summary>Is Set when the log save thread is completed.</summary>
    public static ManualResetEvent LogSaveCompletion = new(false);

    /// <summary><see cref="Environment.TickCount64"/> time of the last log output.</summary>
    public static long LastLogTime = 0;

    /// <summary>Time between log messages after which the current full timestamp should be rendered.</summary>
    public static TimeSpan RepeatTimestampAfter = TimeSpan.FromMinutes(10);

    /// <summary>Called during program init, initializes the log saving to file (if enabled).</summary>
    public static void StartLogSaving()
    {
        // Set base path
        string basePath = "logs";
        Directory.CreateDirectory(basePath);
        // Create timestamped log file path
        DateTimeOffset time = DateTimeOffset.Now;
        string fileName = $"log_{time:yyyy-MM-dd_HH-mm}_{Environment.ProcessId}.txt";
        LogFilePath = Path.Combine(basePath, fileName);
        // Start the save thread
        LogSaveThread = new(LogSaveInternalLoop) { Name = "logsaver" };
        LogSaveThread.Start();
    }

    /// <summary>Internal thread loop for saving logs to file.</summary>
    public static void LogSaveInternalLoop()
    {
        while (true)
        {
            SaveLogsToFileOnce();
            try
            {
                Task.Delay(TimeSpan.FromSeconds(25)).Wait();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        LogSaveCompletion.Set();
    }

    /// <summary>Immediately saves the logs to file.</summary>
    public static void SaveLogsToFileOnce()
    {
        if (LogsToSave.IsEmpty || LogFilePath is null)
        {
            return;
        }
        StringBuilder toStore = new();
        while (LogsToSave.TryDequeue(out string line))
        {
            toStore.Append($"{line}\n");
        }
        if (toStore.Length > 0)
        {
            File.AppendAllText(LogFilePath, toStore.ToString());
            toStore.Clear();
        }
    }

    public enum LogLevel : int
    {
        Verbose, Debug, Info, Init, Warning, Error, None
    }

    /// <summary>Minimum primary logger log level.</summary>
    public static LogLevel MinimumLevel = LogLevel.Info;

    /// <summary>Log a verbose debug message, only in development mode.</summary>
    public static void Verbose(string message)
    {
        LogWithColor(ConsoleColor.Black, ConsoleColor.Gray, "Verbose", ConsoleColor.Black, ConsoleColor.Gray, message, LogLevel.Verbose);
    }

    /// <summary>Log a debug message, only in development mode.</summary>
    public static void Debug(string message)
    {
        LogWithColor(ConsoleColor.Black, ConsoleColor.Gray, "Debug", ConsoleColor.Black, ConsoleColor.Gray, message, LogLevel.Debug);
    }

    /// <summary>Log a basic info message.</summary>
    public static void Info(string message)
    {
        LogWithColor(ConsoleColor.Black, ConsoleColor.Cyan, "Info", ConsoleColor.Black, ConsoleColor.White, message, LogLevel.Info);
    }

    /// <summary>Log an initialization-related message.</summary>
    public static void Init(string message)
    {
        LogWithColor(ConsoleColor.Black, ConsoleColor.Green, "Init", ConsoleColor.Black, ConsoleColor.Gray, message, LogLevel.Init);
    }

    /// <summary>Log a warning message.</summary>
    public static void Warning(string message)
    {
        LogWithColor(ConsoleColor.Black, ConsoleColor.Yellow, "Warning", ConsoleColor.Black, ConsoleColor.Yellow, message, LogLevel.Warning);
    }

    /// <summary>Log an error message.</summary>
    public static void Error(string message)
    {
        LogWithColor(ConsoleColor.Black, ConsoleColor.Red, "Error", ConsoleColor.Black, ConsoleColor.Red, message, LogLevel.Error);
    }

    /// <summary>Internal path to log a message with a given color under lock.</summary>
    public static void LogWithColor(ConsoleColor prefixBackground, ConsoleColor prefixForeground, string prefix, ConsoleColor messageBackground, ConsoleColor messageForeground, string message, LogLevel level)
    {
        lock (ConsoleLock)
        {
            if (MinimumLevel > level)
            {
                return;
            }
            Console.BackgroundColor = ConsoleColor.Black;
            DateTimeOffset timestamp = DateTimeOffset.Now;
            if (Environment.TickCount64 - LastLogTime > RepeatTimestampAfter.TotalMilliseconds && LastLogTime != 0)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"== PlexRequests logs {timestamp:yyyy-MM-dd HH:mm} ==");
            }
            LastLogTime = Environment.TickCount64;
            string time = $"{timestamp:HH:mm:ss.fff}";
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write($"{time} [");
            Console.BackgroundColor = prefixBackground;
            Console.ForegroundColor = prefixForeground;
            Console.Write(prefix);
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write("] ");
            Console.BackgroundColor = messageBackground;
            Console.ForegroundColor = messageForeground;
            Console.WriteLine(message);
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.White;
            LogsToSave?.Enqueue($"{time} [{prefix}] {message}");
        }
    }
}
