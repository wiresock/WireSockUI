using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace WireSockUI.Native
{
    /// <summary>
    ///     Native API class to retrieve specific size icons from icongroups in Windows resources
    /// </summary>
    internal static class WindowsIcons
    {
        /*
         * Icons of interest
         *  New: 2
         *  Open: 3
         *  Disk Save: 28
         *  Disk Delete: 31
         *  Disk network: 33
         *  Delete: 89
         *  Green Shield: 106
         *  Yellow Shield: 107
         *  Settings: 114
         *  Icon Archive: 174
         *  Disk Lock Open: 1030
         *  Disk Lock Closed: 1031
         *  Disk Lock Warning: 1032
         */
        public enum Icons
        {
            Addtunnel = 33,
            NewTunnel = 2,
            OpenTunnel = 3,
            DeleteTunnel = 31,
            InactiveState = 32,
            ActiveState = 33,
            Settings = 114,
            ConnectedTunnel = 1031,
            DisconnectedTunnel = 1030,
            ConnectingTunnel = 1032,
            ProcessList = 150,
            Refresh = 1401,
            Activated = 106
        }

        private const int RtGroupIcon = 14;
        private const int RtIcon = 0x00000003;
        private const uint LoadLibraryAsDataFile = 0x00000002;
        private const uint LoadLibraryAsImageResource = 0x00000020;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        /// <summary>
        ///     Frees the loaded dynamic-link library (DLL) module and, if necessary, decrements its reference count.
        ///     When the reference count reaches zero, the module is unloaded from the address space of the calling process and the
        ///     handle is no longer valid.
        /// </summary>
        /// <param name="hModule">
        ///     A handle to the loaded library module. The LoadLibrary or GetModuleHandle function returns this
        ///     handle.
        /// </param>
        /// <returns>
        ///     If the function succeeds, the return value is nonzero; otherwise, it is zero. To get extended error
        ///     information, call GetLastError.
        /// </returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);


        [DllImport("kernel32")]
        private static extern IntPtr FindResource(IntPtr hModule, int lpName, int lpType);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32")]
        private static extern IntPtr LockResource(IntPtr hResData);

        [DllImport("user32")]
        private static extern int LookupIconIdFromDirectoryEx(byte[] presbits, bool fIcon, int cxDesired, int cyDesired,
            uint flags);

        [DllImport("user32")]
        private static extern IntPtr CreateIconFromResourceEx(byte[] pbIconBits, uint cbIconBits, bool fIcon,
            uint dwVersion, int cxDesired, int cyDesired, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

        /// <summary>
        ///     Retrieves an icon from the specified file and group ID.
        /// </summary>
        /// <param name="file">The file containing the icon resource.</param>
        /// <param name="groupId">The group ID of the icon resource.</param>
        /// <param name="size">The desired size of the icon.</param>
        /// <returns>An Icon object representing the requested icon, or null if it cannot be found.</returns>
        private static Icon GetIconFromGroup(string file, int groupId, int size)
        {
            Icon icon = null;

            // Load the library containing the icon resource
            var hLibrary = LoadLibraryEx(
                file,
                IntPtr.Zero,
                LoadLibraryAsDataFile | LoadLibraryAsImageResource);
            if (hLibrary == IntPtr.Zero) return null;

            try
            {
                // Find and load the group icon resource
                var hResource = FindResource(hLibrary, groupId, RtGroupIcon);
                if (hResource == IntPtr.Zero) return null;

                var hMem = LoadResource(hLibrary, hResource);
                if (hMem == IntPtr.Zero) return null;

                var lpResourcePtr = LockResource(hMem);
                var sz = SizeofResource(hLibrary, hResource);
                if (lpResourcePtr == IntPtr.Zero || sz == 0 || sz > int.MaxValue)
                    return null;

                var lpResource = new byte[(int)sz];
                Marshal.Copy(lpResourcePtr, lpResource, 0, (int)sz);

                // Get the icon ID
                var nId = LookupIconIdFromDirectoryEx(lpResource, true, size, size, 0x0000);
                if (nId <= 0) return null;

                // Find and load the icon resource
                hResource = FindResource(hLibrary, nId, RtIcon);
                if (hResource == IntPtr.Zero) return null;

                hMem = LoadResource(hLibrary, hResource);
                if (hMem == IntPtr.Zero) return null;

                lpResourcePtr = LockResource(hMem);
                sz = SizeofResource(hLibrary, hResource);
                if (lpResourcePtr == IntPtr.Zero || sz == 0 || sz > int.MaxValue)
                    return null;

                lpResource = new byte[(int)sz];
                Marshal.Copy(lpResourcePtr, lpResource, 0, (int)sz);

                // Create the icon from the resource
                var hIcon = CreateIconFromResourceEx(lpResource, sz, true, 0x00030000, size, size, 0);
                if (hIcon != IntPtr.Zero)
                    try
                    {
                        using (var nativeIcon = Icon.FromHandle(hIcon))
                        {
                            icon = (Icon)nativeIcon.Clone();
                        }
                    }
                    finally
                    {
                        DestroyIcon(hIcon);
                    }
            }
            finally
            {
                // Free the library
                FreeLibrary(hLibrary);
            }

            return icon;
        }

        /// <summary>
        ///     Get specific size icon (in pixels) from the Icons enum
        /// </summary>
        /// <param name="icon">
        ///     <see cref="Icons" />
        /// </param>
        /// <param name="size">Icon size/width in pixels</param>
        /// <returns><see cref="Icon" /> or null</returns>
        /// <exception cref="FileNotFoundException">Windows ImageRes resource could not be located.</exception>
        public static Icon GetWindowsIcon(Icons icon, int size)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));

            // Windows 11
            var library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemResources",
                "imageres.dll.mun");

            if (!File.Exists(library))
                library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "imageres.dll");

            if (!File.Exists(library))
                throw new FileNotFoundException("Unable to locate imageres.dll for Windows Icons");

            return GetIconFromGroup(library, (int)icon, size);
        }
    }
}
