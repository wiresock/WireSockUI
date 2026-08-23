using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WireSockUI.Forms
{
    internal static class UiFonts
    {
        private static readonly object InitializationSyncRoot = new object();
        private static bool _defaultsInitialized;
        private static bool _installedFallback;
        private static string _initializationDiagnostic;
        private static Font _ownedFallbackFont;
        private static FieldInfo _controlDefaultFontField;
        private static FieldInfo _toolStripDefaultFontField;
        private static FieldInfo _toolStripDefaultFontCacheField;
        private static ConcurrentDictionary<int, Font> _toolStripDefaultFontCache;
        private static bool _preferenceChangeHandlersInstalled;

        private sealed class AnyDpiComparer : IEqualityComparer<int>
        {
            internal static readonly AnyDpiComparer Instance = new AnyDpiComparer();

            public bool Equals(int left, int right)
            {
                return true;
            }

            public int GetHashCode(int value)
            {
                return 0;
            }
        }

        internal static bool TryEnsureWinFormsDefaultFonts(
            out bool installedFallback,
            out string diagnostic)
        {
            lock (InitializationSyncRoot)
            {
                if (_defaultsInitialized)
                {
                    installedFallback = _installedFallback;
                    diagnostic = _initializationDiagnostic;
                    return true;
                }

                var initialized = TryEnsureWinFormsDefaultFonts(
                    () => Control.DefaultFont,
                    () => SystemFonts.MenuFont,
                    CreateFallbackFont,
                    out installedFallback,
                    out diagnostic);
                if (initialized)
                {
                    _defaultsInitialized = true;
                    _installedFallback = installedFallback;
                    _initializationDiagnostic = diagnostic;
                }

                return initialized;
            }
        }

        internal static bool TryEnsureWinFormsDefaultFonts(
            Func<Font> controlDefaultFontProvider,
            Func<Font> menuFontProvider,
            Func<Font> fallbackFontFactory,
            out bool installedFallback,
            out string diagnostic)
        {
            if (controlDefaultFontProvider == null)
                throw new ArgumentNullException(nameof(controlDefaultFontProvider));
            if (menuFontProvider == null)
                throw new ArgumentNullException(nameof(menuFontProvider));
            if (fallbackFontFactory == null)
                throw new ArgumentNullException(nameof(fallbackFontFactory));

            installedFallback = false;
            diagnostic = null;
            string lookupFailure;
            try
            {
                var controlFont = controlDefaultFontProvider();
                var menuFont = menuFontProvider();
                if (IsUsableFont(controlFont) && IsUsableFont(menuFont))
                    return true;

                lookupFailure = "Windows returned an unusable default or menu font.";
            }
            catch (Exception ex) when (IsRecoverableFontException(ex))
            {
                lookupFailure = $"{ex.GetType().Name}: {ex.Message}";
            }

            Font fallbackFont;
            try
            {
                fallbackFont = fallbackFontFactory();
            }
            catch (Exception ex) when (IsRecoverableFontException(ex))
            {
                diagnostic =
                    $"{lookupFailure} A compatible fallback font could not be created: " +
                    $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }

            if (!IsUsableFont(fallbackFont))
            {
                fallbackFont?.Dispose();
                diagnostic = $"{lookupFailure} No usable compatible fallback font is installed.";
                return false;
            }

            try
            {
                if (!TryInstallWinFormsDefaultFonts(
                        fallbackFont,
                        out var installationDiagnostic))
                {
                    fallbackFont.Dispose();
                    diagnostic =
                        $"{lookupFailure} The compatible fallback font could not be registered." +
                        (string.IsNullOrWhiteSpace(installationDiagnostic)
                            ? string.Empty
                            : $" {installationDiagnostic}");
                    return false;
                }
            }
            catch (Exception ex) when (IsRecoverableFontOrReflectionException(ex))
            {
                fallbackFont.Dispose();
                diagnostic =
                    $"{lookupFailure} The compatible fallback font could not be registered: " +
                    $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }

            installedFallback = true;
            diagnostic =
                $"{lookupFailure} Registered the process-local '{fallbackFont.Name}' fallback " +
                "for WinForms controls and menus.";
            return true;
        }

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
                return IsUsableFont(font);
            }
            catch (Exception ex) when (IsRecoverableFontException(ex))
            {
                TraceFontFallback(ex);
                font = null;
                return false;
            }
        }

        internal static bool TryApplyMessageBoxFont(Action<Font> applyFont)
        {
            return TryApplyMessageBoxFont(() => SystemFonts.MessageBoxFont, applyFont);
        }

        internal static bool TryApplyMessageBoxFont(
            Func<Font> fontProvider,
            Action<Font> applyFont)
        {
            if (fontProvider == null) throw new ArgumentNullException(nameof(fontProvider));
            if (applyFont == null) throw new ArgumentNullException(nameof(applyFont));

            try
            {
                var font = fontProvider();
                if (!IsUsableFont(font))
                    return false;

                applyFont(font);
                return true;
            }
            catch (Exception ex) when (IsRecoverableFontException(ex))
            {
                TraceFontFallback(ex);
                return false;
            }
        }

        internal static bool IsRecoverableFontException(Exception exception)
        {
            return exception is ArgumentException ||
                   exception is ExternalException ||
                   exception is TargetInvocationException targetInvocationException &&
                   IsRecoverableFontException(targetInvocationException.InnerException);
        }

        private static bool IsRecoverableFontOrReflectionException(Exception exception)
        {
            return IsRecoverableFontException(exception) ||
                   exception is MemberAccessException ||
                   exception is TargetException ||
                   exception is InvalidOperationException ||
                   exception is System.Security.SecurityException ||
                   exception is TypeInitializationException typeInitializationException &&
                   IsRecoverableFontOrReflectionException(typeInitializationException.InnerException) ||
                   exception is TargetInvocationException targetInvocationException &&
                   IsRecoverableFontOrReflectionException(targetInvocationException.InnerException);
        }

        private static bool IsUsableFont(Font font)
        {
            if (font == null)
                return false;

            try
            {
                return !string.IsNullOrWhiteSpace(font.Name) &&
                       font.SizeInPoints > 0 &&
                       font.Height > 0;
            }
            catch (Exception ex) when (IsRecoverableFontException(ex))
            {
                return false;
            }
        }

        private static Font CreateFallbackFont()
        {
            Font font;
            if (TryCreateFont("Segoe UI", 9F, out font))
                return font;
            if (TryCreateFont("Tahoma", 8.25F, out font))
                return font;
            if (TryCreateFont("Microsoft Sans Serif", 8.25F, out font))
                return font;

            return null;
        }

        private static bool TryCreateFont(string familyName, float size, out Font font)
        {
            font = null;
            try
            {
                var candidate = new Font(
                    familyName,
                    size,
                    FontStyle.Regular,
                    GraphicsUnit.Point);
                if (!IsUsableFont(candidate))
                {
                    candidate.Dispose();
                    return false;
                }

                font = candidate;
                return true;
            }
            catch (Exception ex) when (IsRecoverableFontException(ex))
            {
                return false;
            }
        }

        internal static bool TryInstallWinFormsDefaultFonts(Font font, out string diagnostic)
        {
            diagnostic = null;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var controlDefaultFontField = typeof(Control).GetField("defaultFont", flags);
            var toolStripDefaultFontField = typeof(ToolStripManager).GetField("defaultFont", flags);
            var toolStripDefaultFontCacheField = typeof(ToolStripManager).GetField(
                "defaultFontCache",
                flags);
            var toolStripDefaultFontProperty = typeof(ToolStripManager).GetProperty(
                "DefaultFont",
                flags);

            if (!IsWritableFontField(controlDefaultFontField) ||
                !IsWritableFontField(toolStripDefaultFontField) ||
                toolStripDefaultFontProperty == null ||
                toolStripDefaultFontProperty.PropertyType != typeof(Font))
            {
                diagnostic =
                    "The installed .NET Framework does not expose the expected WinForms font caches.";
                return false;
            }

            object previousControlFont = null;
            object previousToolStripFont = null;
            object previousToolStripFontCache = null;
            var replacedToolStripFontCache = false;
            var changingHandlerSubscribed = false;
            var changedHandlerSubscribed = false;
            try
            {
                // ToolStripManager registers its cache-reset handler in its static constructor.
                // Run it before registering our later handler so a Windows preference change
                // clears the framework cache first and the compatibility fallback restores it.
                RuntimeHelpers.RunClassConstructor(typeof(ToolStripManager).TypeHandle);
                previousControlFont = controlDefaultFontField.GetValue(null);
                previousToolStripFont = toolStripDefaultFontField.GetValue(null);

                if (!TryInstallToolStripFontCache(
                        flags,
                        toolStripDefaultFontCacheField,
                        font,
                        out var replacementToolStripFontCache,
                        out previousToolStripFontCache,
                        out replacedToolStripFontCache,
                        out var cacheDiagnostic))
                {
                    diagnostic = cacheDiagnostic;
                    return false;
                }

                controlDefaultFontField.SetValue(null, font);
                toolStripDefaultFontField.SetValue(null, font);

                var installedControlFont = Control.DefaultFont;
                var installedToolStripFont =
                    (Font)toolStripDefaultFontProperty.GetValue(null, null);
                if (!ReferenceEquals(font, installedControlFont) ||
                    !ReferenceEquals(font, installedToolStripFont))
                {
                    throw new InvalidOperationException(
                        "WinForms did not retain the compatible fallback font.");
                }

                _controlDefaultFontField = controlDefaultFontField;
                _toolStripDefaultFontField = toolStripDefaultFontField;
                _toolStripDefaultFontCacheField = replacedToolStripFontCache
                    ? toolStripDefaultFontCacheField
                    : null;
                _toolStripDefaultFontCache = replacementToolStripFontCache;
                _ownedFallbackFont = font;
                if (!_preferenceChangeHandlersInstalled)
                {
                    SystemEvents.UserPreferenceChanging += RestoreFallbackAfterPreferenceChange;
                    changingHandlerSubscribed = true;
                    SystemEvents.UserPreferenceChanged += RestoreFallbackAfterPreferenceChanged;
                    changedHandlerSubscribed = true;
                    _preferenceChangeHandlersInstalled = true;
                }

                return true;
            }
            catch (Exception ex) when (IsRecoverableFontOrReflectionException(ex))
            {
                if (changedHandlerSubscribed)
                    SystemEvents.UserPreferenceChanged -= RestoreFallbackAfterPreferenceChanged;
                if (changingHandlerSubscribed)
                    SystemEvents.UserPreferenceChanging -= RestoreFallbackAfterPreferenceChange;
                _ownedFallbackFont = null;
                _controlDefaultFontField = null;
                _toolStripDefaultFontField = null;
                _toolStripDefaultFontCacheField = null;
                _toolStripDefaultFontCache = null;
                TryRestoreFontField(controlDefaultFontField, previousControlFont);
                TryRestoreFontField(toolStripDefaultFontField, previousToolStripFont);
                if (replacedToolStripFontCache)
                    TryRestoreFontField(
                        toolStripDefaultFontCacheField,
                        previousToolStripFontCache);
                diagnostic = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static bool TryInstallToolStripFontCache(
            BindingFlags flags,
            FieldInfo cacheField,
            Font font,
            out ConcurrentDictionary<int, Font> replacementCache,
            out object previousCache,
            out bool replaced,
            out string diagnostic)
        {
            replacementCache = null;
            previousCache = null;
            replaced = false;
            diagnostic = null;

            var dpiHelperType = typeof(Control).Assembly.GetType(
                "System.Windows.Forms.DpiHelper",
                false);
            var perMonitorV2Property = dpiHelperType?.GetProperty(
                "EnableToolStripPerMonitorV2HighDpiImprovements",
                flags);
            var perMonitorV2Enabled = false;
            if (perMonitorV2Property != null)
            {
                if (perMonitorV2Property.PropertyType != typeof(bool))
                {
                    diagnostic =
                        "The installed .NET Framework exposes an incompatible ToolStrip DPI setting.";
                    return false;
                }

                perMonitorV2Enabled = (bool)perMonitorV2Property.GetValue(null, null);
            }

            if (cacheField == null)
            {
                if (perMonitorV2Enabled)
                {
                    diagnostic =
                        "The installed .NET Framework does not expose its per-monitor ToolStrip font cache.";
                    return false;
                }

                return true;
            }

            if (cacheField.FieldType != typeof(ConcurrentDictionary<int, Font>) ||
                cacheField.IsInitOnly)
            {
                diagnostic =
                    "The installed .NET Framework exposes an incompatible ToolStrip font cache.";
                return false;
            }

            previousCache = cacheField.GetValue(null);
            replacementCache = new ConcurrentDictionary<int, Font>(AnyDpiComparer.Instance);
            replacementCache[0] = font;
            cacheField.SetValue(null, replacementCache);
            replaced = true;
            return true;
        }

        internal static void RestoreFallbackAfterPreferenceChange(
            object sender,
            UserPreferenceChangingEventArgs eventArgs)
        {
            if (eventArgs == null)
                return;

            if (eventArgs.Category == UserPreferenceCategory.Color)
            {
                // Top-level controls subscribe after startup and clear Control.defaultFont
                // during UserPreferenceChanged. Re-append our handler during the preceding
                // Changing notification so it runs after every currently open form.
                SystemEvents.UserPreferenceChanged -= RestoreFallbackAfterPreferenceChanged;
                SystemEvents.UserPreferenceChanged += RestoreFallbackAfterPreferenceChanged;
                return;
            }

            if (eventArgs.Category != UserPreferenceCategory.Window)
                return;

            RestoreFallbackFontCaches();
        }

        internal static void RestoreFallbackAfterPreferenceChanged(
            object sender,
            UserPreferenceChangedEventArgs eventArgs)
        {
            if (eventArgs?.Category == UserPreferenceCategory.Color)
                RestoreFallbackFontCaches();
        }

        private static void RestoreFallbackFontCaches()
        {
            lock (InitializationSyncRoot)
            {
                if (_ownedFallbackFont == null)
                    return;

                try
                {
                    _controlDefaultFontField?.SetValue(null, _ownedFallbackFont);
                    _toolStripDefaultFontField?.SetValue(null, _ownedFallbackFont);
                    if (_toolStripDefaultFontCache != null)
                    {
                        _toolStripDefaultFontCache[0] = _ownedFallbackFont;
                        _toolStripDefaultFontCacheField?.SetValue(
                            null,
                            _toolStripDefaultFontCache);
                    }
                }
                catch (Exception ex) when (IsRecoverableFontOrReflectionException(ex))
                {
                    Trace.TraceError(
                        $"Unable to restore the WinForms fallback font after a Windows " +
                        $"preference change: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static bool IsWritableFontField(FieldInfo field)
        {
            return field != null && field.FieldType == typeof(Font) && !field.IsInitOnly;
        }

        private static void TryRestoreFontField(FieldInfo field, object value)
        {
            try
            {
                field?.SetValue(null, value);
            }
            catch
            {
                // Startup will stop if the guarded fallback cannot be installed.
            }
        }

        private static void TraceFontFallback(Exception exception)
        {
            Trace.TraceWarning(
                $"Unable to load or apply the Windows message font; retaining the existing " +
                $"control font. {exception.GetType().Name}: {exception.Message}");
        }
    }
}
