using System;
using System.Collections.Generic;

namespace WireSockUI.Native
{
    public class ProcessEntry
    {
        public ProcessEntry(int processId, string name, string imageName, string user)
        {
            ProcessId = processId;
            Name = name;
            ImageName = imageName;
            User = user;
        }

        /// <summary>
        ///     Unique process identifier
        /// </summary>
        public int ProcessId { get; private set; }

        /// <summary>
        ///     Process display name
        /// </summary>
        public string Name { get; }

        /// <summary>
        ///     Process image executable path
        /// </summary>
        public string ImageName { get; private set; }

        /// <summary>
        ///     Process username
        /// </summary>
        public string User { get; private set; }

        public static IEqualityComparer<ProcessEntry> Comparer { get; } = new ProcessEntryEqualityComparer();

        private sealed class ProcessEntryEqualityComparer : IEqualityComparer<ProcessEntry>
        {
            public bool Equals(ProcessEntry x, ProcessEntry y)
            {
                return y != null && x != null && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(ProcessEntry p)
            {
                return p?.Name != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(p.Name) : 0;
            }
        }
    }
}
