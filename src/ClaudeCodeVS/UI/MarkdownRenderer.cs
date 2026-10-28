using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeCodeVS.Localization;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Renders the Markdown Claude writes (headings, lists, quotes, tables, fenced code, inline styles,
    /// links) into a themed FlowDocument. Tolerates incomplete input, so it can render while streaming.
    /// </summary>
    internal sealed class MarkdownRenderer
    {
        private static readonly Regex HeadingRegex = new Regex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
        private static readonly Regex RuleRegex = new Regex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
        private static readonly Regex ListRegex = new Regex(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex FenceRegex = new Regex(@"^(\s*)(`{3,}|~{3,})\s*([^`\s]*)", RegexOptions.Compiled);
        private static readonly Regex TableSeparatorRegex = new Regex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);
        private static readonly Regex TaskRegex = new Regex(@"^\[( |x|X)\]\s+", RegexOptions.Compiled);
        private static readonly Regex PathLikeRegex = new Regex(@"[\\/]|\.\w{1,8}(:\d+)?$", RegexOptions.Compiled);

        private static readonly Regex InlineRegex = new Regex(
            @"(?<code>`+)(?<codetext>.+?)\k<code>" +
            @"|\*\*(?<bold>(?=\S).+?(?<=\S))\*\*" +
            @"|__(?<bold2>(?=\S).+?(?<=\S))__(?!\w)" +
            @"|~~(?<strike>(?=\S).+?(?<=\S))~~" +
            @"|\[(?<ltext>[^\]]+)\]\((?<lurl><[^>]+>|[^)\s]+)(?:\s+""[^""]*"")?\)" +
            @"|(?<url>https?://[^\s<>()\[\]`]*[^\s<>()\[\]`.,;:!?'""])" +
            @"|(?<![\w*])\*(?<em>(?=\S)[^*]+?(?<=\S))\*(?![\w*])" +
            @"|(?<![\w_])_(?<em2>(?=\S)[^_]+?(?<=\S))_(?![\w_])",
            RegexOptions.Compiled);

        private readonly IChatHost _host;

        public MarkdownRenderer(IChatHost host)
        {
            _host = host;
        }

        /// <param name="final">When true the text is complete, so file paths in inline code are resolved into links.</param>
        public FlowDocument Render(string markdown, bool final)
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left,
                FontSize = Theme.ChatFontSize,
                LineHeight = Theme.ChatFontSize * 1.5,
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
            };
            document.SetResourceReference(FlowDocument.FontFamilyProperty, Theme.FontFamilyKey);
            document.SetResourceReference(FlowDocument.ForegroundProperty, Theme.TextKey);

            var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            document.Blocks.AddRange(ParseBlocks(lines, final));
            return document;
        }

        private List<Block> ParseBlocks(string[] lines, bool final)
        {
            var blocks = new List<Block>();
            var paragraph = new List<string>();

            void FlushParagraph()
            {
                if (paragraph.Count > 0)
                {
                    blocks.Add(MakeParagraph(paragraph, final));
                    paragraph.Clear();
                }
            }

            int i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];

                var fence = FenceRegex.Match(line);
                if (fence.Success)
                {
                    FlushParagraph();
                    var marker = fence.Groups[2].Value;
                    int indent = fence.Groups[1].Value.Length;
                    var code = new List<string>();
                    i++;
                    while (i < lines.Length && !IsClosingFence(lines[i], marker))
                    {
                        code.Add(StripIndent(lines[i], indent));
                        i++;
                    }

                    i++; // closing fence (absent while streaming)
                    blocks.Add(MakeCodeBlock(string.Join("\n", code), fence.Groups[3].Value));
                    continue;
                }

                if (line.Trim().Length == 0)
                {
                    FlushParagraph();
                    i++;
                    continue;
                }

                var heading = HeadingRegex.Match(line);
                if (heading.Success)
                {
                    FlushParagraph();
                    blocks.Add(MakeHeading(heading.Groups[2].Value, heading.Groups[1].Length, final));
                    i++;
                    continue;
                }

                if (RuleRegex.IsMatch(line))
                {
                    FlushParagraph();
                    blocks.Add(MakeRule());
                    i++;
                    continue;
                }

                if (IsTableStart(lines, i))
                {
                    FlushParagraph();
                    blocks.Add(ParseTable(lines, ref i, final));
                    continue;
                }

                if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    var quoted = new List<string>();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                    {
                        var content = lines[i].TrimStart().Substring(1);
                        quoted.Add(content.StartsWith(" ", StringComparison.Ordinal) ? content.Substring(1) : content);
                        i++;
                    }

                    blocks.Add(MakeQuote(ParseBlocks(quoted.ToArray(), final)));
                    continue;
                }

                if (ListRegex.IsMatch(line))
                {
                    FlushParagraph();
                    blocks.Add(ParseList(lines, ref i, final));
                    continue;
                }

                paragraph.Add(line.Trim());
                i++;
            }

            FlushParagraph();
            return blocks;
        }

        private List ParseList(string[] lines, ref int i, bool final)
        {
            var first = ListRegex.Match(lines[i]);
            int indent = first.Groups[1].Value.Length;
            bool ordered = char.IsDigit(first.Groups[2].Value[0]);
            var list = new List
            {
                MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(18, 0, 0, 0),
            };
            if (ordered && int.TryParse(first.Groups[2].Value.TrimEnd('.', ')'), out var start) && start > 1)
            {
                list.StartIndex = start;
            }

            ListItem currentItem = null;
            Paragraph current = null;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (line.Trim().Length == 0)
                {
                    int next = i + 1;
                    while (next < lines.Length && lines[next].Trim().Length == 0)
                    {
                        next++;
                    }

                    var following = next < lines.Length ? ListRegex.Match(lines[next]) : Match.Empty;
                    bool continues = following.Success
                        && (following.Groups[1].Value.Length > indent
                            || (following.Groups[1].Value.Length == indent && char.IsDigit(following.Groups[2].Value[0]) == ordered));
                    if (!continues)
                    {
                        break;
                    }

                    i = next;
                    continue;
                }

                var item = ListRegex.Match(line);
                if (item.Success)
                {
                    int itemIndent = item.Groups[1].Value.Length;
                    if (itemIndent < indent)
                    {
                        break;
                    }

                    if (itemIndent > indent && currentItem != null)
                    {
                        currentItem.Blocks.Add(ParseList(lines, ref i, final));
                        continue;
                    }

                    if (char.IsDigit(item.Groups[2].Value[0]) != ordered)
                    {
                        break;
                    }

                    current = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                    current.Inlines.AddRange(ParseInlines(TaskMarker(item.Groups[3].Value), final));
                    currentItem = new ListItem(current);
                    list.ListItems.Add(currentItem);
                    i++;
                    continue;
                }

                if (current == null || IsBlockStart(line))
                {
                    break;
                }

                current.Inlines.Add(new LineBreak());
                current.Inlines.AddRange(ParseInlines(line.Trim(), final));
                i++;
            }

            return list;
        }

        private Block ParseTable(string[] lines, ref int i, bool final)
        {
            var header = SplitRow(lines[i]);
            i += 2;
            var rows = new List<string[]>();
            while (i < lines.Length && lines[i].Contains("|") && lines[i].Trim().Length > 0)
            {
                rows.Add(SplitRow(lines[i]));
                i++;
            }

            int columns = Math.Max(header.Length, rows.Count == 0 ? 0 : rows.Max(r => r.Length));
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 10), BorderThickness = new Thickness(1, 1, 0, 0) };
            table.SetResourceReference(Block.BorderBrushProperty, Theme.PanelBorderKey);
            for (int c = 0; c < columns; c++)
            {
                table.Columns.Add(new TableColumn());
            }

            var group = new TableRowGroup();
            group.Rows.Add(MakeTableRow(header, columns, true, final));
            foreach (var row in rows)
            {
                group.Rows.Add(MakeTableRow(row, columns, false, final));
            }

            table.RowGroups.Add(group);
            return table;
        }

        private TableRow MakeTableRow(string[] cells, int columns, bool header, bool final)
        {
            var row = new TableRow();
            for (int c = 0; c < columns; c++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                paragraph.Inlines.AddRange(ParseInlines(c < cells.Length ? cells[c] : string.Empty, final));
                var cell = new TableCell(paragraph) { BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6, 2, 6, 2) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, Theme.PanelBorderKey);
                if (header)
                {
                    cell.FontWeight = FontWeights.SemiBold;
                    cell.SetResourceReference(TextElement.BackgroundProperty, Theme.PanelKey);
                }

                row.Cells.Add(cell);
            }

            return row;
        }

        private Paragraph MakeParagraph(IList<string> lines, bool final)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                paragraph.Inlines.AddRange(ParseInlines(lines[i], final));
            }

            return paragraph;
        }

        private Paragraph MakeHeading(string text, int level, bool final)
        {
            var scale = new[] { 1.45, 1.3, 1.15, 1.05, 1.0, 1.0 };
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 6),
                FontWeight = FontWeights.SemiBold,
                FontSize = Theme.ChatFontSize * scale[level - 1],
            };
            paragraph.Inlines.AddRange(ParseInlines(text, final));
            return paragraph;
        }

        private static Block MakeRule()
        {
            var rule = new Paragraph { Margin = new Thickness(0, 2, 0, 10), BorderThickness = new Thickness(0, 0, 0, 1), FontSize = 2 };
            rule.SetResourceReference(Block.BorderBrushProperty, Theme.BorderKey);
            return rule;
        }

        private static Block MakeQuote(IEnumerable<Block> content)
        {
            var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 0, 0, 8) };
            section.SetResourceReference(Block.BorderBrushProperty, Theme.BorderKey);
            section.SetResourceReference(TextElement.ForegroundProperty, Theme.MutedTextKey);
            section.Blocks.AddRange(content.ToList());
            return section;
        }

        private Block MakeCodeBlock(string code, string language)
        {
            var copyLabel = new TextBlock { Text = L.T("Kopyala"), VerticalAlignment = VerticalAlignment.Center };
            var copyGlyph = Ui.Glyph(Theme.Glyph.Copy, 10).WithMargin(0, 1, 5, 0);
            var copyContent = new StackPanel { Orientation = Orientation.Horizontal };
            copyContent.Children.Add(copyGlyph);
            copyContent.Children.Add(copyLabel);
            var copy = new Button { Content = copyContent, ToolTip = L.T("Kodu panoya kopyala"), Padding = new Thickness(7, 2, 7, 2), FontSize = Theme.BaseFontSize * 0.9 };
            copy.SetResourceReference(FrameworkElement.StyleProperty, Theme.GhostButtonStyle);
            copy.SetResourceReference(Control.ForegroundProperty, Theme.MutedTextKey);
            copy.Click += (s, e) =>
            {
                if (CopyToClipboard(code))
                {
                    copyGlyph.Text = Theme.Glyph.Check;
                    copyLabel.Text = L.T("Kopyalandı");
                    var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    reset.Tick += (o, a) =>
                    {
                        reset.Stop();
                        copyGlyph.Text = Theme.Glyph.Copy;
                        copyLabel.Text = L.T("Kopyala");
                    };
                    reset.Start();
                }
            };

            var insert = new Button { Content = Ui.IconLabel(Theme.Glyph.Paste, L.T("Ekle"), 10), ToolTip = L.T("Aktif düzenleyicide imlecin bulunduğu yere ekle"), Padding = new Thickness(7, 2, 7, 2), FontSize = Theme.BaseFontSize * 0.9 };
            insert.SetResourceReference(FrameworkElement.StyleProperty, Theme.GhostButtonStyle);
            insert.SetResourceReference(Control.ForegroundProperty, Theme.MutedTextKey);
            insert.Click += (s, e) => _host.InsertCode(code);

            var header = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(copy, Dock.Right);
            DockPanel.SetDock(insert, Dock.Right);
            header.Children.Add(copy);
            header.Children.Add(insert);
            var label = Ui.Text(string.IsNullOrEmpty(language) ? L.T("kod") : language, Theme.MutedTextKey, 0.85, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(4, 0, 0, 0);
            header.Children.Add(label);

            var headerBar = new Border { Child = header, Padding = new Thickness(8, 3, 4, 3), BorderThickness = new Thickness(0, 0, 0, 1) };
            headerBar.SetResourceReference(Border.BorderBrushProperty, Theme.PanelBorderKey);

            var text = new TextBox
            {
                Text = code,
                FontFamily = Theme.CodeFont,
                FontSize = Theme.BaseFontSize,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(12, 8, 12, 10),
            };
            text.SetResourceReference(FrameworkElement.StyleProperty, Theme.PlainTextBoxStyle);
            text.SetResourceReference(Control.ForegroundProperty, Theme.PanelTextKey);

            var panel = new DockPanel();
            DockPanel.SetDock(headerBar, Dock.Top);
            panel.Children.Add(headerBar);
            panel.Children.Add(text);

            var border = new Border
            {
                Child = panel,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 12),
                ClipToBounds = true,
            };
            border.SetResourceReference(Border.BackgroundProperty, Theme.PanelKey);
            border.SetResourceReference(Border.BorderBrushProperty, Theme.PanelBorderKey);
            return new BlockUIContainer(border);
        }

        private List<Inline> ParseInlines(string text, bool final)
        {
            var inlines = new List<Inline>();
            int position = 0;
            foreach (Match match in InlineRegex.Matches(text))
            {
                if (match.Index > position)
                {
                    inlines.Add(new Run(text.Substring(position, match.Index - position)));
                }

                position = match.Index + match.Length;

                if (match.Groups["codetext"].Success)
                {
                    inlines.Add(MakeInlineCode(match.Groups["codetext"].Value, final));
                }
                else if (match.Groups["bold"].Success || match.Groups["bold2"].Success)
                {
                    var bold = new Bold();
                    bold.Inlines.AddRange(ParseInlines(match.Groups["bold"].Success ? match.Groups["bold"].Value : match.Groups["bold2"].Value, final));
                    inlines.Add(bold);
                }
                else if (match.Groups["strike"].Success)
                {
                    var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                    strike.Inlines.AddRange(ParseInlines(match.Groups["strike"].Value, final));
                    inlines.Add(strike);
                }
                else if (match.Groups["ltext"].Success)
                {
                    var target = match.Groups["lurl"].Value.Trim('<', '>');
                    inlines.Add(MakeLink(ParseInlines(match.Groups["ltext"].Value, final), target, () => _host.OpenLink(target)));
                }
                else if (match.Groups["url"].Success)
                {
                    var url = match.Groups["url"].Value;
                    inlines.Add(MakeLink(new List<Inline> { new Run(url) }, url, () => _host.OpenLink(url)));
                }
                else
                {
                    var italic = new Italic();
                    italic.Inlines.AddRange(ParseInlines(match.Groups["em"].Success ? match.Groups["em"].Value : match.Groups["em2"].Value, final));
                    inlines.Add(italic);
                }
            }

            if (position < text.Length)
            {
                inlines.Add(new Run(text.Substring(position)));
            }

            return inlines;
        }

        private Inline MakeInlineCode(string code, bool final)
        {
            var text = code.Length > 2 && code.StartsWith(" ", StringComparison.Ordinal) && code.EndsWith(" ", StringComparison.Ordinal)
                ? code.Substring(1, code.Length - 2)
                : code;
            var run = new Run(" " + text + " ") { FontFamily = Theme.CodeFont, FontSize = Theme.ChatFontSize * 0.9 };
            run.SetResourceReference(TextElement.BackgroundProperty, Theme.PanelKey);

            if (final && text.Length < 260 && text.IndexOf(' ') < 0 && PathLikeRegex.IsMatch(text))
            {
                var path = _host.ResolveExistingFile(text, out var line);
                if (path != null)
                {
                    return MakeLink(new List<Inline> { run }, path, () => _host.OpenFile(path, line));
                }
            }

            return run;
        }

        private static Hyperlink MakeLink(IEnumerable<Inline> content, string toolTip, Action onClick)
        {
            var link = new Hyperlink { ToolTip = toolTip, Cursor = Cursors.Hand };
            link.Inlines.AddRange(content.ToList());
            link.SetResourceReference(TextElement.ForegroundProperty, Theme.LinkKey);
            link.Click += (s, e) => onClick();
            return link;
        }

        private static bool IsBlockStart(string line)
        {
            return FenceRegex.IsMatch(line)
                || HeadingRegex.IsMatch(line)
                || RuleRegex.IsMatch(line)
                || ListRegex.IsMatch(line)
                || line.TrimStart().StartsWith(">", StringComparison.Ordinal);
        }

        private static bool IsTableStart(string[] lines, int i)
        {
            return lines[i].Contains("|")
                && i + 1 < lines.Length
                && lines[i + 1].Contains("|")
                && lines[i + 1].Contains("-")
                && TableSeparatorRegex.IsMatch(lines[i + 1]);
        }

        private static string[] SplitRow(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1);
            }

            if (trimmed.EndsWith("|", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed.Split('|').Select(cell => cell.Trim()).ToArray();
        }

        private static bool IsClosingFence(string line, string marker)
        {
            var trimmed = line.Trim();
            return trimmed.Length >= marker.Length && trimmed.All(c => c == marker[0]);
        }

        private static string StripIndent(string line, int indent)
        {
            int remove = 0;
            while (remove < indent && remove < line.Length && line[remove] == ' ')
            {
                remove++;
            }

            return line.Substring(remove);
        }

        private static string TaskMarker(string text)
        {
            var match = TaskRegex.Match(text);
            return match.Success ? (match.Groups[1].Value == " " ? "☐ " : "☑ ") + text.Substring(match.Length) : text;
        }

        private static bool CopyToClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                return false; // clipboard is busy; the user can retry
            }
        }
    }
}
