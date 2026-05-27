using System;
using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

namespace Everboot.Logging;

internal static class LoggingSetup
{
    public static void Configure(ILoggingBuilder logging, IHostEnvironment env)
    {
        logging.ClearProviders();

        var minLevel = env.IsDevelopment() ? LogLevel.Debug : LogLevel.Information;
        logging.SetMinimumLevel(minLevel);

        var logDirectory = ResolveLogDirectory();
        Directory.CreateDirectory(logDirectory);

        // Console: writes to stderr so VS Code's Debug Console / stdout protocols
        // are not polluted when the debugger captures stdout for the program.
        logging.AddZLoggerConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
            options.UsePlainTextFormatter(formatter =>
            {
                formatter.SetPrefixFormatter(
                    $"{0:yyyy-MM-dd HH:mm:ss.fff} [{1:short}] [{2}] ",
                    (in MessageTemplate template, in LogInfo info) =>
                        template.Format(info.Timestamp.Local.DateTime, info.LogLevel, info.Category.Name));
            });
        });

        // File: daily-rolled JSON with size cap; ZLogger rolls automatically.
        logging.AddZLoggerRollingFile(options =>
        {
            options.FilePathSelector = (timestamp, sequenceNumber) =>
                Path.Combine(logDirectory, $"everboot-{timestamp.ToLocalTime():yyyy-MM-dd}.{sequenceNumber:000}.log");
            options.RollingInterval = RollingInterval.Day;
            options.RollingSizeKB = 10 * 1024; // 10 MB per file
            options.UseJsonFormatter();
        });
    }

    private static string ResolveLogDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("EVERBOOT_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(AppContext.BaseDirectory, "logs");
        }

        // Linux/macOS containers and services: prefer /var/log when writable, else local.
        const string varLog = "/var/log/everboot";
        try
        {
            Directory.CreateDirectory(varLog);
            using var probe = File.Create(Path.Combine(varLog, ".writetest"), 1, FileOptions.DeleteOnClose);
            return varLog;
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "logs");
        }
    }
}
