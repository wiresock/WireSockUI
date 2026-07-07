using System;
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
        private static bool _notificationIconReady;

        private static string EnsureNotificationIcon()
        {
            var icon = Path.Combine(Global.SecureMainFolder, "WireSock.ico");

            lock (IconSyncRoot)
            {
                if (_notificationIconReady && IsRegularNotificationIcon(icon))
                    return icon;

                Global.EnsureSecureMainFolderExists();

                if (TryUseExistingNotificationIcon(icon))
                    return icon;

                DeleteExistingNotificationIcon(icon);

                using (var stream = new FileStream(icon, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    Resources.ico.Save(stream);
                }

                File.SetAccessControl(icon, CreateNotificationIconFileSecurity());
                _notificationIconReady = true;
            }

            return icon;
        }

        private static bool IsRegularNotificationIcon(string icon)
        {
            return TryGetAttributes(icon, out var attributes) &&
                   (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }

        private static bool TryUseExistingNotificationIcon(string icon)
        {
            if (!IsRegularNotificationIcon(icon))
                return false;

            try
            {
                File.SetAccessControl(icon, CreateNotificationIconFileSecurity());
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Unable to refresh notification icon ACL: {ex.Message}");
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Unable to refresh notification icon ACL: {ex.Message}");
            }

            _notificationIconReady = true;
            return true;
        }

        private static void DeleteExistingNotificationIcon(string icon)
        {
            if (!TryGetAttributes(icon, out var attributes))
                return;

            if ((attributes & FileAttributes.Directory) == 0)
            {
                File.Delete(icon);
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(icon);
                return;
            }

            throw new InvalidOperationException(
                $"The notification icon path '{icon}' points to a directory. Remove the unexpected directory from the secured WireSockUI ProgramData folder and retry.");
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

        public static void Notify(string title, string body)
        {
            var icon = EnsureNotificationIcon();
            var context = WindowsApplicationContext.FromCurrentProcess();
            var notifier = ToastNotificationManager.CreateToastNotifier(context.AppUserModelId);

            var notification = new ToastNotification(GetXml(title, body, icon));

            notification.Activated += Notification_Activated;
            notification.Dismissed += Notification_Dismissed;
            notification.Failed += Notification_Failed;

            notifier.Show(notification);
        }

        private static void Notification_Failed(ToastNotification sender, ToastFailedEventArgs args)
        {
            Debug.WriteLine($"Notification failed: {args.ErrorCode}");
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
            foreach (Form form in Application.OpenForms)
                if (form.Name == "frmMain")
                    form.BeginInvoke((Action)(() =>
                    {
                        form.Show();
                        form.WindowState = FormWindowState.Normal;
                    }));
        }
    }
}
