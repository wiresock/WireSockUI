using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using System.Xml.Linq;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using WireSockUI.Native;
using WireSockUI.Properties;

namespace WireSockUI.Notifications
{
    internal static class Notifications
    {
        private static readonly object IconSyncRoot = new object();
        private static readonly object ActivationSyncRoot = new object();
        private static readonly Lazy<WindowsApplicationContext> ApplicationContext =
            new Lazy<WindowsApplicationContext>(() => WindowsApplicationContext.FromCurrentProcess());
        private static bool _notificationIconReady;
        private static WeakReference _activationForm;

        private static string EnsureNotificationIcon()
        {
            var icon = Path.Combine(Global.NotificationAssetsFolder, "WireSock.ico");

            lock (IconSyncRoot)
            {
                if (_notificationIconReady && IsRegularNotificationIcon(icon))
                    return icon;

                Global.EnsureNotificationAssetsFolderExists();

                var iconBytes = CreateNotificationIconBytes();
                if (!TryCreateFreshNotificationIcon(icon, iconBytes, out var replaceDiagnostic))
                {
                    if (TryReuseExistingNotificationIcon(icon, iconBytes, out var reuseDiagnostic))
                    {
                        Trace.TraceWarning(
                            $"Reusing verified notification icon '{icon}' because it could not be replaced: {replaceDiagnostic}");
                        _notificationIconReady = true;
                        return icon;
                    }

                    throw new IOException(
                        $"Could not prepare notification icon '{icon}'. Replace failed: {replaceDiagnostic}; reuse failed: {reuseDiagnostic}");
                }

                using (var iconHandle = SecureFileSystem.OpenFile(icon, true))
                    iconHandle.SetSecurity(CreateNotificationIconFileSecurity());
                _notificationIconReady = true;
            }

            return icon;
        }

        private static byte[] CreateNotificationIconBytes()
        {
            using (var stream = new MemoryStream())
            {
                Resources.ico.Save(stream);
                return stream.ToArray();
            }
        }

        private static bool TryCreateFreshNotificationIcon(string icon, byte[] iconBytes, out string diagnostic)
        {
            try
            {
                DeleteExistingNotificationIcon(icon);

                using (var stream = new FileStream(icon, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(iconBytes, 0, iconBytes.Length);
                }

                diagnostic = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException ||
                                       ex is UnauthorizedAccessException ||
                                       ex is Win32Exception ||
                                       ex is InvalidOperationException)
            {
                diagnostic = ex.Message;
                return false;
            }
        }

        private static bool TryReuseExistingNotificationIcon(string icon, byte[] expectedIconBytes, out string diagnostic)
        {
            if (!IsRegularNotificationIcon(icon))
            {
                diagnostic = "existing icon is missing, a directory, or a reparse point";
                return false;
            }

            if (Program.IsPotentiallyUserWritableFile(icon))
            {
                diagnostic = "existing icon is writable by or owned by non-administrative users";
                return false;
            }

            try
            {
                if (!FileContentsEqual(icon, expectedIconBytes))
                {
                    diagnostic = "existing icon contents do not match the bundled icon";
                    return false;
                }

                using (var iconHandle = SecureFileSystem.OpenFile(icon, true))
                    iconHandle.SetSecurity(CreateNotificationIconFileSecurity());
                diagnostic = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException ||
                                       ex is UnauthorizedAccessException ||
                                       ex is Win32Exception ||
                                       ex is InvalidOperationException)
            {
                diagnostic = ex.Message;
                return false;
            }
        }

        private static bool FileContentsEqual(string path, byte[] expectedBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.Read))
            {
                if (stream.Length != expectedBytes.Length)
                    return false;

                for (var i = 0; i < expectedBytes.Length; i++)
                {
                    if (stream.ReadByte() != expectedBytes[i])
                        return false;
                }

                return true;
            }
        }

