using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WireSockUI;
using WireSockUI.Config;
using WireSockUI.Native;

namespace WireSockUI.Tests
{
    internal static partial class Program
    {
        private const uint DirectoryMutationGenericWrite = 0x40000000;
        private const uint DirectoryReadGenericRead = 0x80000000;
        private const uint DirectoryMutationOpenExisting = 3;
        private const uint DirectoryMutationBackupSemantics = 0x02000000;
        private const uint DirectoryMutationOpenReparsePoint = 0x00200000;
        private const uint FsctlSetReparsePoint = 0x000900a4;
        private const uint IoReparseTagMountPoint = 0xa0000003;
        private const int AccessDeniedError = 5;
        private const int SharingViolationError = 32;

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle OpenDirectoryForMutationTest(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            byte[] inputBuffer,
            uint inputBufferSize,
            IntPtr outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);

        private static SafeFileHandle OpenDirectoryForMutationTest(string path)
        {
            return OpenDirectoryForMutationTest(
                path,
                DirectoryMutationGenericWrite,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                DirectoryMutationOpenExisting,
                DirectoryMutationBackupSemantics | DirectoryMutationOpenReparsePoint,
                IntPtr.Zero);
        }

        private static SafeFileHandle OpenDirectoryForReadTest(string path)
        {
            return OpenDirectoryForMutationTest(
                path,
                DirectoryReadGenericRead,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                DirectoryMutationOpenExisting,
                DirectoryMutationBackupSemantics | DirectoryMutationOpenReparsePoint,
                IntPtr.Zero);
        }

        private static void NotificationShortcutParentMutationRaceIsBlocked()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var programsDirectory = Path.Combine(root, "Programs");
            var redirectTarget = Path.Combine(
                Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var stagedShortcut = Path.Combine(root, "trusted-stage.lnk");
            var destinationShortcut = Path.Combine(programsDirectory, "WireSockUI.lnk");
            var redirectedShortcut = Path.Combine(redirectTarget, "WireSockUI.lnk");
            var parentMutationApplied = false;
            var parentMutationError = 0;

            try
            {
                Directory.CreateDirectory(programsDirectory);
                Directory.CreateDirectory(redirectTarget);
                File.WriteAllBytes(stagedShortcut, new byte[] { 1, 2, 3, 4 });

                using (var readHandle = OpenDirectoryForReadTest(programsDirectory))
                {
                    AssertFalse(readHandle.IsInvalid,
                        "Expected the test directory to allow a pre-existing read handle.");
                    using (SecureFileSystem.OpenDirectoryChainForStableChildCreation(programsDirectory))
                    {
                    }
                }

                SecurityIdentifier currentUserSid;
                using (var currentIdentity = WindowsIdentity.GetCurrent())
                    currentUserSid = currentIdentity.User;
                var destinationSecurity = new FileSecurity();
                destinationSecurity.SetAccessRuleProtection(true, false);
                destinationSecurity.SetOwner(currentUserSid);
                destinationSecurity.AddAccessRule(new FileSystemAccessRule(
                    currentUserSid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));

                using (SecureFileSystem.OpenDirectoryChainForStableChildCreation(programsDirectory))
                    WindowsApplicationContext.InstallTrustedNotificationShortcut(
                        stagedShortcut,
                        destinationShortcut,
                        null,
                        null,
                        destinationSecurity);
                AssertTrue(
                    File.ReadAllBytes(destinationShortcut).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                    "Expected stable-chain protection to preserve normal child publication.");
                File.Delete(destinationShortcut);

                Exception observedFailure = null;
                using (var mutationHandle = OpenDirectoryForMutationTest(programsDirectory))
                {
                    AssertFalse(mutationHandle.IsInvalid,
                        "Expected the test directory to allow a mutation-capable handle before stabilization.");

                    using (SecureFileSystem.OpenDirectoryChainForStableChildCreation(programsDirectory))
                    {
                        try
                        {
                            WindowsApplicationContext.InstallTrustedNotificationShortcut(
                                stagedShortcut,
                                destinationShortcut,
                                () =>
                                {
                                    var reparseBuffer = BuildMountPointReparseBuffer(redirectTarget);
                                    if (DeviceIoControl(
                                            mutationHandle,
                                            FsctlSetReparsePoint,
                                            reparseBuffer,
                                            (uint)reparseBuffer.Length,
                                            IntPtr.Zero,
                                            0,
                                            out _,
                                            IntPtr.Zero))
                                    {
                                        parentMutationApplied = true;
                                        return;
                                    }

                                    parentMutationError = Marshal.GetLastWin32Error();
                                    throw new InvalidOperationException(
                                        $"The parent mutation was blocked with Win32 error {parentMutationError}.");
                                },
                                null,
                                destinationSecurity);
                        }
                        catch (Exception ex)
                        {
                            observedFailure = ex;
                        }
                    }
                }

                if (!parentMutationApplied)
                {
                    if (parentMutationError != SharingViolationError &&
                        parentMutationError != AccessDeniedError)
                        SkipOrFail(
                            $"directory junction mutation unavailable (Win32 error {parentMutationError}); " +
                            "notification parent-redirection check not exercised.");

                    AssertTrue(observedFailure is InvalidOperationException,
                        "Expected the stable parent chain to fail the attempted mutation closed.");
                }
                else
                {
                    AssertTrue(observedFailure is IOException,
                        "Expected handle validation to reject a shortcut created through a redirected parent.");
                    AssertTrue(
                        observedFailure.Message.IndexOf(
                            "resolved to unexpected object", StringComparison.OrdinalIgnoreCase) >= 0,
                        $"Expected a final-path mismatch diagnostic, got '{observedFailure.Message}'.");
                }

                AssertFalse(File.Exists(destinationShortcut),
                    "Expected no shortcut to remain at the requested destination.");
                AssertFalse(File.Exists(redirectedShortcut),
                    "Expected handle-based cleanup to remove the shortcut from the redirected directory.");
            }
            finally
            {
                TryDeleteFile(destinationShortcut);
                TryDeleteFile(redirectedShortcut);
                TryDeleteFile(stagedShortcut);
                TryDeleteDirectory(programsDirectory, false);
                TryDeleteDirectory(root, true);
                TryDeleteDirectory(redirectTarget, true);
            }
        }

