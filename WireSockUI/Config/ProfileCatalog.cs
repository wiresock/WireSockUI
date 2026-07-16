using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WireSockUI.Config
{
    internal sealed class ProfileCatalogResult
    {
        private ProfileCatalogResult(IReadOnlyList<string> profiles, Exception exception)
        {
            Profiles = profiles ?? Array.Empty<string>();
            Exception = exception;
        }

        internal IReadOnlyList<string> Profiles { get; }
        internal Exception Exception { get; }
        internal bool Succeeded => Exception == null;

        internal static ProfileCatalogResult Success(IReadOnlyList<string> profiles)
        {
            return new ProfileCatalogResult(profiles, null);
        }

        internal static ProfileCatalogResult Failure(Exception exception)
        {
            return new ProfileCatalogResult(null, exception ?? throw new ArgumentNullException(nameof(exception)));
        }
    }

    internal sealed class ProfileCatalog
    {
        private readonly Func<IReadOnlyList<string>> _loadProfiles;

        internal ProfileCatalog() : this(Profile.GetProfiles)
        {
        }

        internal ProfileCatalog(Func<IReadOnlyList<string>> loadProfiles)
        {
            _loadProfiles = loadProfiles ?? throw new ArgumentNullException(nameof(loadProfiles));
        }

        internal ProfileCatalogResult Load()
        {
            try
            {
                var loadedProfiles = _loadProfiles() ??
                                     throw new InvalidDataException("The profile loader returned no catalog.");
                var duplicate = loadedProfiles
                    .GroupBy(profile => profile, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate != null)
                    throw new InvalidDataException(
                        $"Profile names differ only by letter case: {string.Join(", ", duplicate.OrderBy(name => name, StringComparer.Ordinal))}. Rename one of these files before continuing.");

                var profiles = loadedProfiles
                    .OrderBy(profile => profile, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(profile => profile, StringComparer.Ordinal)
                    .ToArray();
                return ProfileCatalogResult.Success(profiles);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to enumerate WireSock UI profiles: {ex}");
                return ProfileCatalogResult.Failure(ex);
            }
        }
    }
}
