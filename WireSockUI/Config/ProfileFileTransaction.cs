using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace WireSockUI.Config
{
    internal static class ProfileFileTransaction
    {
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        public static void Commit(string temporaryPath, string destinationPath, string originalPath = null)
        {
            if (string.IsNullOrWhiteSpace(temporaryPath))
                throw new ArgumentException("A temporary profile path is required.", nameof(temporaryPath));
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("A destination profile path is required.", nameof(destinationPath));

            Profile.EnsureRegularProfileFile(temporaryPath);

            if (string.IsNullOrWhiteSpace(originalPath))
            {
                CommitWithoutRename(temporaryPath, destinationPath);
                return;
            }

            var fullOriginalPath = Path.GetFullPath(originalPath);
            var fullDestinationPath = Path.GetFullPath(destinationPath);
            if (string.Equals(fullOriginalPath, fullDestinationPath, StringComparison.Ordinal))
            {
                CommitWithoutRename(temporaryPath, destinationPath);
                return;
            }

            if (string.Equals(fullOriginalPath, fullDestinationPath, StringComparison.OrdinalIgnoreCase))
            {
                CommitCaseOnlyRename(temporaryPath, destinationPath, originalPath);
                return;
            }

            Profile.EnsureRegularProfileFile(originalPath);
            if (Profile.ProfilePathExists(destinationPath))
                throw new IOException($"The destination profile '{destinationPath}' already exists.");

            File.Move(temporaryPath, destinationPath);
            try
            {
                File.Delete(originalPath);
            }
            catch (Exception commitException)
            {
                try
                {
                    File.Move(destinationPath, temporaryPath);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "The profile rename failed and the temporary profile could not be restored.",
                        commitException,
                        rollbackException);
                }

                throw;
            }
        }

        private static void CommitCaseOnlyRename(string temporaryPath, string destinationPath, string originalPath)
        {
            Profile.EnsureRegularProfileFile(originalPath);
            MoveFileReplacingExisting(originalPath, destinationPath,
                $"Unable to rename profile '{originalPath}' to '{destinationPath}'.");
            try
            {
                CommitWithoutRename(temporaryPath, destinationPath);
            }
            catch (Exception commitException)
            {
                try
                {
                    MoveFileReplacingExisting(destinationPath, originalPath,
                        $"Unable to restore profile '{originalPath}'.");
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "The case-only profile rename failed and the original profile name could not be restored.",
                        commitException,
                        rollbackException);
                }

                throw;
            }
        }

        private static void CommitWithoutRename(string temporaryPath, string destinationPath)
        {
            if (Profile.ProfilePathExists(destinationPath))
            {
                Profile.EnsureRegularProfileFile(destinationPath);
                MoveFileReplacingExisting(temporaryPath, destinationPath,
                    $"Unable to replace profile '{destinationPath}'.");
                return;
            }

            File.Move(temporaryPath, destinationPath);
        }

        private static void MoveFileReplacingExisting(string existingPath, string destinationPath,
            string failureMessage)
        {
            if (MoveFileEx(existingPath, destinationPath, MoveFileReplaceExisting | MoveFileWriteThrough))
                return;

            var error = Marshal.GetLastWin32Error();
            throw new IOException(failureMessage, new Win32Exception(error));
        }
    }
}
