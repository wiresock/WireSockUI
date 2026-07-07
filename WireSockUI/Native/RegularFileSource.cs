using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WireSockUI.Native
{
    internal static class RegularFileSource
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint OpenExisting = 3;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagSequentialScan = 0x08000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out ByHandleFileInformation lpFileInformation);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ByHandleFileInformation
        {
            public FileAttributes FileAttributes;
            public FileTime CreationTime;
            public FileTime LastAccessTime;
            public FileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        public static FileStream OpenForRead(string sourcePath, string sourceDescription)
        {
            var handle = CreateFile(
                sourcePath,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var errorCode = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(
                    $"Unable to open {sourceDescription} '{Path.GetFileName(sourcePath)}'.",
                    new Win32Exception(errorCode));
            }

            try
            {
                if (!GetFileInformationByHandle(handle, out var fileInformation))
                    throw new IOException(
                        $"Unable to inspect {sourceDescription} '{Path.GetFileName(sourcePath)}'.",
                        new Win32Exception(Marshal.GetLastWin32Error()));

                if ((fileInformation.FileAttributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(
                        $"{ToSentenceCase(sourceDescription)} '{Path.GetFileName(sourcePath)}' is a reparse point.");

                if ((fileInformation.FileAttributes & FileAttributes.Directory) != 0)
                    throw new IOException(
                        $"{ToSentenceCase(sourceDescription)} '{Path.GetFileName(sourcePath)}' is a directory.");

                return new FileStream(handle, FileAccess.Read, 81920, false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public static void CopyToTemporaryFile(
            string sourcePath,
            string destinationPath,
            long maxBytes,
            string sourceDescription,
            string tooLargeMessage)
        {
            var destinationCreated = false;

            try
            {
                using (var source = OpenForRead(sourcePath, sourceDescription))
                {
                    if (source.Length > maxBytes)
                        throw new InvalidOperationException(tooLargeMessage);

                    using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                               FileShare.None))
                    {
                        destinationCreated = true;
                        var buffer = new byte[81920];
                        long bytesCopied = 0;
                        int bytesRead;

                        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            bytesCopied += bytesRead;
                            if (bytesCopied > maxBytes)
                                throw new InvalidOperationException(tooLargeMessage);

                            destination.Write(buffer, 0, bytesRead);
                        }
                    }
                }
            }
            catch
            {
                if (destinationCreated)
                    TryDeleteIncompleteDestination(destinationPath);
                throw;
            }
        }

        private static void TryDeleteIncompleteDestination(string destinationPath)
        {
            try
            {
                if (File.Exists(destinationPath))
                    File.Delete(destinationPath);
            }
            catch
            {
                // Keep the original copy/open error.
            }
        }

        private static string ToSentenceCase(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }
    }
}
