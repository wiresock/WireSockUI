using System;
using System.Net;
using Windows.Data.Json;

namespace WireSockUI.Extensions
{
    internal static class GitHubExtensions
    {
        private const int ReleaseRequestTimeoutMilliseconds = 5000;
        private const int MaxReleaseResponseBytes = 1024 * 1024;

        /// <summary>
        ///     Retrieve latest published release version from GitHub
        /// </summary>
        /// <returns><see cref="T:Version" /> or null</returns>
        public static Version GetLatestRelease(string repository)
        {
            if (string.IsNullOrEmpty(repository)) return null;
            var request = WebRequest.CreateHttp($"https://api.github.com/repos/{repository}/releases/latest");

            request.Method = "GET";
            request.Accept = "application/vnd.github+json";
            request.UserAgent = "WireSockUI";
            request.Timeout = ReleaseRequestTimeoutMilliseconds;
            request.ReadWriteTimeout = ReleaseRequestTimeoutMilliseconds;

            using (var response = request.GetResponse())
            {
                if (response.ContentLength > MaxReleaseResponseBytes)
                    throw new InvalidOperationException(
                        $"The GitHub release response exceeds {MaxReleaseResponseBytes} bytes.");

                using (var responseStream = response.GetResponseStream())
                {
                    var data = BoundedStreamReader.ReadUtf8ToEnd(responseStream, MaxReleaseResponseBytes);

                    if (!JsonObject.TryParse(data, out var json)) return null;
                    if (!ReleaseVersionParser.TryParseReleaseTag(json.GetNamedString("tag_name"), out var version))
                        return null;

                    return version;
                }
            }
        }
    }
}
