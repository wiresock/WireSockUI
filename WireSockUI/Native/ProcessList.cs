using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WireSockUI.Native
{
    internal class ProcessList
    {
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [DllImport("advapi32", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(int dwFlags, int th32ProcessId);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32First(IntPtr hSnapshot, ref Processentry32 lppe);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref Processentry32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, [Out] char[] lpExeName,
            [In][Out] ref int lpdwSize);

        private static string GetProcessUser(IntPtr handle)
        {
            const int tokenQuery = 0x0008;

            if (handle == IntPtr.Zero)
                return null;


            if (!OpenProcessToken(handle, tokenQuery, out var tokenHandle))
                return null;

            try
            {
                using (var wi = new WindowsIdentity(tokenHandle))
                {
                    return wi.Name;
                }
            }
            finally
            {
                if (tokenHandle != IntPtr.Zero)
                    CloseHandle(tokenHandle);
            }
        }

        private static string GetProcessImage(IntPtr handle)
        {
            var buffer = new char[1024];
            var size = 1024;

            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                return new string(buffer, 0, size);

            return null;
        }

        /// <summary>
        ///     Retrieve a list of <see cref="ProcessEntry" /> for all processes running in the system.
        /// </summary>
        /// <returns>Lis of <see cref="ProcessEntry" /></returns>
        public static IEnumerable<ProcessEntry> GetProcessList()
        {
            const int th32CsSnapprocess = 2;
            const int processQueryLimitedInformation = 0x00001000;

            var snap = CreateToolhelp32Snapshot(th32CsSnapprocess, 0);
            if (snap == IntPtr.Zero || snap == InvalidHandleValue)
                yield break;

            try
            {
                var entry = new Processentry32 { dwSize = Marshal.SizeOf<Processentry32>() };

                if (Process32First(snap, ref entry))
                    do
                    {
                        var handle = OpenProcess(processQueryLimitedInformation, false, entry.th32ProcessID);
                        ProcessEntry processEntry;

                        try
                        {
                            processEntry = new ProcessEntry(
                                entry.th32ProcessID,
                                entry.szExeFile,
                                GetProcessImage(handle),
                                GetProcessUser(handle));
                        }
                        catch (Exception ex)
                        {
                            Trace.TraceWarning(
                                $"Skipping process '{entry.szExeFile}' ({entry.th32ProcessID}) because it could not be inspected: {ex.Message}");
                            processEntry = null;
                        }
                        finally
                        {
                            if (handle != IntPtr.Zero)
                                CloseHandle(handle);
                        }

                        if (processEntry != null)
                            yield return processEntry;
                    } while (Process32Next(snap, ref entry));
            }
            finally
            {
                if (snap != IntPtr.Zero && snap != InvalidHandleValue)
                    CloseHandle(snap);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct Processentry32
        {
            public int dwSize;
            public readonly int cntUsage;
            public readonly int th32ProcessID;
            public readonly IntPtr th32DefaultHeapID;
            public readonly int th32ModuleID;
            public readonly int cntThreads;
            public readonly int th32ParentProcessID;
            public readonly int pcPriClassBase;
            public readonly int dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public readonly string szExeFile;
        }
    }
}
