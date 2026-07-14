using System;
using static WireSockUI.Native.WireguardBoosterExports;

namespace WireSockUI.Native
{
    internal interface IWireSockNativeApi
    {
        IntPtr GetHandle(WireSockManager.Mode mode, LogPrinter logPrinter, WgbLogLevel logLevel,
            bool enableTrafficCapture);

        void SetLogLevel(WireSockManager.Mode mode, IntPtr handle, WgbLogLevel logLevel);
        bool CreateTunnelFromFile(WireSockManager.Mode mode, IntPtr handle, string fileName);
        bool StartTunnel(WireSockManager.Mode mode, IntPtr handle);
        bool StopTunnel(WireSockManager.Mode mode, IntPtr handle);
        bool DropTunnel(WireSockManager.Mode mode, IntPtr handle, bool preserveNetworkLock);
        bool GetTunnelActive(WireSockManager.Mode mode, IntPtr handle);
        WgbStats GetTunnelState(WireSockManager.Mode mode, IntPtr handle);
        bool SetNetworkLockMode(WireSockManager.Mode mode, IntPtr handle, WgbNetworkLockMode networkLockMode);
        WgbNetworkLockMode GetNetworkLockMode(WireSockManager.Mode mode, IntPtr handle);
    }

    internal sealed class WireSockNativeApi : IWireSockNativeApi
    {
        public IntPtr GetHandle(WireSockManager.Mode mode, LogPrinter logPrinter, WgbLogLevel logLevel,
            bool enableTrafficCapture)
        {
            return IsVirtualAdapter(mode)
                ? wgbp_get_handle(logPrinter, logLevel, enableTrafficCapture)
                : wgb_get_handle(logPrinter, logLevel, enableTrafficCapture);
        }

        public void SetLogLevel(WireSockManager.Mode mode, IntPtr handle, WgbLogLevel logLevel)
        {
            if (IsVirtualAdapter(mode))
                wgbp_set_log_level(handle, logLevel);
            else
                wgb_set_log_level(handle, logLevel);
        }

        public bool CreateTunnelFromFile(WireSockManager.Mode mode, IntPtr handle, string fileName)
        {
            return IsVirtualAdapter(mode)
                ? wgbp_create_tunnel_from_file_w(handle, fileName)
                : wgb_create_tunnel_from_file_w(handle, fileName);
        }

        public bool StartTunnel(WireSockManager.Mode mode, IntPtr handle)
        {
            return IsVirtualAdapter(mode) ? wgbp_start_tunnel(handle) : wgb_start_tunnel(handle);
        }

        public bool StopTunnel(WireSockManager.Mode mode, IntPtr handle)
        {
            return IsVirtualAdapter(mode) ? wgbp_stop_tunnel(handle) : wgb_stop_tunnel(handle);
        }

        public bool DropTunnel(WireSockManager.Mode mode, IntPtr handle, bool preserveNetworkLock)
        {
            return IsVirtualAdapter(mode)
                ? wgbp_drop_tunnel(handle, preserveNetworkLock)
                : wgb_drop_tunnel(handle, preserveNetworkLock);
        }

        public bool GetTunnelActive(WireSockManager.Mode mode, IntPtr handle)
        {
            return IsVirtualAdapter(mode) ? wgbp_get_tunnel_active(handle) : wgb_get_tunnel_active(handle);
        }

        public WgbStats GetTunnelState(WireSockManager.Mode mode, IntPtr handle)
        {
            return IsVirtualAdapter(mode) ? wgbp_get_tunnel_state(handle) : wgb_get_tunnel_state(handle);
        }

        public bool SetNetworkLockMode(WireSockManager.Mode mode, IntPtr handle,
            WgbNetworkLockMode networkLockMode)
        {
            return IsVirtualAdapter(mode)
                ? wgbp_set_network_lock_mode(handle, networkLockMode)
                : wgb_set_network_lock_mode(handle, networkLockMode);
        }

        public WgbNetworkLockMode GetNetworkLockMode(WireSockManager.Mode mode, IntPtr handle)
        {
            return IsVirtualAdapter(mode)
                ? wgbp_get_network_lock_mode(handle)
                : wgb_get_network_lock_mode(handle);
        }

        private static bool IsVirtualAdapter(WireSockManager.Mode mode)
        {
            return mode == WireSockManager.Mode.VirtualAdapter;
        }
    }
}
