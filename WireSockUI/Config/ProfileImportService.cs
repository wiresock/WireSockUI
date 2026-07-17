using WireSockUI.Native;

namespace WireSockUI.Config
{
    internal static class ProfileImportService
    {
        public static string CopyToTemporaryProfileFile(string sourcePath)
        {
            var tmpProfile = ProfileFileTransaction.CreateTemporaryProfilePath();

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
                Profile.MaxProfileSizeBytes,
                "profile",
                "The profile file is too large to be imported.");
        }

        public static void TryDeleteTemporaryProfile(string tmpProfile)
        {
            ProfileFileTransaction.TryDeleteTemporaryProfile(tmpProfile);
        }
    }
}