        private static byte[] BuildMountPointReparseBuffer(string targetDirectory)
        {
            var printName = Path.GetFullPath(targetDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var substituteName = @"\??\" + printName;
            var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
            var printBytes = Encoding.Unicode.GetBytes(printName);
            var pathBytesLength = checked(substituteBytes.Length + 2 + printBytes.Length + 2);
            var buffer = new byte[checked(16 + pathBytesLength)];

            WriteUInt32(buffer, 0, IoReparseTagMountPoint);
            WriteUInt16(buffer, 4, checked((ushort)(buffer.Length - 8)));
            WriteUInt16(buffer, 8, 0);
            WriteUInt16(buffer, 10, checked((ushort)substituteBytes.Length));
            WriteUInt16(buffer, 12, checked((ushort)(substituteBytes.Length + 2)));
            WriteUInt16(buffer, 14, checked((ushort)printBytes.Length));
            Buffer.BlockCopy(substituteBytes, 0, buffer, 16, substituteBytes.Length);
            Buffer.BlockCopy(printBytes, 0, buffer, 18 + substituteBytes.Length, printBytes.Length);
            return buffer;
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
        }

        private static void GlobalRejectsUntrustedPreexistingSecureData()
        {
            var originalSecureMainFolder = Global.SecureMainFolder;
            var originalOwnerWriteFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(directory);
                var preseededSettings = Path.Combine(directory, "PrivilegedSettings.xml");
                File.WriteAllText(preseededSettings, "attacker-controlled");
                AssertTrue(WireSockUI.Program.IsPotentiallyUserWritableDirectory(directory),
                    "Expected the test directory to represent a non-administrator-controlled pre-seed.");
                var securityBefore = Directory.GetAccessControl(directory).GetSecurityDescriptorBinaryForm();

                Global.SecureMainFolder = directory;
                SecureFileSystem.AllowOwnerWriteFailureForTests = false;
                AssertThrows<UnauthorizedAccessException>(
                    () => Global.EnsureSecureMainFolderExists(),
                    "Refusing to change security");

                var securityAfter = Directory.GetAccessControl(directory).GetSecurityDescriptorBinaryForm();
                AssertTrue(securityBefore.SequenceEqual(securityAfter),
                    "Expected startup trust validation not to rewrite the untrusted directory ACL.");
                AssertEqual("attacker-controlled", File.ReadAllText(preseededSettings));
            }
            finally
            {
                Global.SecureMainFolder = originalSecureMainFolder;
                SecureFileSystem.AllowOwnerWriteFailureForTests = originalOwnerWriteFailure;
                TryDeleteDirectory(directory, true);
            }
        }

