using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FrontLineOverlay
{
    /// <summary>
    /// Log local + Windows Error Reporting (Watson / Partner Center).
    ///
    /// Exceções não recuperáveis NÃO são engolidas: o processo termina para a
    /// Store coletar o dump. O crash.log é anexado ao WER via WerRegisterFile.
    /// Sem PDB/.appxsym a Store continua mostrando unknown_function — veja
    /// docs/partner-center-crashes.md.
    /// </summary>
    public static class CrashReporter
    {
        private const int WerRegFileTypeOther = 2;
        private const int WerFileAnonymousData = 2;

        [DllImport("wer.dll", CharSet = CharSet.Unicode)]
        private static extern int WerRegisterFile(string pwzFile, int regFileType, int flags);

        private static readonly object Gate = new();
        private static bool _installed;
        private static string? _logDir;
        private static string? _crashLogPath;
        private static string? _sessionLogPath;

        public static string LogDirectory
        {
            get { EnsurePaths(); return _logDir!; }
        }

        public static string CrashLogPath
        {
            get { EnsurePaths(); return _crashLogPath!; }
        }

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            EnsurePaths();
            RotateIfHuge(_crashLogPath!, 2_000_000);
            RotateIfHuge(_sessionLogPath!, 1_000_000);

            try { WerRegisterFile(_crashLogPath!, WerRegFileTypeOther, WerFileAnonymousData); }
            catch { }

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Log(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"),
                    "AppDomain.UnhandledException", terminating: e.IsTerminating);
            };

            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (e.Exception is OutOfMemoryException)
                    Log(e.Exception, "FirstChance.OutOfMemory");
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log(e.Exception, "TaskScheduler.UnobservedTaskException");
                e.SetObserved();
            };

            if (Application.Current != null)
                Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;

            Info($"CrashReporter ativo. Logs em {LogDirectory}");
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log(e.Exception, "DispatcherUnhandledException");

            if (IsRecoverable(e.Exception))
            {
                TryRecoverMemory();
                e.Handled = true;
                return;
            }

            // Deixa o Watson/Store coletar o dump. Não marque Handled.
            e.Handled = false;
        }

        public static bool IsRecoverable(Exception ex) =>
            ex is OutOfMemoryException or TimeoutException;

        public static void TryRecoverMemory()
        {
            try
            {
                Info("Tentando recuperar memória após OOM.");
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            catch { }
        }

        public static void Info(string message) => Write("INFO", message, null);

        public static void Log(Exception ex, string source = "", bool terminating = false)
        {
            var header = terminating ? "FATAL" : "ERROR";
            var msg = string.IsNullOrEmpty(source) ? ex.GetType().FullName : $"{source}: {ex.GetType().FullName}";
            Write(header, msg, ex);
            TryWriteEventLog(ex, source, terminating);
        }

        public static void LogPythonExit(int exitCode, string? detail = null)
        {
            Write("ERROR", $"FrontlineServer.exe saiu com código {exitCode}. {detail}", null);
        }

        public static void LogBreadcrumb(string where, string? extra = null)
        {
            Write("TRACE", extra == null ? where : $"{where} | {extra}", null, sessionOnly: true);
        }

        private static void Write(string level, string message, Exception? ex, bool sessionOnly = false)
        {
            try
            {
                EnsurePaths();
                var sb = new StringBuilder();
                sb.Append(DateTime.UtcNow.ToString("o"));
                sb.Append(" [").Append(level).Append("] ");
                sb.Append(message);
                if (ex != null)
                {
                    sb.AppendLine();
                    sb.AppendLine(ex.ToString());
                    if (ex.InnerException != null)
                    {
                        sb.AppendLine("--- inner ---");
                        sb.AppendLine(ex.InnerException.ToString());
                    }
                }
                sb.AppendLine();
                var text = sb.ToString();
                lock (Gate)
                {
                    File.AppendAllText(_sessionLogPath!, text, Encoding.UTF8);
                    if (!sessionOnly)
                        File.AppendAllText(_crashLogPath!, text, Encoding.UTF8);
                }
            }
            catch { }
        }

        private static void EnsurePaths()
        {
            if (_logDir != null) return;
            _logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FrontLineLyrics", "logs");
            Directory.CreateDirectory(_logDir);
            _crashLogPath = Path.Combine(_logDir, "crash.log");
            _sessionLogPath = Path.Combine(_logDir, "session.log");
        }

        private static void RotateIfHuge(string path, long maxBytes)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < maxBytes) return;
                var bak = path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
            catch { }
        }

        private static void TryWriteEventLog(Exception ex, string source, bool terminating)
        {
            try
            {
                var text = $"FrontLine Lyrics {(terminating ? "FATAL" : "ERROR")} {source}\n{ex}";
                EventLog.WriteEntry("Application", text.Length > 7000 ? text[..7000] : text,
                    terminating ? EventLogEntryType.Error : EventLogEntryType.Warning);
            }
            catch { }
        }
    }
}
