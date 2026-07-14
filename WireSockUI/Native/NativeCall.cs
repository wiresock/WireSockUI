using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WireSockUI.Native
{
    internal static class NativeCall
    {
        [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
        private static extern void SetLastErrorNative(uint errorCode);

        public static void ClearLastError()
        {
            SetLastErrorNative(0);
        }

        public static string GetLastErrorDiagnostic()
        {
            return FormatError(Marshal.GetLastWin32Error());
        }

        public static bool TryQuery<T>(Func<T> query, Func<T, bool> isErrorSentinel, out T value,
            out string diagnostic)
        {
            return TryQuery(query, isErrorSentinel, ClearLastError, Marshal.GetLastWin32Error, out value,
                out diagnostic);
        }

        internal static bool TryQuery<T>(Func<T> query, Func<T, bool> isErrorSentinel, Action clearLastError,
            Func<int> getLastError, out T value, out string diagnostic)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (isErrorSentinel == null) throw new ArgumentNullException(nameof(isErrorSentinel));
            if (clearLastError == null) throw new ArgumentNullException(nameof(clearLastError));
            if (getLastError == null) throw new ArgumentNullException(nameof(getLastError));

            clearLastError();
            value = query();

            if (!isErrorSentinel(value))
            {
                diagnostic = null;
                return true;
            }

            diagnostic = FormatError(getLastError());
            return diagnostic == null;
        }

        private static string FormatError(int error)
        {
            return error == 0 ? null : $"Native error {error}: {new Win32Exception(error).Message}";
        }
    }
}