        private static void LegacyMigrationCleansManagedOrphansBeforeCatalogLimit()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                Directory.CreateDirectory(pendingFolder);
                for (var index = 0; index <= LegacyProfileMigrationService.MaxLegacyCatalogEntries; index++)
                    File.WriteAllText(
                        Path.Combine(pendingFolder, Guid.NewGuid().ToString("N") + ".tmp"),
                        "orphan");

                File.WriteAllText(Path.Combine(pendingFolder, "office.conf"), ValidConfig());
                var pendingNames = LegacyProfileMigrationService.GetPendingProfileNames();

                AssertEqual(1, pendingNames.Count);
                AssertEqual("office", pendingNames[0]);
                AssertFalse(Directory.EnumerateFiles(pendingFolder, "*.tmp").Any(),
                    "Expected managed migration temporaries to be removed before applying the catalog limit.");
            });
        }

        private static void ProfileTransactionRecoveryCleansManagedOrphansBeforeEntryLimit()
        {
            WithTemporaryConfigFolder(() =>
            {
                Global.EnsureProfileTransactionsFolderExists();
                for (var index = 0; index <= 256; index++)
                    File.WriteAllText(
                        Path.Combine(
                            Global.ProfileTransactionsFolder,
                            Guid.NewGuid().ToString("N") + ".profile.tmp"),
                        "orphan");

                ProfileFileTransaction.RecoverInterruptedTransactions();

                AssertFalse(Directory.EnumerateFileSystemEntries(Global.ProfileTransactionsFolder).Any(),
                    "Expected managed transaction temporaries to be removed before applying the entry limit.");
            });
        }

        private static void ShellLinkPropVariantInteropIsSafe()
        {
            var propVariantType = typeof(ShellLink).GetNestedType(
                "PropVariant", BindingFlags.NonPublic);
            if (propVariantType == null)
                throw new InvalidOperationException("ShellLink.PropVariant was not found.");

            AssertEqual(ShellLink.NativePropVariantSize, Marshal.SizeOf(propVariantType));
            AssertEqual(IntPtr.Size == 8 ? 24 : 16, ShellLink.NativePropVariantSize);

            var propVariant = Activator.CreateInstance(propVariantType, true);
            try
            {
                propVariantType.GetProperty("VarType")?.SetValue(
                    propVariant, VarEnum.VT_UI4, null);
                try
                {
                    propVariantType.GetProperty("Value")?.GetValue(propVariant, null);
                    throw new InvalidOperationException(
                        "Expected a non-string PROPVARIANT to be rejected.");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
                {
                }
            }
            finally
            {
                (propVariant as IDisposable)?.Dispose();
            }

            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var shortcutPath = Path.Combine(directory, "property.lnk");
            var targetPath = Assembly.GetExecutingAssembly().Location;
            const string appUserModelId = "WireSock.Foundation.Tests.PropVariant";
            try
            {
                Directory.CreateDirectory(directory);
                using (var shortcut = new ShellLink
                {
                    TargetPath = targetPath,
                    AppUserModelId = appUserModelId
                })
                    shortcut.Save(shortcutPath);

                using (var shortcut = new ShellLink(shortcutPath))
                    AssertEqual(appUserModelId, shortcut.AppUserModelId);
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }
    }
}
