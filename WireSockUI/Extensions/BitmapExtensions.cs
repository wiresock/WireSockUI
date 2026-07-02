using System.Drawing;
using System.Drawing.Drawing2D;
using System;
using System.Runtime.InteropServices;
using WireSockUI.Native;

namespace WireSockUI.Extensions
{
    internal static class BitmapExtensions
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Bitmap DrawCircle(int size, int diameter, Brush color)
        {
            var bm = new Bitmap(size, size);

            using (var gr = Graphics.FromImage(bm))
            {
                gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
                gr.FillEllipse(color, 0, 0, diameter, diameter);
            }

            return bm;
        }

        /// <summary>
        ///     Superimpose a secondary icon on top of the main icon
        /// </summary>
        /// <param name="icon"><see cref="Icon" /> to superimpose onto</param>
        /// <param name="size"><see cref="Icon" /> size to use</param>
        /// <param name="imposed"><see cref="WindowsIcons.Icons" /> to superimpose</param>
        /// <param name="imposedSize">Size of the superimposed icon</param>
        /// <param name="imposedOffset">Offset placement of the superimposed icon</param>
        /// <returns><see cref="Icon" /> with the superimposed icon</returns>
        public static Icon SuperImpose(this Icon icon, int size, WindowsIcons.Icons imposed, int imposedSize,
            int imposedOffset)
        {
            using (var bitmap = new Icon(icon, new Size(size, size)).ToBitmap())
            {
                using (var gr = Graphics.FromImage(bitmap))
                {
                    using (var imposedIcon = WindowsIcons.GetWindowsIcon(imposed, imposedSize))
                    {
                        if (imposedIcon != null)
                            gr.DrawIcon(imposedIcon, imposedOffset, imposedOffset);
                    }
                }

                var hIcon = bitmap.GetHicon();
                try
                {
                    using (var nativeIcon = Icon.FromHandle(hIcon))
                    {
                        return (Icon)nativeIcon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }
    }
}