        private static bool IsRegularNotificationIcon(string icon)
        {
            return TryGetAttributes(icon, out var attributes) &&
                   (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }

        private static void DeleteExistingNotificationIcon(string icon)
        {
            if (!TryGetAttributes(icon, out var attributes))
                return;

            if ((attributes & FileAttributes.Directory) == 0)
            {
                using (var iconHandle = (attributes & FileAttributes.ReparsePoint) != 0
                           ? SecureFileSystem.OpenReparsePointForDelete(icon, false)
                           : SecureFileSystem.OpenFileForDelete(icon))
                    iconHandle.Delete();
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                using (var iconHandle = SecureFileSystem.OpenReparsePointForDelete(icon, true))
                    iconHandle.Delete();
                return;
            }

            throw new InvalidOperationException(
                $"The notification icon path '{icon}' points to a directory. Remove the unexpected directory from the WireSockUI notification assets folder and retry.");
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            attributes = default(FileAttributes);

            try
            {
                attributes = File.GetAttributes(path);
                return true;
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

        private static FileSecurity CreateNotificationIconFileSecurity()
        {
            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var security = new FileSecurity();

            security.SetAccessRuleProtection(true, false);
            security.SetOwner(administratorsSid);
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                administratorsSid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.ReadAndExecute,
                AccessControlType.Allow));

            return security;
        }

        private static XmlDocument GetXml(string title, string body, string icon)
        {
            var toast =
                new XElement("toast",
                    new XElement("visual",
                        new XElement("binding",
                            new XAttribute("template", "ToastGeneric"),
                            new XElement("text", title),
                            new XElement("text", body),
                            new XElement("image",
                                new XAttribute("src", icon),
                                new XAttribute("placement", "appLogoOverride"),
                                new XAttribute("hint-crop", "circle")))));

            var xml = new XmlDocument();
            xml.LoadXml(toast.ToString());

            return xml;
        }

        public static void Notify(string title, string body, Form activationForm)
        {
            if (activationForm == null)
                throw new ArgumentNullException(nameof(activationForm));

            lock (ActivationSyncRoot)
                _activationForm = new WeakReference(activationForm);

            var context = ApplicationContext.Value;
            if (!context.NotificationShortcutReady)
                return;

            var icon = EnsureNotificationIcon();
            var notifier = ToastNotificationManager.CreateToastNotifier(context.AppUserModelId);

            var notification = new ToastNotification(GetXml(title, body, icon));

            notification.Activated += Notification_Activated;
            notification.Dismissed += Notification_Dismissed;
            notification.Failed += Notification_Failed;

            notifier.Show(notification);
        }

        private static void Notification_Failed(ToastNotification sender, ToastFailedEventArgs args)
        {
            Trace.TraceWarning($"Notification failed: {args.ErrorCode}");
        }

        private static void Notification_Dismissed(ToastNotification sender, ToastDismissedEventArgs args)
        {
            switch (args.Reason)
            {
                case ToastDismissalReason.ApplicationHidden:
                    Debug.WriteLine("Notification dismissed: Application hidden");
                    break;
                case ToastDismissalReason.UserCanceled:
                    Debug.WriteLine("Notification dismissed: User cancelled");
                    break;
                case ToastDismissalReason.TimedOut:
                    Debug.WriteLine("Notification dismissed: Timed out");
                    break;
            }
        }

        private static void Notification_Activated(ToastNotification sender, object args)
        {
            Form form;
            lock (ActivationSyncRoot)
                form = _activationForm?.Target as Form;

            if (form == null || form.IsDisposed || form.Disposing || !form.IsHandleCreated)
                return;

            try
            {
                form.BeginInvoke((Action)(() =>
                {
                    if (form.IsDisposed || form.Disposing)
                        return;

                    form.Show();
                    form.WindowState = FormWindowState.Normal;
                    form.Activate();
                }));
            }
            catch (ObjectDisposedException)
            {
                // The main window completed shutdown while the WinRT callback was being dispatched.
            }
            catch (InvalidOperationException ex)
            {
                Trace.TraceWarning($"Unable to activate WireSock UI from a toast notification: {ex.Message}");
            }
        }
    }
}
