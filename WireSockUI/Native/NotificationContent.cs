using System;
using System.IO;

namespace WireSockUI.Native
{
    internal static class NotificationContent
    {
        internal static string BuildLocalImageUri(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A notification image path is required.", nameof(path));

            return new Uri(Path.GetFullPath(path), UriKind.Absolute).AbsoluteUri;
        }
    }
}
