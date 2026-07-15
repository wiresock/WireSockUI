using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace WireSockUI.Native
{
    internal enum WindowsBinaryArchitecture
    {
        Unknown,
        X86,
        X64,
        Arm64
    }

    internal static class WindowsBinaryArchitectureInfo
    {
        private const ushort ImageFileMachineUnknown = 0x0000;
        private const ushort ImageFileMachineI386 = 0x014c;
        private const ushort ImageFileMachineAmd64 = 0x8664;
        private const ushort ImageFileMachineArm64 = 0xaa64;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine,
            out ushort nativeMachine);

        public static bool TryGetCurrentProcessArchitecture(out WindowsBinaryArchitecture architecture,
            out string diagnostic)
        {
            architecture = WindowsBinaryArchitecture.Unknown;
            diagnostic = null;

            try
            {
                if (!IsWow64Process2(GetCurrentProcess(), out var processMachine, out var nativeMachine))
                {
                    diagnostic = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }

                architecture = FromMachine(processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine);
                if (architecture != WindowsBinaryArchitecture.Unknown)
                    return true;

                diagnostic =
                    $"Windows reported unsupported process machine 0x{(processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine):x4}.";
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                architecture = IntPtr.Size == 8
                    ? WindowsBinaryArchitecture.X64
                    : WindowsBinaryArchitecture.X86;
                return true;
            }
        }

        public static bool TryReadPortableExecutableArchitecture(string path,
            out WindowsBinaryArchitecture architecture, out string diagnostic)
        {
            architecture = WindowsBinaryArchitecture.Unknown;
            diagnostic = null;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                           4096, FileOptions.SequentialScan))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d)
                    {
                        diagnostic = $"'{path}' is not a valid PE image.";
                        return false;
                    }

                    stream.Position = 0x3c;
                    var peOffset = reader.ReadInt32();
                    if (peOffset < 0 || peOffset > stream.Length - 6)
                    {
                        diagnostic = $"'{path}' contains an invalid PE header offset.";
                        return false;
                    }

                    stream.Position = peOffset;
                    if (reader.ReadUInt32() != 0x00004550)
                    {
                        diagnostic = $"'{path}' does not contain a valid PE signature.";
                        return false;
                    }

                    var machine = reader.ReadUInt16();
                    architecture = FromMachine(machine);
                    if (architecture != WindowsBinaryArchitecture.Unknown)
                        return true;

                    diagnostic = $"'{path}' uses unsupported PE machine 0x{machine:x4}.";
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                diagnostic = $"Unable to inspect '{path}': {ex.Message}";
                return false;
            }
        }

        public static bool AreCompatible(WindowsBinaryArchitecture processArchitecture,
            WindowsBinaryArchitecture libraryArchitecture)
        {
            return processArchitecture != WindowsBinaryArchitecture.Unknown &&
                   processArchitecture == libraryArchitecture;
        }

        public static string Format(WindowsBinaryArchitecture architecture)
        {
            switch (architecture)
            {
                case WindowsBinaryArchitecture.X86:
                    return "x86";
                case WindowsBinaryArchitecture.X64:
                    return "x64";
                case WindowsBinaryArchitecture.Arm64:
                    return "ARM64";
                default:
                    return "unknown";
            }
        }

        private static WindowsBinaryArchitecture FromMachine(ushort machine)
        {
            switch (machine)
            {
                case ImageFileMachineI386:
                    return WindowsBinaryArchitecture.X86;
                case ImageFileMachineAmd64:
                    return WindowsBinaryArchitecture.X64;
                case ImageFileMachineArm64:
                    return WindowsBinaryArchitecture.Arm64;
                default:
                    return WindowsBinaryArchitecture.Unknown;
            }
        }
    }
}
