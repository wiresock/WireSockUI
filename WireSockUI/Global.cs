using System;
using System.Threading;

namespace WireSockUI
{
    internal static class Global
    {
        public static string MainFolder =
            $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\\WireSockUI";

        public static string ConfigsFolder = MainFolder + "\\Configs";
        public static EventWaitHandle AlreadyRunning;
    }
}
