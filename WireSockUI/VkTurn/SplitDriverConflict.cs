using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace WireSockUI.VkTurn
{
    internal static class SplitDriverConflict
    {
        // Never accept an arbitrary service name from core output.
        internal static bool IsSupportedService(string name) =>
            name == "AmneziaVPNSplitTunnel" || name == "MullvadSplitTunnel" ||
            name == "mullvad-split-tunnel";

        internal static void Stop(string name)
        {
            if (!IsSupportedService(name))
                throw new ArgumentException("Неподдерживаемая служба драйвера.", nameof(name));
            StopNamedService(name);
        }

        internal static bool IsAmneziaBackgroundRunning()
        {
            try
            {
                using (var service = new ServiceController("AmneziaVPN-service"))
                    return service.Status != ServiceControllerStatus.Stopped;
            }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }

        // Called only after a separate confirmation naming this background service.
        internal static void StopAmneziaBackground()
        {
            StopNamedService("AmneziaVPN-service");
        }

        private static void StopNamedService(string name)
        {
            try { StopExistingService(name); }
            catch (InvalidOperationException ex) when
                (ex.InnerException is Win32Exception native && native.NativeErrorCode == 1060)
            {
                // Amnezia may remove its driver service while shutting down.
            }
        }

        private static void StopExistingService(string name)
        {
            using (var service = new ServiceController(name))
            {
                if (service.Status == ServiceControllerStatus.Stopped) return;
                int stopError = 0;
                if (service.Status != ServiceControllerStatus.StopPending)
                {
                    // ControlService targets only this service. ServiceController.Stop()
                    // can also stop dependent services, which the user did not approve.
                    var manager = OpenSCManager(null, null, 1);
                    if (manager == IntPtr.Zero) throw new Win32Exception();
                    try
                    {
                        var handle = OpenService(manager, name, 0x20); // SERVICE_STOP
                        if (handle == IntPtr.Zero) throw new Win32Exception();
                        try
                        {
                            if (!ControlService(handle, 1, out var status))
                            {
                                var error = Marshal.GetLastWin32Error();
                                // A service can exit while SCM is delivering STOP, closing
                                // the control pipe before acknowledging it. This is NOT
                                // proof of failure or success: verify SCM's state below.
                                // Also tolerate a concurrent stop/state transition.
                                if (error != 1062 && error != 109 && error != 232 &&
                                    error != 1061 && error != 1053)
                                    throw new Win32Exception(error,
                                        $"Не удалось остановить {name} (Win32 {error}): {new Win32Exception(error).Message}");
                                stopError = error;
                            }
                        }
                        finally { CloseServiceHandle(handle); }
                    }
                    finally { CloseServiceHandle(manager); }
                }
                // Refresh the cached pre-stop status, including after a broken pipe.
                service.Refresh();
                try
                {
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
                catch (System.ServiceProcess.TimeoutException)
                {
                    service.Refresh();
                    var finalStatus = service.Status;
                    if (finalStatus == ServiceControllerStatus.Stopped) return;
                    var detail = stopError == 0 ? "" :
                        $" Ошибка запроса остановки Win32 {stopError}: {new Win32Exception(stopError).Message}.";
                    throw new System.ServiceProcess.TimeoutException(
                        $"Служба {name} не остановилась за 10 секунд. Текущее состояние: {finalStatus}." + detail);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint Type, State, Controls, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
