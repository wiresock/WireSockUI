using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;

namespace WireSockUI.Native
{
    internal sealed class NativeRecoveryMarkerLease
    {
        internal NativeRecoveryMarkerLease(Guid operationId)
        {
            OperationId = operationId;
        }

        internal Guid OperationId { get; }
    }

    internal sealed class NativeRecoveryMarkerSnapshot
    {
        internal NativeRecoveryMarkerSnapshot(string contents, long generation)
        {
            Contents = contents;
            Generation = generation;
        }

        internal string Contents { get; }
        internal long Generation { get; }
    }

    internal sealed class NativeRecoveryMarkerStore
    {
        private const int MaxMarkerSizeBytes = 64 * 1024;
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;
        private const string OperationPrefix = "Operation: ";

        private readonly Func<FileSecurity> _createFileSecurity;
        private readonly Action _ensureDirectory;
        private readonly Func<string> _pathProvider;
        private readonly object _syncRoot = new object();
        private long _generation;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        internal NativeRecoveryMarkerStore(
            Func<string> pathProvider,
            Action ensureDirectory,
            Func<FileSecurity> createFileSecurity)
        {
            _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
            _ensureDirectory = ensureDirectory ?? throw new ArgumentNullException(nameof(ensureDirectory));
            _createFileSecurity = createFileSecurity ?? throw new ArgumentNullException(nameof(createFileSecurity));
        }

        internal NativeRecoveryMarkerLease Write(string context, string diagnostic)
        {
            var lease = new NativeRecoveryMarkerLease(Guid.NewGuid());
            lock (_syncRoot)
            {
                try
                {
                    WriteCore(lease, context, diagnostic);
                    return lease;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Failed to write WireSock UI native recovery marker: {ex.Message}");
                    return null;
                }
            }
        }

        internal bool TryUpdate(NativeRecoveryMarkerLease lease, string context, string diagnostic)
        {
            if (lease == null)
                return false;

            lock (_syncRoot)
            {
                try
                {
                    if (!CurrentMarkerBelongsTo(lease))
                        return false;

                    WriteCore(lease, context, diagnostic);
                    return true;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Failed to update WireSock UI native recovery marker: {ex.Message}");
                    return false;
                }
            }
        }

        internal string Read()
        {
            return Capture()?.Contents;
        }

        internal NativeRecoveryMarkerSnapshot Capture()
        {
            lock (_syncRoot)
            {
                var contents = ReadCore();
                return contents == null ? null : new NativeRecoveryMarkerSnapshot(contents, _generation);
            }
        }

        internal bool TryDelete(NativeRecoveryMarkerLease lease)
        {
            if (lease == null)
                return false;

            lock (_syncRoot)
            {
                try
                {
                    var path = _pathProvider();
                    if (!TryGetAttributes(path, out var attributes) ||
                        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        return false;

                    using (var marker = SecureFileSystem.OpenFileForBoundedReadAndDelete(path, MaxMarkerSizeBytes))
                    {
                        if (!MarkerBelongsTo(marker, lease))
                            return false;

                        marker.Delete();
                    }

                    _generation++;
                    return true;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Failed to delete WireSock UI native recovery marker: {ex.Message}");
                    return false;
                }
            }
        }

        internal bool TryDelete(NativeRecoveryMarkerSnapshot snapshot)
        {
            if (snapshot == null)
                return false;

            lock (_syncRoot)
            {
                try
                {
                    if (_generation != snapshot.Generation)
                        return false;

                    var path = _pathProvider();
                    if (!TryGetAttributes(path, out var attributes))
                        return false;

                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                    {
                        using (var marker = SecureFileSystem.OpenFileForReadAndDelete(path))
                        {
                            string currentContents;
                            try
                            {
                                marker.EnsureMaximumLength(MaxMarkerSizeBytes);
                                currentContents = marker.ReadAllText();
                            }
                            catch (Exception ex)
                            {
                                currentContents = CreateReadFailureDiagnostic(ex);
                            }

                            if (!string.Equals(currentContents, snapshot.Contents, StringComparison.Ordinal))
                                return false;

                            marker.Delete();
                        }
                    }
                    else
                    {
                        if (!string.Equals(ReadCore(), snapshot.Contents, StringComparison.Ordinal) ||
                            !DeletePathCore(path))
                            return false;
                    }

                    _generation++;
                    return true;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Failed to delete WireSock UI native recovery marker: {ex.Message}");
                    return false;
                }
            }
        }

        private void WriteCore(NativeRecoveryMarkerLease lease, string context, string diagnostic)
        {
            _ensureDirectory();
            var path = _pathProvider();
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var contextValue = TruncateUtf8(context ?? string.Empty, MaxMarkerSizeBytes / 4);
                var header =
                    $"UTC: {DateTime.UtcNow:o}{Environment.NewLine}" +
                    $"{OperationPrefix}{lease.OperationId:D}{Environment.NewLine}" +
                    $"Context: {contextValue}{Environment.NewLine}" +
                    "Diagnostic: ";
                var footer = Environment.NewLine;
                var diagnosticByteLimit = MaxMarkerSizeBytes - Encoding.UTF8.GetByteCount(header) -
                                          Encoding.UTF8.GetByteCount(footer);
                var diagnosticValue = TruncateUtf8(
                    diagnostic ?? "No diagnostic available.",
                    Math.Max(0, diagnosticByteLimit));
                var message = header + diagnosticValue + footer;

                using (var stream = SecureFileSystem.AllowOwnerWriteFailureForTests
                           ? new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                               FileOptions.WriteThrough)
                           : File.Create(
                               temporaryPath,
                               4096,
                               FileOptions.WriteThrough,
                               _createFileSecurity()))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(message);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (!MoveFileEx(temporaryPath, path, MoveFileReplaceExisting | MoveFileWriteThrough))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"Unable to atomically replace native recovery marker '{path}'.");

