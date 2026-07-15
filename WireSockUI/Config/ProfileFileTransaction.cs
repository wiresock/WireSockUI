using System;
using System.IO;

namespace WireSockUI.Config
{
    internal static class ProfileFileTransaction
    {
        public static void Commit(string temporaryPath, string destinationPath, string originalPath = null)
        {
            if (string.IsNullOrWhiteSpace(temporaryPath))
                throw new ArgumentException("A temporary profile path is required.", nameof(temporaryPath));
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("A destination profile path is required.", nameof(destinationPath));

            Profile.EnsureRegularProfileFile(temporaryPath);

            if (string.IsNullOrWhiteSpace(originalPath) ||
                string.Equals(Path.GetFullPath(originalPath), Path.GetFullPath(destinationPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                CommitWithoutRename(temporaryPath, destinationPath);
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

        private static void CommitWithoutRename(string temporaryPath, string destinationPath)
        {
            if (Profile.ProfilePathExists(destinationPath))
            {
                Profile.EnsureRegularProfileFile(destinationPath);
                File.Replace(temporaryPath, destinationPath, null);
                return;
            }

            File.Move(temporaryPath, destinationPath);
        }
    }
}
