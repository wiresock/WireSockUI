using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WireSockUI.Native;

namespace WireSockUI.Diagnostics
{
    internal sealed class SecureRollingTraceListener : TraceListener
    {
        private const long DefaultMaximumBytes = 1024 * 1024;
        private const long MinimumMaximumBytes = 256;
        private const int DefaultRetainedFiles = 3;
        private const string TruncationSuffix = " ... [truncated]";
        private static readonly Regex SecretAssignmentPattern = new Regex(
            @"(?im)\b(PrivateKey|PresharedKey|Socks5ProxyPassword)\s*=\s*[^\r\n]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex UriCredentialPattern = new Regex(
            @"(?i)(\b[a-z][a-z0-9+.-]*://)[^\s/@:]+:[^\s/@]+@",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly string _logPath;
        private readonly long _maximumBytes;
        private readonly int _retainedFiles;
        private readonly object _syncRoot = new object();
        private long _currentLength;
        private StreamWriter _writer;

        internal SecureRollingTraceListener(
            string logPath,
            long maximumBytes = DefaultMaximumBytes,
            int retainedFiles = DefaultRetainedFiles)
        {
            if (string.IsNullOrWhiteSpace(logPath))
                throw new ArgumentException("A diagnostic log path is required.", nameof(logPath));
            if (maximumBytes < MinimumMaximumBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (retainedFiles < 1)
                throw new ArgumentOutOfRangeException(nameof(retainedFiles));

            _logPath = Path.GetFullPath(logPath);
            _maximumBytes = maximumBytes;
            _retainedFiles = retainedFiles;
        }

        internal static void Initialize()
        {
            Global.EnsureDiagnosticsFolderExists();
            var listener = new SecureRollingTraceListener(Global.DiagnosticLogPath);
            try
            {
                listener.PrepareForUse();
                Trace.Listeners.Add(listener);
                Trace.AutoFlush = true;
                Trace.TraceInformation(
                    $"WireSock UI diagnostics started. Version={typeof(Program).Assembly.GetName().Version}; " +
                    $"ProcessArchitecture={(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        internal static string Redact(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message ?? string.Empty;

            var redacted = SecretAssignmentPattern.Replace(message, "$1 = [REDACTED]");
            return UriCredentialPattern.Replace(redacted, "$1[REDACTED]@");
        }

        public override void Write(string message)
        {
            WriteRecord(TraceEventType.Information, message);
        }

        public override void WriteLine(string message)
        {
            WriteRecord(TraceEventType.Information, message);
        }

        public override void TraceEvent(
            TraceEventCache eventCache,
            string source,
            TraceEventType eventType,
            int id,
            string message)
        {
            WriteRecord(eventType, message);
        }

        public override void TraceEvent(
            TraceEventCache eventCache,
            string source,
            TraceEventType eventType,
            int id,
            string format,
            params object[] args)
        {
            WriteRecord(eventType, SafeFormat(format, args));
        }

        public override void TraceData(
            TraceEventCache eventCache,
            string source,
            TraceEventType eventType,
            int id,
            object data)
        {
            WriteRecord(eventType, SafeToString(data));
        }

        public override void Flush()
        {
            lock (_syncRoot)
            {
                try
                {
                    _writer?.Flush();
                }
                catch (Exception)
                {
                    CloseWriter();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_syncRoot)
                    CloseWriter();
            }

            base.Dispose(disposing);
        }

        internal void PrepareForUse()
        {
            lock (_syncRoot)
            {
                ValidateExistingLogPath(_logPath);
                for (var index = 1; index <= _retainedFiles; index++)
                    ValidateExistingLogPath(GetArchivePath(index));

                EnsureWriter();
            }
        }

        private void WriteRecord(TraceEventType eventType, string message)
        {
            lock (_syncRoot)
            {
                try
                {
                    EnsureWriter();
                    var recordPrefix = $"{DateTime.UtcNow:o} [{eventType}] ";
                    var maximumRecordBytes = checked((int)Math.Min(
                        int.MaxValue,
                        _maximumBytes - Encoding.UTF8.GetByteCount(Environment.NewLine)));
                    var record = TruncateUtf8(recordPrefix + Redact(message), maximumRecordBytes);
                    var recordBytes = Encoding.UTF8.GetByteCount(record + Environment.NewLine);
                    if (_currentLength + recordBytes > _maximumBytes)
                    {
                        CloseWriter();
                        RotateFiles();
                        EnsureWriter();
                    }

                    _writer.WriteLine(record);
                    _currentLength += recordBytes;
                    if (Trace.AutoFlush)
                        _writer.Flush();
                }
                catch (Exception)
                {
                    CloseWriter();
                }
            }
        }

        private static string TruncateUtf8(string value, int maximumBytes)
        {
            if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
                return value;

            var suffixBytes = Encoding.UTF8.GetByteCount(TruncationSuffix);
            var contentBudget = maximumBytes - suffixBytes;
            if (contentBudget <= 0)
                return string.Empty;

            var low = 0;
            var high = value.Length;
            while (low < high)
            {
                var candidate = low + (high - low + 1) / 2;
                if (Encoding.UTF8.GetByteCount(value.Substring(0, candidate)) <= contentBudget)
                    low = candidate;
                else
                    high = candidate - 1;
            }

            if (low > 0 && low < value.Length && char.IsHighSurrogate(value[low - 1]))
                low--;

            return value.Substring(0, low) + TruncationSuffix;
        }

        private static string SafeFormat(string format, object[] args)
        {
            try
            {
                return args == null || args.Length == 0
                    ? format
                    : string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (Exception ex)
            {
                return $"{format ?? string.Empty} [diagnostic formatting failed: {ex.GetType().Name}]";
            }
        }

        private static string SafeToString(object data)
        {
            try
            {
                return data?.ToString();
            }
            catch (Exception ex)
            {
                return $"[diagnostic value formatting failed: {ex.GetType().Name}]";
            }
        }

        private void EnsureWriter()
        {
            if (_writer != null)
                return;

            var directory = Path.GetDirectoryName(_logPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("The diagnostic log path has no containing directory.");

            Directory.CreateDirectory(directory);
            if (!File.Exists(_logPath))
            {
                using (new FileStream(_logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                }
            }

            using (var logFile = SecureFileSystem.OpenFile(_logPath, true))
                logFile.SetSecurity(Global.CreateAdministratorsOnlyFileSecurity());

            var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            _currentLength = stream.Length;
        }

        private static void ValidateExistingLogPath(string path)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException($"Diagnostic log path '{path}' is not a regular file.");

            using (var file = SecureFileSystem.OpenFile(path, true))
                file.SetSecurity(Global.CreateAdministratorsOnlyFileSecurity());
        }

        private void RotateFiles()
        {
            for (var index = _retainedFiles - 1; index >= 1; index--)
            {
                var source = GetArchivePath(index);
                var destination = GetArchivePath(index + 1);
                DeleteRegularFileIfPresent(destination);
                if (File.Exists(source))
                    File.Move(source, destination);
            }

            var firstArchive = GetArchivePath(1);
            DeleteRegularFileIfPresent(firstArchive);
            if (File.Exists(_logPath))
                File.Move(_logPath, firstArchive);
        }

        private string GetArchivePath(int index)
        {
            return _logPath + "." + index.ToString(CultureInfo.InvariantCulture);
        }

        private static void DeleteRegularFileIfPresent(string path)
        {
            if (!File.Exists(path))
                return;

            using (var file = SecureFileSystem.OpenFileForDelete(path))
                file.Delete();
        }

        private void CloseWriter()
        {
            var writer = _writer;
            _writer = null;
            _currentLength = 0;
            if (writer == null)
                return;

            try
            {
                writer.Dispose();
            }
            catch (Exception)
            {
                // Diagnostics must never destabilize application shutdown or error handling.
            }
        }
    }
}