                temporaryPath = null;
                _generation++;
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning($"Failed to remove temporary native recovery marker: {ex.Message}");
                    }
                }
            }
        }

        private bool CurrentMarkerBelongsTo(NativeRecoveryMarkerLease lease)
        {
            var path = _pathProvider();
            if (!TryGetAttributes(path, out var attributes) ||
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;

            try
            {
                using (var marker = SecureFileSystem.OpenFileForBoundedRead(path, MaxMarkerSizeBytes))
                    return MarkerBelongsTo(marker, lease);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static bool MarkerBelongsTo(
            SecureFileSystem.ValidatedHandle marker,
            NativeRecoveryMarkerLease lease)
        {
            return TryReadOperationId(marker.ReadAllText(), out var operationId) &&
                   operationId == lease.OperationId;
        }

        private static string TruncateUtf8(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value) || maxBytes <= 0)
                return string.Empty;
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
                return value;

            const string suffix = "...";
            if (maxBytes <= suffix.Length)
                return suffix.Substring(0, maxBytes);

            var byteLimit = maxBytes - suffix.Length;
            var low = 0;
            var high = value.Length;
            while (low < high)
            {
                var candidateLength = low + (high - low + 1) / 2;
                if (Encoding.UTF8.GetByteCount(value.Substring(0, candidateLength)) <= byteLimit)
                    low = candidateLength;
                else
                    high = candidateLength - 1;
            }

            if (low > 0 && char.IsHighSurrogate(value[low - 1]))
                low--;
            return value.Substring(0, low) + suffix;
        }

        private string ReadCore()
        {
            try
            {
                var path = _pathProvider();
                if (!TryGetAttributes(path, out var attributes))
                    return null;

                if ((attributes & FileAttributes.Directory) != 0)
                    return "The native recovery marker is a directory and was not read.";

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return "The native recovery marker is a reparse point and was not read.";

                using (var marker = SecureFileSystem.OpenFileForBoundedRead(path, MaxMarkerSizeBytes))
                    return marker.ReadAllText();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex)
            {
                var diagnostic = CreateReadFailureDiagnostic(ex);
                Trace.TraceWarning($"Failed to read WireSock UI native recovery marker: {ex.Message}");
                return diagnostic;
            }
        }

        private static string CreateReadFailureDiagnostic(Exception exception)
        {
            return $"The native recovery marker could not be read: {exception.Message}";
        }

        private static bool TryReadOperationId(string contents, out Guid operationId)
        {
            using (var reader = new StringReader(contents ?? string.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith(OperationPrefix, StringComparison.Ordinal))
                        return Guid.TryParse(line.Substring(OperationPrefix.Length), out operationId);
                }
            }

            operationId = Guid.Empty;
            return false;
        }

        private static bool DeletePathCore(string path)
        {
            if (!TryGetAttributes(path, out var attributes))
                return false;

            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    using (var marker = SecureFileSystem.OpenReparsePointForDelete(path, true))
                        marker.Delete();
                }
                else
                {
                    Directory.Delete(path, false);
                }

                return true;
            }

            using (var marker = (attributes & FileAttributes.ReparsePoint) != 0
                       ? SecureFileSystem.OpenReparsePointForDelete(path, false)
                       : SecureFileSystem.OpenFileForDelete(path))
                marker.Delete();
            return true;
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = 0;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = 0;
                return false;
            }
        }
    }
}
