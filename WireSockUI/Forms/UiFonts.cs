using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace WireSockUI.Forms
{
    internal static class UiFonts
    {
        internal static bool TryGetMessageBoxFont(out Font font)
        {
            return TryGetMessageBoxFont(() => SystemFonts.MessageBoxFont, out font);
        }

        internal static bool TryGetMessageBoxFont(Func<Font> fontProvider, out Font font)
        {
            if (fontProvider == null) throw new ArgumentNullException(nameof(fontProvider));

            try
            {
                font = fontProvider();
                return font != null;
            }
            catch (Exception ex) when (IsRecoverableFontException(ex))
            {
                Trace.TraceWarning(
                    $"Unable to load the Windows message font; retaining the existing control font. " +
                    $"{ex.GetType().Name}: {ex.Message}");
                font = null;
                return false;
            }
        }

        internal static bool IsRecoverableFontException(Exception exception)
        {
            return exception is ArgumentException || exception is ExternalException;
        }
    }
}
