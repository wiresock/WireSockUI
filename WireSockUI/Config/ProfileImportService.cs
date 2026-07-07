using System;
using System.Diagnostics;
using System.IO;
using WireSockUI.Native;

namespace WireSockUI.Config
{
    internal static class ProfileImportService
    {
        private const long MaxImportedProfileSizeBytes = 1024 * 1024;

        public static string CopyToTemporaryProfileFile(string sourcePath)
        {
            Global.EnsureConfigsFolder();
            var tmpProfile = Path.Combine(Global.ConfigsFolder, $"{Guid.NewGuid():N}.tmp");

            try
            {
                CopyProfileToTemporaryFile(sourcePath, tmpProfile);
                return tmpProfile;
            }
            catch
            {
                TryDeleteTemporaryProfile(tmpProfile);
                throw;
            }
        }

        public static void CopyProfileToTemporaryFile(string sourcePath, string destinationPath)
        {
            RegularFileSource.CopyToTemporaryFile(
                sourcePath,
                destinationPath,
                MaxImportedProfileSizeBytes,
                "profile",
                "The profile file is too large to be imported.");
        }

        public static void TryDeleteTemporaryProfile(string tmpProfile)
        {
            try
            {
                if (File.Exists(tmpProfile))
                    File.Delete(tmpProfile);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete temporary imported profile '{tmpProfile}': {ex.Message}");
            }
        }
    }
}
