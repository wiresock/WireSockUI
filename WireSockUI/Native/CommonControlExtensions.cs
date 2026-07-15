using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WireSockUI.Native
{
    internal static class CommonControlExtensions
    {
        private const int WmUser = 0x400;
        private const int EmGetcharformat = WmUser + 58;
        private const int EmSetcharformat = WmUser + 68;

        private const int EmSetcuebanner = 0x1501;

        /* EM_SETCHARFORMAT wparam masks */
        internal const uint ScfSelection = 0x0001;
        internal const uint ScfWord = 0x0002;
        internal const uint ScfDefault = 0x0000; // set the default charformat or paraformat
        internal const uint ScfAll = 0x0004; // not valid with SCF_SELECTION or SCF_WORD
        internal const uint ScfUseuirules = 0x0008; // modifier for SCF_SELECTION; says that

        [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern IntPtr SendMessage(HandleRef hWnd, uint msg, uint wParam,
            [In][Out] ref Charformat2W lParam);

        [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern IntPtr SendMessage(HandleRef hWnd, uint msg, uint wParam,
            [MarshalAs(UnmanagedType.LPWStr)] string lParam);

        private static Charformat2W GetCharFormat(RichTextBox richTextBox, bool fSelection)
        {
            var format = new Charformat2W { cbSize = Charformat2W.SIZE };
            SendMessage(new HandleRef(richTextBox, richTextBox.Handle), EmGetcharformat,
                fSelection ? ScfSelection : ScfAll, ref format);
            return format;
        }

        private static void SetCharFormat(RichTextBox richTextBox, bool fSelection, Charformat2W format)
        {
            format.cbSize = Charformat2W.SIZE;
            SendMessage(new HandleRef(richTextBox, richTextBox.Handle), EmSetcharformat,
                fSelection ? ScfSelection : ScfAll, ref format);
        }

        /// <summary>
        ///     Apply red wavy underline to a current selection in a <see cref="RichTextBox" />
        /// </summary>
        /// <param name="richTextBox">
        ///     <see cref="RichTextBox" />
        /// </param>
        public static void UnderlineSelection(this RichTextBox richTextBox)
        {
            var format = new Charformat2W
            {
                dwMask = CharformatMasks.CfmUnderline | CharformatMasks.CfmUnderlinetype,
                dwEffects = CharformatEffects.CfeUnderline,
                bUnderlineType = CharformatUnderline.CfuUnderlinewave,
                bUnderlineColor = CharformatUnderlineColor.Red
            };

            SetCharFormat(richTextBox, true, format);
        }

        /// <summary>
        ///     Set a user cue to action message for a <see cref="TextBox" />
        /// </summary>
        /// <param name="textBox">
        ///     <see cref="TextBox" />
        /// </param>
        /// <param name="bannerText">Message to display</param>
        public static void SetCueBanner(this TextBox textBox, string bannerText)
        {
            SendMessage(new HandleRef(textBox, textBox.Handle), EmSetcuebanner, 0, bannerText);
        }
        // the format came from a toolbar, etc. and
        // therefore UI formatting rules should be
        // used instead of strictly formatting the
        // selection.

        [StructLayout(LayoutKind.Sequential)]
        private struct Charformat2W
        {
            public static readonly uint SIZE = (uint)Marshal.SizeOf(typeof(Charformat2W));

            public uint cbSize;
            public CharformatMasks dwMask;
            public CharformatEffects dwEffects;
            public int yHeight;
            public readonly int yOffset;
            public readonly int crTextColor; // 0x00bbggrr
            public readonly byte bCharSet;
            public readonly byte bPitchAndFamily;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] szFaceName;

            public readonly ushort wWeight;
            public readonly short sSpacing;
            public readonly int crBackColor; // 0x00bbggrr
            public readonly uint lcid;
            public readonly uint dwReserved;
            public readonly short sStyle;
            public readonly ushort wKerning;
            public CharformatUnderline bUnderlineType;
            public readonly byte bAnimation;
            public readonly byte bRevAuthor;
            public CharformatUnderlineColor bUnderlineColor; // bReserved1

            public string FaceName
            {
                get => Encoding.Unicode.GetString(szFaceName);
                set => szFaceName = Encoding.Unicode.GetBytes(value);
            }

            public int HeightPoints
            {
                get => yHeight / 20;
                set => yHeight = value * 20;
            }
        }

        [Flags]
        private enum CharformatMasks : uint
        {
            CfmBold = 0x00000001,
            CfmItalic = 0x00000002,
            CfmUnderline = 0x00000004,
            CfmStrikeout = 0x00000008,
            CfmProtected = 0x00000010,
            CfmLink = 0x00000020, // Exchange hyperlink extension 
            CfmSize = 0x80000000,
            CfmColor = 0x40000000,
            CfmFace = 0x20000000,
            CfmOffset = 0x10000000,
            CfmCharset = 0x08000000,

            CfmSmallcaps = 0x0040,
            CfmAllcaps = 0x0080,
            CfmHidden = 0x0100,
            CfmOutline = 0x0200,
            CfmShadow = 0x0400,
            CfmEmboss = 0x0800,
            CfmImprint = 0x1000,
            CfmDisabled = 0x2000,
            CfmRevised = 0x4000,

            CfmBackcolor = 0x04000000,
            CfmLcid = 0x02000000,
            CfmUnderlinetype = 0x00800000,
            CfmWeight = 0x00400000,
            CfmSpacing = 0x00200000,
            CfmKerning = 0x00100000,
            CfmStyle = 0x00080000,
            CfmAnimation = 0x00040000,
            CfmRevauthor = 0x00008000,

            CfmSubscript = CharformatEffects.CfeSubscript | CharformatEffects.CfeSuperscript,
            CfmSuperscript = CfmSubscript

            //CFM_EFFECTS = (CFM_BOLD | CFM_ITALIC | CFM_UNDERLINE | CFM_COLOR | CFM_STRIKEOUT | CHARFORMAT_EFFECTS.CFE_PROTECTED | CFM_LINK),
            //CFM_ALL = (CFM_EFFECTS | CFM_SIZE | CFM_FACE | CFM_OFFSET | CFM_CHARSET),
            //CFM_EFFECTS2 = (CFM_EFFECTS | CFM_DISABLED | CFM_SMALLCAPS | CFM_ALLCAPS | CFM_HIDDEN | CFM_OUTLINE | CFM_SHADOW | CFM_EMBOSS | CFM_IMPRINT | CFM_DISABLED | CFM_REVISED | CFM_SUBSCRIPT | CFM_SUPERSCRIPT | CFM_BACKCOLOR),
            //CFM_ALL2 = (CFM_ALL | CFM_EFFECTS2 | CFM_BACKCOLOR | CFM_LCID | CFM_UNDERLINETYPE | CFM_WEIGHT | CFM_REVAUTHOR | CFM_SPACING | CFM_KERNING | CFM_STYLE | CFM_ANIMATION),
        }

        [Flags]
        private enum CharformatEffects : uint
        {
            CfeBold = 0x0001,
            CfeItalic = 0x0002,
            CfeUnderline = 0x0004,
            CfeStrikeout = 0x0008,
            CfeProtected = 0x0010,
            CfeLink = 0x0020,
            CfeAutocolor = 0x40000000, // NOTE: this corresponds to CFM_COLOR, which controls it

            CfeSubscript = 0x00010000, // Superscript and subscript are mutually exclusive
            CfeSuperscript = 0x00020000,

            CfeSmallcaps = CharformatMasks.CfmSmallcaps,
            CfeAllcaps = CharformatMasks.CfmAllcaps,
            CfeHidden = CharformatMasks.CfmHidden,
            CfeOutline = CharformatMasks.CfmOutline,
            CfeShadow = CharformatMasks.CfmShadow,
            CfeEmboss = CharformatMasks.CfmEmboss,
            CfeImprint = CharformatMasks.CfmImprint,
            CfeDisabled = CharformatMasks.CfmDisabled,
            CfeRevised = CharformatMasks.CfmRevised,

            CfeAutobackcolor =
                CharformatMasks
                    .CfmBackcolor // CFE_AUTOCOLOR and CFE_AUTOBACKCOLOR correspond to CFM_COLOR and // CFM_BACKCOLOR, respectively, which control them
        }

        private enum CharformatUnderline : byte
        {
            CfuCf1Underline = 0xFF, // Map charformat's bit underline to CF2
            CfuInvert = 0xFE, // For IME composition fake a selection

            CfuUnderlinethicklongdash = 18,
            CfuUnderlinethickdotted = 17,
            CfuUnderlinethickdashdotdot = 16,
            CfuUnderlinethickdashdot = 15,
            CfuUnderlinethickdash = 14,
            CfuUnderlinelongdash = 13,
            CfuUnderlineheavywave = 12,
            CfuUnderlinedoublewave = 11,
            CfuUnderlinehairline = 10,
            CfuUnderlinethick = 9,
            CfuUnderlinewave = 8,
            CfuUnderlinedashdotdot = 7,
            CfuUnderlinedashdot = 6,
            CfuUnderlinedash = 5,
            CfuUnderlinedotted = 4,
            CfuUnderlinedouble = 3,
            CfuUnderlineword = 2,
            CfuUnderline = 1,
            CfuUnderlinenone = 0
        }

        private enum CharformatUnderlineColor : byte
        {
            FontColor = 0,
            Black = 1,
            Blue = 2,
            Aqua = 3,
            Lime = 4,
            Fuchsia = 5,
            Red = 6,
            Yellow = 7,
            White = 8,
            Navy = 9,
            Teal = 10,
            Green = 11,
            Purple = 12,
            Maroon = 13,
            Olive = 14,
            DarkkGray = 15,
            LightGray = 16
        }
    }
}