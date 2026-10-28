using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeCodeVS.UI
{
    /// <summary>Small factories for themed WPF elements. Styles come from ClaudeChatControl.xaml.</summary>
    internal static class Ui
    {
        public static Button Button(string text, Action onClick, string toolTip = null, string style = Theme.SecondaryButtonStyle, string glyph = null)
        {
            var button = new Button
            {
                Content = glyph == null ? (object)text : IconLabel(glyph, text),
                ToolTip = toolTip,
                Margin = new Thickness(0, 0, 6, 0),
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, style);
            button.Click += (s, e) => onClick();
            return button;
        }

        public static Button IconButton(string glyph, Action onClick, string toolTip)
        {
            var button = new Button { Content = glyph, ToolTip = toolTip };
            button.SetResourceReference(FrameworkElement.StyleProperty, Theme.IconButtonStyle);
            button.Click += (s, e) => onClick();
            return button;
        }

        /// <summary>An icon glyph followed by a label, for buttons and chips.</summary>
        public static StackPanel IconLabel(string glyph, string text, double glyphSize = 11)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(Glyph(glyph, glyphSize).WithMargin(0, 1, 6, 0));
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        public static TextBlock Glyph(string glyph, double size = 12, Brush foreground = null)
        {
            var block = new TextBlock
            {
                Text = glyph,
                FontFamily = Theme.IconFont,
                FontSize = size,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (foreground != null)
            {
                block.Foreground = foreground;
            }

            return block;
        }

        public static TextBlock Text(string text, object brushKey = null, double sizeFactor = 1.0, FontWeight? weight = null, bool wrap = true)
        {
            var block = new TextBlock
            {
                Text = text ?? string.Empty,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            };
            if (brushKey != null)
            {
                block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            }

            if (Math.Abs(sizeFactor - 1.0) > 0.01)
            {
                block.FontSize = Theme.BaseFontSize * sizeFactor;
            }

            if (weight.HasValue)
            {
                block.FontWeight = weight.Value;
            }

            return block;
        }

        /// <summary>Read-only text that looks like a TextBlock but supports selection and copy.</summary>
        public static TextBox SelectableText(string text, bool monospace = false, object brushKey = null, double? fontSize = null)
        {
            var box = new TextBox { Text = text ?? string.Empty, TextWrapping = TextWrapping.Wrap };
            box.SetResourceReference(FrameworkElement.StyleProperty, Theme.PlainTextBoxStyle);
            box.SetResourceReference(Control.ForegroundProperty, brushKey ?? Theme.TextKey);
            if (monospace)
            {
                box.FontFamily = Theme.CodeFont;
            }

            if (fontSize.HasValue)
            {
                box.FontSize = fontSize.Value;
            }

            return box;
        }

        /// <summary>Monospace, selectable text in a subtle rounded panel, for commands and tool output.</summary>
        public static Border CodeBox(string text, Brush foreground = null)
        {
            var box = SelectableText(text, monospace: true, brushKey: Theme.PanelTextKey);
            if (foreground != null)
            {
                box.Foreground = foreground;
            }

            var border = new Border
            {
                Child = box,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 2, 0, 4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
            };
            border.Bind(Border.BackgroundProperty, Theme.PanelKey);
            border.Bind(Border.BorderBrushProperty, Theme.PanelBorderKey);
            return border;
        }

        public static Border Chip(string glyph, string text, string toolTip, Action onRemove)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(Glyph(glyph, 10).Bind(TextBlock.ForegroundProperty, Theme.MutedTextKey).WithMargin(0, 1, 5, 0));
            panel.Children.Add(Text(text, Theme.PanelTextKey, 0.92, wrap: false));
            if (onRemove != null)
            {
                var remove = Glyph(Theme.Glyph.Cancel, 8).Bind(TextBlock.ForegroundProperty, Theme.MutedTextKey).WithMargin(7, 1, 0, 0);
                remove.Cursor = Cursors.Hand;
                remove.ToolTip = "Kaldır";
                remove.MouseLeftButtonUp += (s, e) => onRemove();
                panel.Children.Add(remove);
            }

            var border = new Border
            {
                Child = panel,
                ToolTip = toolTip,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 4, 4),
                Background = Theme.AccentSoft,
            };
            return border;
        }

        /// <summary>Line diff of an edit: unchanged prefix/suffix lines are trimmed, the rest shown as -/+.</summary>
        public static UIElement Diff(string oldText, string newText, int maxLinesPerSide = 60)
        {
            var before = SplitLines(oldText);
            var after = SplitLines(newText);
            int prefix = 0;
            while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < before.Length - prefix && suffix < after.Length - prefix
                   && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
            {
                suffix++;
            }

            var panel = new StackPanel();
            for (int i = Math.Max(0, prefix - 2); i < prefix; i++)
            {
                panel.Children.Add(DiffLine("  " + before[i], null));
            }

            AddSide(panel, before.Skip(prefix).Take(before.Length - prefix - suffix).ToArray(), "- ", Theme.RemovedBackground, maxLinesPerSide);
            AddSide(panel, after.Skip(prefix).Take(after.Length - prefix - suffix).ToArray(), "+ ", Theme.AddedBackground, maxLinesPerSide);

            for (int i = after.Length - suffix; i < Math.Min(after.Length, after.Length - suffix + 2); i++)
            {
                panel.Children.Add(DiffLine("  " + after[i], null));
            }

            var border = new Border
            {
                Child = panel,
                Padding = new Thickness(0, 4, 0, 4),
                Margin = new Thickness(0, 2, 0, 4),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
            };
            border.Bind(Border.BackgroundProperty, Theme.PanelKey);
            border.Bind(Border.BorderBrushProperty, Theme.PanelBorderKey);
            return border;
        }

        public static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxLength) + $"\n… ({text.Length - maxLength} karakter daha)";
        }

        public static T WithMargin<T>(this T element, double left, double top, double right, double bottom)
            where T : FrameworkElement
        {
            element.Margin = new Thickness(left, top, right, bottom);
            return element;
        }

        private static void AddSide(Panel panel, string[] lines, string marker, Brush background, int max)
        {
            foreach (var line in lines.Take(max))
            {
                panel.Children.Add(DiffLine(marker + line, background));
            }

            if (lines.Length > max)
            {
                panel.Children.Add(DiffLine($"  … {lines.Length - max} satır daha", null));
            }
        }

        private static FrameworkElement DiffLine(string text, Brush background)
        {
            var line = new TextBlock
            {
                Text = text,
                FontFamily = Theme.CodeFont,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(8, 0, 8, 0),
                Background = background ?? Brushes.Transparent,
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, Theme.PanelTextKey);
            return line;
        }

        private static string[] SplitLines(string text)
        {
            return string.IsNullOrEmpty(text) ? new string[0] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        }
    }
}
