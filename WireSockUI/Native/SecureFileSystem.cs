using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WireSockUI.Native
{
    internal static class SecureFileSystem
    {
        private const uint ReadControl = 0x00020000;
        private const uint GenericRead = 0x80000000;
        private const uint Delete = 0x00010000;
        private const uint WriteDac = 0x00040000;
        private const uint WriteOwner = 0x00080000;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint OwnerSecurityInformation = 0x00000001;
        private const uint DaclSecurityInformation = 0x00000004;
        private const uint ProtectedDaclSecurityInformation = 0x80000000;

        internal static bool AllowOwnerWriteFailureForTests { get; set; }

        internal static ValidatedHandle OpenDirectory(string path, bool writableSecurity)
        {
            return Open(path, true, writableSecurity, false, false);
        }

        internal static ValidatedHandle OpenFile(string path, bool writableSecurity)
        {
            return Open(path, false, writableSecurity, false, false);
        }

        internal static string ReadAllText(string path)
        {
            using (var file = Open(path, false, false, false, false, true))
                return file.ReadAllText();
        }

        internal static ValidatedHandle OpenFileForBoundedRead(string path, long maxBytes)
        {
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            var file = Open(path, false, false, false, false, true);
            try
            {
                file.EnsureMaximumLength(maxBytes);
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        internal static ValidatedHandle OpenFileForDelete(string path)
        {
            return Open(path, false, false, true, false);
        }

        internal static ValidatedHandle OpenFileForReadAndDelete(string path)
        {
            return Open(path, false, false, true, false, true);
        }

        internal static ValidatedHandle OpenReparsePointForDelete(string path, bool expectDirectory)
        {
            return Open(path, expectDirectory, false, true, true);
        }

        internal static IDisposable OpenDirectoryChain(string path)
        {
            var pending = new Stack<string>();
            var current = NormalizeComparablePath(path);
            while (!string.IsNullOrWhiteSpace(current))
            {
                pending.Push(current);
                var trimmed = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Path.GetDirectoryName(trimmed);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }

            var handles = new List<ValidatedHandle>();
            try
            {
                while (pending.Count > 0)
                    handles.Add(OpenDirectory(pending.Pop(), false));
                return new ValidatedHandleCollection(handles);
            }
            catch
            {
                DisposeHandles(handles);
                throw;
            }
        }

        private static ValidatedHandle Open(
            string path,
            bool expectDirectory,
            bool writableSecurity,
            bool allowDelete,
            bool expectReparsePoint,
            bool readContent = false)
        {
            var expectedPath = Path.GetFullPath(path);
            var desiredAccess = ReadControl;
            if (readContent)
                desiredAccess |= GenericRead;
            if (writableSecurity && !AllowOwnerWriteFailureForTests)
                desiredAccess |= WriteDac | WriteOwner;
            if (allowDelete)
                desiredAccess |= Delete;

            var handle = CreateFile(
                expectedPath,
                desiredAccess,
                readContent || allowDelete ? FileShare.Read : FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, $"Unable to open '{expectedPath}' without following reparse points.");
            }

            try
            {
                if (!GetFileInformationByHandle(handle, out var information))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"Unable to inspect the opened path '{expectedPath}'.");

                var attributes = (FileAttributes)information.FileAttributes;
                var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                if (isReparsePoint != expectReparsePoint)
                    throw new IOException(expectReparsePoint
                        ? $"'{expectedPath}' is no longer a reparse point."
                        : $"Refusing to use reparse point '{expectedPath}'.");

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory != expectDirectory)
                    throw new IOException($"'{expectedPath}' is not a regular {(expectDirectory ? "directory" : "file")}.");

                if ((writableSecurity || readContent) && !expectDirectory && information.NumberOfLinks != 1)
                    throw new IOException(
                        $"Refusing to use hard-linked file '{expectedPath}' ({information.NumberOfLinks} links).");

                var finalPath = GetFinalPath(handle);
                if (!PathsEqual(expectedPath, finalPath))
                    throw new IOException(
                        $"Opened path '{expectedPath}' resolved to unexpected object '{finalPath}'.");

                return new ValidatedHandle(handle, expectedPath, attributes, readContent);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static string GetFinalPath(SafeFileHandle handle)
        {
            var capacity = 512;
            while (capacity <= 32768)
            {
                var buffer = new StringBuilder(capacity);
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resolve an opened path.");

                if (length < buffer.Capacity)
                    return NormalizeFinalPath(buffer.ToString());

                capacity = checked((int)length + 1);
            }

            throw new PathTooLongException("The opened path exceeds the supported Windows path length.");
        }

        private static bool PathsEqual(string expectedPath, string finalPath)
        {
            return string.Equals(
                NormalizeComparablePath(expectedPath),
                NormalizeComparablePath(finalPath),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFinalPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string extendedPrefix = @"\\?\";
            if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(uncPrefix.Length);
            if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
                return path.Substring(extendedPrefix.Length);
            return path;
        }

        private static string NormalizeComparablePath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal sealed class ValidatedHandle : IDisposable
        {
            private readonly SafeFileHandle _handle;
            private readonly bool _canReadContent;

            internal ValidatedHandle(
                SafeFileHandle handle,
                string path,
                FileAttributes attributes,
                bool canReadContent)
            {
                _handle = handle;
                _canReadContent = canReadContent;
                Path = path;
                Attributes = attributes;
            }

            internal string Path { get; }
            internal FileAttributes Attributes { get; }

            internal string ReadAllText()
            {
                return ReadAllText(Encoding.UTF8);
            }

            internal string ReadAllText(Encoding encoding)
            {
                if (encoding == null) throw new ArgumentNullException(nameof(encoding));
                string contents = null;
                UseReadStream(stream =>
                {
                    using (var reader = new StreamReader(stream, encoding, true))
                        contents = reader.ReadToEnd();
                });
                return contents;
            }

            internal void EnsureMaximumLength(long maxBytes)
            {
                if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
                UseReadStream(stream =>
                {
                    if (stream.Length > maxBytes)
                        throw new InvalidDataException(
                            $"'{Path}' exceeds the maximum supported size of {maxBytes} bytes.");
                });
            }

            internal void CopyToNewFile(string destinationPath, long maxBytes)
            {
                if (maxBytes <= 0)
                    throw new ArgumentOutOfRangeException(nameof(maxBytes));

                UseReadStream(source =>
                {
                    if (source.Length > maxBytes)
                        throw new IOException($"'{Path}' exceeds the maximum supported size of {maxBytes} bytes.");

                    using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                               FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long bytesCopied = 0;
                        int bytesRead;
                        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (bytesRead > maxBytes - bytesCopied)
                                throw new IOException(
                                    $"'{Path}' exceeds the maximum supported size of {maxBytes} bytes.");

                            destination.Write(buffer, 0, bytesRead);
                            bytesCopied += bytesRead;
                        }
                    }
                });
            }

            private void UseReadStream(Action<FileStream> action)
            {
                if (action == null)
                    throw new ArgumentNullException(nameof(action));
                if (!_canReadContent)
                    throw new InvalidOperationException($"The validated handle for '{Path}' was not opened for reading.");

                var handleReferenceAdded = false;
                try
                {
                    _handle.DangerousAddRef(ref handleReferenceAdded);
                    using (var borrowedHandle = new SafeFileHandle(_handle.DangerousGetHandle(), false))
                    using (var stream = new FileStream(borrowedHandle, FileAccess.Read, 4096, false))
                    {
                        stream.Position = 0;
                        action(stream);
                    }
                }
                finally
                {
                    if (handleReferenceAdded)
                        _handle.DangerousRelease();
                }
            }

            internal void SetSecurity(FileSystemSecurity security)
            {
                if (security == null)
                    throw new ArgumentNullException(nameof(security));
                if (AllowOwnerWriteFailureForTests)
                    return;

                var descriptor = security.GetSecurityDescriptorBinaryForm();
                var descriptorHandle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
                try
                {
                    var descriptorPointer = descriptorHandle.AddrOfPinnedObject();
                    if (SetKernelObjectSecurity(
                            _handle,
                            OwnerSecurityInformation | DaclSecurityInformation |
                            ProtectedDaclSecurityInformation,
                            descriptorPointer))
                        return;

                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, $"Unable to secure '{Path}'.");
                }
                finally
                {
                    descriptorHandle.Free();
                }
            }

            internal void Delete()
            {
                var disposition = new FileDispositionInformation { DeleteFile = true };
                if (!SetFileInformationByHandle(
                        _handle,
                        FileInfoByHandleClass.FileDispositionInfo,
                        ref disposition,
                        (uint)Marshal.SizeOf<FileDispositionInformation>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to delete '{Path}'.");
            }

            public void Dispose()
            {
                _handle.Dispose();
            }
        }

        private sealed class ValidatedHandleCollection : IDisposable
        {
            private List<ValidatedHandle> _handles;

            internal ValidatedHandleCollection(List<ValidatedHandle> handles)
            {
                _handles = handles;
            }

            public void Dispose()
            {
                var handles = _handles;
                _handles = null;
                if (handles != null)
                    DisposeHandles(handles);
            }
        }

        private static void DisposeHandles(IList<ValidatedHandle> handles)
        {
            for (var index = handles.Count - 1; index >= 0; index--)
                handles[index].Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        private enum FileInfoByHandleClass
        {
            FileDispositionInfo = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInformation
        {
            [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetKernelObjectSecurity(
            SafeFileHandle handle,
            uint securityInformation,
            IntPtr securityDescriptor);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            ref FileDispositionInformation fileInformation,
            uint bufferSize);
    }
}
