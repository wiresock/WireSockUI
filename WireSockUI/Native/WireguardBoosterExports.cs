using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WireSockUI.Native
{
    internal static class WireguardBoosterExports
    {
        internal const int MaxLogMessageBytes = 64 * 1024;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogPrinter(IntPtr message);

        internal static string DecodeLogMessage(IntPtr message)
        {
            if (message == IntPtr.Zero)
                return string.Empty;

            var length = 0;
            while (length < MaxLogMessageBytes && Marshal.ReadByte(message, length) != 0)
                length++;

            if (length == MaxLogMessageBytes)
                throw new ArgumentException(
                    $"The native log message is at least {MaxLogMessageBytes} bytes or is not null-terminated within that limit.",
                    nameof(message));

            if (length == 0)
                return string.Empty;

            var bytes = new byte[length];
            Marshal.Copy(message, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        public enum WgbLogLevel
        {
            Error = 0,
            Warning = 1,
            Info = 2,
            Debug = 4,
            All = 255
        }

        public enum WgbNetworkLockMode
        {
            Disabled = 0,
            Enabled = 1
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern IntPtr wgb_get_handle_ex(LogPrinter logPrinter, WgbLogLevel level,
            IntPtr eventLogger, [MarshalAs(UnmanagedType.I1)] bool enableTrafficCapture,
            [MarshalAs(UnmanagedType.I1)] bool enableAnalytics);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern void wgb_release_handle(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern void wgb_set_log_level(IntPtr wgboosterHandle, WgbLogLevel level);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_create_tunnel_from_file(IntPtr wgboosterHandle,
            [MarshalAs(UnmanagedType.LPStr)] string fileName);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_create_tunnel_from_file_w(IntPtr wgboosterHandle,
            [MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_drop_tunnel(IntPtr wgboosterHandle, bool preserveNetworkLock);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_start_tunnel(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_stop_tunnel(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern WgbStats wgb_get_tunnel_state(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_get_tunnel_active(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_set_network_lock_mode(IntPtr wgboosterHandle, WgbNetworkLockMode mode);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern WgbNetworkLockMode wgb_get_network_lock_mode(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern IntPtr wgbp_get_handle_ex(LogPrinter logPrinter, WgbLogLevel level,
            IntPtr eventLogger, [MarshalAs(UnmanagedType.I1)] bool enableTrafficCapture,
            [MarshalAs(UnmanagedType.I1)] bool enableAnalytics);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern void wgbp_release_handle(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern void wgbp_set_log_level(IntPtr wgboosterHandle, WgbLogLevel level);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_create_tunnel_from_file(IntPtr wgboosterHandle,
            [MarshalAs(UnmanagedType.LPStr)] string fileName);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_create_tunnel_from_file_w(IntPtr wgboosterHandle,
            [MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_drop_tunnel(IntPtr wgboosterHandle, bool preserveNetworkLock);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_start_tunnel(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_stop_tunnel(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern WgbStats wgbp_get_tunnel_state(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_get_tunnel_active(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_set_network_lock_mode(IntPtr wgboosterHandle, WgbNetworkLockMode mode);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern WgbNetworkLockMode wgbp_get_network_lock_mode(IntPtr wgboosterHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wg_reset_network_lock();

        [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories | DllImportSearchPath.System32)]
        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wg_is_network_lock_active();

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WgbStats
        {
            public long time_since_last_handshake;
            public ulong tx_bytes;
            public ulong rx_bytes;
            public float estimated_loss;
            public int estimated_rtt; // rtt estimated on time it took to complete latest initiated handshake in ms
        }

    }
}
