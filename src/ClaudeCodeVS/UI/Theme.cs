using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Visual Studio theme resource keys (bound dynamically so light/dark/blue switches apply live),
    /// fixed accent colours that read well on every theme, and the style keys defined in ClaudeChatControl.xaml.
    /// </summary>
    internal static class Theme
    {
        public const string PrimaryButtonStyle = "CcPrimaryButton";
        public const string SecondaryButtonStyle = "CcSecondaryButton";
        public const string GhostButtonStyle = "CcGhostButton";
        public const string DangerButtonStyle = "CcDangerButton";
        public const string IconButtonStyle = "CcIconButton";
        public const string SuggestionButtonStyle = "CcSuggestionButton";
        public const string PlainTextBoxStyle = "CcPlainTextBox";

        public const string Sparkle = "✻";

        /// <summary>The spinner Claude Code uses in the terminal.</summary>
        public static readonly string[] SparkleFrames = { "·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢" };

        public static readonly Brush Accent = Frozen(0xFF, 0xD9, 0x77, 0x57);
        public static readonly Brush AccentSoft = Frozen(0x26, 0xD9, 0x77, 0x57);
        public static readonly Brush Success = Frozen(0xFF, 0x4E, 0xA3, 0x6B);
        public static readonly Brush Error = Frozen(0xFF, 0xE0, 0x52, 0x52);
        public static readonly Brush ErrorSoft = Frozen(0x1F, 0xE0, 0x52, 0x52);
        public static readonly Brush ErrorBorder = Frozen(0x59, 0xE0, 0x52, 0x52);
        public static readonly Brush Warning = Frozen(0xFF, 0xD8, 0x9B, 0x2B);
        public static readonly Brush Hover = Frozen(0x1F, 0x80, 0x80, 0x80);
        public static readonly Brush AddedBackground = Frozen(0x48, 0x3F, 0xB9, 0x50);
        public static readonly Brush RemovedBackground = Frozen(0x48, 0xE0, 0x52, 0x52);

        public static readonly FontFamily CodeFont = new FontFamily("Cascadia Mono, Consolas, Courier New");
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static object BackgroundKey => EnvironmentColors.ToolWindowBackgroundBrushKey;

        public static object TextKey => EnvironmentColors.ToolWindowTextBrushKey;

        public static object MutedTextKey => EnvironmentColors.SystemGrayTextBrushKey;

        public static object BorderKey => EnvironmentColors.ToolWindowBorderBrushKey;

        public static object LinkKey => EnvironmentColors.ControlLinkTextBrushKey;

        public static object PanelKey => CommonControlsColors.TextBoxBackgroundBrushKey;

        public static object PanelTextKey => CommonControlsColors.TextBoxTextBrushKey;

        public static object PanelBorderKey => CommonControlsColors.TextBoxBorderBrushKey;

        public static object FontFamilyKey => VsFonts.EnvironmentFontFamilyKey;

        public static object FontSizeKey => VsFonts.EnvironmentFontSizeKey;

        public static double BaseFontSize => Application.Current?.TryFindResource(VsFonts.EnvironmentFontSizeKey) is double size ? size : 12.0;

        /// <summary>Conversation text is one point larger than the IDE chrome for readability.</summary>
        public static double ChatFontSize => BaseFontSize + 1;

        public static T Bind<T>(this T element, DependencyProperty property, object resourceKey)
            where T : FrameworkElement
        {
            element.SetResourceReference(property, resourceKey);
            return element;
        }

        public static T BindContent<T>(this T element, DependencyProperty property, object resourceKey)
            where T : FrameworkContentElement
        {
            element.SetResourceReference(property, resourceKey);
            return element;
        }

        private static Brush Frozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>Segoe Fluent Icons / MDL2 glyphs.</summary>
        public static class Glyph
        {
            public const string Add = "";
            public const string Cancel = "";
            public const string ChevronDown = "";
            public const string ChevronRight = "";
            public const string Stop = "";
            public const string Send = "";
            public const string Refresh = "";
            public const string Lock = "";
            public const string Check = "";
            public const string Paste = "";
            public const string Warning = "";
            public const string View = "";
            public const string Document = "";
            public const string Copy = "";
            public const string Code = "";
            public const string Lightning = "";
            public const string Info = "";
            public const string Bug = "";
            public const string Help = "";
        }
    }
}
