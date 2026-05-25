using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VianaRego.Shared.Data;
using VianaRego.Shared.Models;
using VianaRego.Shared.Configuration;

namespace VianaRego.Shared.Logging
{
    /// <summary>
    /// Thread-safe logger that writes to both SQLite and shared log file with component prefixes.
    /// All Edge Agent system components write to EdgeAgent.log for unified logging.
    /// SQLite enables queryable, structured logging; text file provides fallback for debugging.
    /// </summary>
    public class SharedLogger
    {
        private readonly string _logFilePath;
        private readonly string _componentName;
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private static readonly System.Collections.Concurrent.ConcurrentQueue<(DateTime timestamp, string level, string message, string component, DbContextOptions<LocalDbContext> options)> _logQueue = new();
        private static bool _backgroundTaskStarted = false;
        private static readonly object _lock = new object();
        private readonly DbContextOptions<LocalDbContext> _dbOptions;

        public SharedLogger(string logFileName, string componentName)
        {
            var logDir = ResolveLogDirectory();
            _logFilePath = Path.Combine(logDir, logFileName);
            _componentName = componentName;
            
            // Initialize DbContext options for SQLite logging
            var dbPath = DataPath.GetPath("viana-edge.db");
            _dbOptions = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            EnsureBackgroundTaskStarted();
        }

        private void EnsureBackgroundTaskStarted()
        {
            lock (_lock)
            {
                if (_backgroundTaskStarted) return;
                _backgroundTaskStarted = true;

                Task.Run(async () =>
                {
                    while (true)
                    {
                        if (_logQueue.TryDequeue(out var log))
                        {
                            try
                            {
                                using var db = new LocalDbContext(log.options);
                                await db.Logs.AddAsync(new LogEntry
                                {
                                    Timestamp = log.timestamp,
                                    Component = log.component,
                                    Level = log.level,
                                    Message = log.message,
                                    Emoji = string.Empty
                                });
                                await db.SaveChangesAsync();
                            }
                            catch
                            {
                                // Silently fail SQLite write if DB unavailable
                            }
                        }
                        else
                        {
                            await Task.Delay(100);
                        }
                    }
                });
            }
        }

        public void LogInfo(string message, string emoji = "") => WriteLog("INFO", message);

        public void LogWarning(string message, string emoji = "") => WriteLog("WARN", message);

        public void LogError(string message, string emoji = "", string? details = null) => WriteLog("ERROR", message);

        public void LogSuccess(string message, string emoji = "") => WriteLog("INFO", message);

        private void WriteLog(string level, string message)
        {
            var timestamp = DateTime.UtcNow;
            var logLine = $"[{timestamp:yyyy-MM-dd HH:mm:ss}] [{_componentName}] {message}";

            _semaphore.Wait();
            try
            {
                // 1. Immediate file append (fast)
                TryAppendLogLine(logLine);
                
                // 2. Queue for background SQLite write (non-blocking)
                _logQueue.Enqueue((timestamp, level, message, _componentName, _dbOptions));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static string ResolveLogDirectory()
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MeldCX", "VianaRego", "logs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeldCX", "VianaRego", "logs"),
                Path.Combine(Path.GetTempPath(), "MeldCX", "VianaRego", "logs")
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
                catch
                {
                    // Try next candidate.
                }
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private void TryAppendLogLine(string logLine)
        {
            try
            {
                File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
            }
            catch
            {
                // Ignore file write failures. SQLite/console channels can still work.
            }
        }

        /// <summary>
        /// Get the full path to the log file
        /// </summary>
        public string LogFilePath => _logFilePath;
    }
}


