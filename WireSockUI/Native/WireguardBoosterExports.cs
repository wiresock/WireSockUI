using System;
using System.Runtime.InteropServices;

namespace WireSockUI.Native
{
    internal static class WireguardBoosterExports
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogPrinter(string message);

        public enum WgbLogLevel
        {
            Error = 0,
            Warning = 1,
            Info = 2,
            Debug = 4,
            All = 255
        }

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern IntPtr wgb_get_handle(LogPrinter logPrinter, WgbLogLevel level, bool enableTrafficCapture);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern void wgb_set_log_level(IntPtr wgboosterHandle, WgbLogLevel level);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_create_tunnel_from_file(IntPtr wgboosterHandle,
            [MarshalAs(UnmanagedType.LPStr)] string fileName);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_create_tunnel_from_file_w(IntPtr wgboosterHandle,
            [MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_create_tunnel(IntPtr wgboosterHandle,
            [MarshalAs(UnmanagedType.LPWStr)] string configName, ref WgbInterface interfaceSettings,
            ref WgbPeer peerSettings, ref WgbExtra extra);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_drop_tunnel(IntPtr wgboosterHandle, bool preserveNetworkLock);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_start_tunnel(IntPtr wgboosterHandle);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_stop_tunnel(IntPtr wgboosterHandle);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern WgbStats wgb_get_tunnel_state(IntPtr wgboosterHandle);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgb_get_tunnel_active(IntPtr wgboosterHandle);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern IntPtr
            wgbp_get_handle(LogPrinter logPrinter, WgbLogLevel level, bool enableTrafficCapture);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern void wgbp_set_log_level(IntPtr wgboosterHandle, WgbLogLevel level);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_create_tunnel_from_file(IntPtr wgboosterHandle,
            [MarshalAs(UnmanagedType.LPStr)] string fileName);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_create_tunnel_from_file_w(IntPtr wgboosterHandle,
            [MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_create_tunnel(IntPtr wgboosterHandle,
            [MarshalAs(UnmanagedType.LPWStr)] string configName, ref WgbInterface interfaceSettings,
            ref WgbPeer peerSettings, ref WgbExtra extra);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_drop_tunnel(IntPtr wgboosterHandle, bool preserveNetworkLock);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_start_tunnel(IntPtr wgboosterHandle);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_stop_tunnel(IntPtr wgboosterHandle);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern WgbStats wgbp_get_tunnel_state(IntPtr wgboosterHandle);

        [DllImport("wgbooster.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool wgbp_get_tunnel_active(IntPtr wgboosterHandle);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WgbStats
        {
            public long time_since_last_handshake;
            public ulong tx_bytes;
            public ulong rx_bytes;
            public float estimated_loss;
            public int estimated_rtt; // rtt estimated on time it took to complete latest initiated handshake in ms
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct WgbInterface
        {
            [MarshalAs(UnmanagedType.LPStr)] public string private_key; // required
            [MarshalAs(UnmanagedType.LPStr)] public string address; // required
            [MarshalAs(UnmanagedType.LPStr)] public string dns; // optional
            [MarshalAs(UnmanagedType.LPStr)] public string mtu; // optional
            [MarshalAs(UnmanagedType.LPStr)] public string listen_port; // optional
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct WgbPeer
        {
            [MarshalAs(UnmanagedType.LPStr)] public string public_key; // required
            [MarshalAs(UnmanagedType.LPStr)] public string preshared_key; // optional
            [MarshalAs(UnmanagedType.LPStr)] public string allowed_ips; // required
            [MarshalAs(UnmanagedType.LPStr)] public string endpoint; // required
            public uint persistent_keep_alive; // optional
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WgbExtra
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string allowed_apps; // optional
            [MarshalAs(UnmanagedType.LPStr)] public string ignored_ips; // optional
            [MarshalAs(UnmanagedType.LPStr)] public string socks5_proxy; // optional
        }
    }
}
