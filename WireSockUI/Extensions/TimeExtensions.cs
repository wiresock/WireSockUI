using System;
using WireSockUI.Properties;

namespace WireSockUI.Extensions
{
    internal static class TimeExtensions
    {
        public static string AsTimeAgo(this long seconds)
        {
            var maxSeconds = (long)TimeSpan.MaxValue.TotalSeconds;
            var minSeconds = (long)TimeSpan.MinValue.TotalSeconds;
            var clampedSeconds = Math.Max(minSeconds, Math.Min(maxSeconds, seconds));

            return TimeSpan.FromSeconds(clampedSeconds).AsTimeAgo();
        }

        public static string AsTimeAgo(this TimeSpan value)
        {
            const int second = 1;
            const int minute = 60 * second;
            const int hour = 60 * minute;
            const int day = 24 * hour;
            const int month = 30 * day;

            var delta = Math.Abs(value.TotalSeconds);

            if (delta < 1 * minute)
                return Math.Abs(value.Seconds) == 1
                    ? Resources.TimeLapseSecond
                    : Math.Abs(value.Seconds) + Resources.TimeLapseSeconds;

            if (delta < 2 * minute)
                return Resources.TimeLapseMinute;

            if (delta < 45 * minute)
                return Math.Abs(value.Minutes) + Resources.TimeLapseMinutes;

            if (delta < 90 * minute)
                return Resources.TimeLapseHour;

            if (delta < 24 * hour)
            {
                var hours = Convert.ToInt32(Math.Floor(delta / hour));
                return hours <= 1 ? Resources.TimeLapseHour : hours + Resources.TimeLapseHours;
            }

            if (delta < 48 * hour)
                return "yesterday";

            if (delta < 30 * day)
                return Math.Abs(value.Days) + Resources.TimeLapseDays;

            if (delta < 12 * month)
            {
                var months = Math.Abs(value.Days) / 30;
                return months <= 1 ? Resources.TimeLapseMonth : months + Resources.TimeLapseMonths;
            }

            var years = Math.Abs(value.Days) / 365;
            return years <= 1 ? Resources.TimeLapseYear : years + Resources.TimeLapseYears;
        }
    }
}
