using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeCodeVS.Cli;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.UI
{
    /// <summary>A collapsible row for one tool call: status, name, one-line summary, input details and result.</summary>
    internal sealed class ToolCallView : Border
    {
        private const int MaxOutputLength = 4000;

        private readonly IChatHost _host;
        private readonly TextBlock _chevron;
        private readonly TextBlock _status;
        private readonly StackPanel _activity = new StackPanel { Margin = new Thickness(30, 0, 0, 0) };
        private readonly Border _detailsHost;
        private readonly StackPanel _details = new StackPanel();
        private bool _hasDetails;

        public ToolCallView(string toolUseId, string toolName, JObject input, IChatHost host)
        {
            ToolUseId = toolUseId;
            ToolName = toolName ?? "?";
            Input = input ?? new JObject();
            _host = host;
            Margin = new Thickness(-4, 1, 0, 1);

            _chevron = Ui.Glyph(Theme.Glyph.ChevronRight, 8).Bind(TextBlock.ForegroundProperty, Theme.MutedTextKey);
            _chevron.Width = 14;
            _chevron.Opacity = 0;
            _status = new TextBlock { Text = "●", Width = 16, FontSize = 9, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            _status.SetResourceReference(TextBlock.ForegroundProperty, Theme.MutedTextKey);
            var name = Ui.Text(ToolName, Theme.TextKey, weight: FontWeights.SemiBold, wrap: false).WithMargin(4, 0, 8, 0);
            name.VerticalAlignment = VerticalAlignment.Center;

            var row = new DockPanel { LastChildFill = true };
            foreach (var element in new FrameworkElement[] { _chevron, _status, name })
            {
                DockPanel.SetDock(element, Dock.Left);
                row.Children.Add(element);
            }

            row.Children.Add(BuildSummary());

            var header = new Border { Child = row, Padding = new Thickness(4, 3, 6, 3), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent };
            header.MouseEnter += (s, e) => { if (_hasDetails) { header.Background = Theme.Hover; header.Cursor = Cursors.Hand; } };
            header.MouseLeave += (s, e) => header.Background = Brushes.Transparent;
            header.MouseLeftButtonUp += (s, e) =>
            {
                if (!(e.OriginalSource is FrameworkContentElement source && (source is Hyperlink || source.Parent is Hyperlink)))
                {
                    Toggle();
                }
            };

            _detailsHost = new Border
            {
                Child = _details,
                Margin = new Thickness(15, 2, 0, 6),
                Padding = new Thickness(14, 2, 0, 2),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            _detailsHost.Bind(Border.BorderBrushProperty, Theme.PanelBorderKey);

            BuildInputDetails();

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(_activity);
            stack.Children.Add(_detailsHost);
            Child = stack;
        }

        public string ToolUseId { get; }

        public string ToolName { get; }

        public JObject Input { get; }

        public static string Summarize(string toolName, JObject input, IChatHost host)
        {
            string Value(string key) => input?.Str(key);
            string Display(string path) => path == null ? null : host.ToDisplayPath(path);

            switch (toolName)
            {
                case "Bash":
                case "PowerShell":
                    return OneLine(Value("command"));
                case "Read":
                case "Write":
                case "Edit":
                case "MultiEdit":
                case "NotebookEdit":
                    return Display(Value("file_path") ?? Value("notebook_path")) ?? string.Empty;
                case "Glob":
                case "Grep":
                    return Value("pattern") + (Value("path") != null ? "  (" + Display(Value("path")) + ")" : string.Empty);
                case "WebFetch":
                    return Value("url");
                case "WebSearch":
                    return Value("query");
                case "Task":
                case "Agent":
                    return Value("description") ?? Value("subagent_type");
                case "Skill":
                    return Value("skill") ?? Value("command");
                case "TodoWrite":
                    return $"{input?.Arr("todos")?.Count ?? 0} görev";
                default:
                    var first = input?.Properties().Select(p => p.Value).FirstOrDefault(v => v.Type == JTokenType.String);
                    return OneLine(first?.ToString());
            }
        }

        public static string ExtractText(JToken content)
        {
            if (content == null || content.Type == JTokenType.Null)
            {
                return string.Empty;
            }

            if (content.Type == JTokenType.String)
            {
                return (string)content;
            }

            if (content is JArray blocks)
            {
                return string.Join("\n", blocks.OfType<JObject>()
                    .Select(b => b.Str("type") == "text" ? b.Str("text") : b.Str("type") == "image" ? "[görsel]" : null)
                    .Where(s => s != null));
            }

            return content.ToString(Formatting.Indented);
        }

        public void SetAwaitingPermission() => SetStatus(Theme.Glyph.Lock, Theme.Warning, 11);

        public void SetResult(JToken content, bool isError)
        {
            SetStatus(isError ? Theme.Glyph.Cancel : Theme.Glyph.Check, isError ? Theme.Error : Theme.Success, 10);
            var text = ExtractText(content);

            if (isError)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    AddDetail(Ui.CodeBox(Ui.Truncate(text.Trim(), MaxOutputLength), Theme.Error));
                }

                return;
            }

            switch (ToolName)
            {
                case "Edit":
                case "MultiEdit":
                case "Write":
                case "NotebookEdit":
                case "TodoWrite":
                    return;
                case "Read":
                    var lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
                    AddDetail(Ui.Text($"{lines} satır okundu", Theme.MutedTextKey, 0.9));
                    return;
                default:
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        AddDetail(Ui.CodeBox(Ui.Truncate(text.Trim(), MaxOutputLength)));
                    }

                    return;
            }
        }

        /// <summary>Shows what a subagent (Task tool) is doing underneath this call.</summary>
        public void AddChildActivity(string text)
        {
            if (_activity.Children.Count >= 40)
            {
                _activity.Children.RemoveAt(0);
            }

            _activity.Children.Add(Ui.Text("↳ " + text, Theme.MutedTextKey, 0.9, wrap: false).WithMargin(0, 1, 0, 1));
        }

        private void SetStatus(string glyph, Brush brush, double size)
        {
            _status.Text = glyph;
            _status.FontFamily = Theme.IconFont;
            _status.FontSize = size;
            _status.Foreground = brush;
        }

        private UIElement BuildSummary()
        {
            var summary = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = Theme.CodeFont,
                FontSize = Theme.BaseFontSize * 0.95,
            };
            summary.SetResourceReference(TextBlock.ForegroundProperty, Theme.MutedTextKey);

            var filePath = Input.Str("file_path") ?? Input.Str("notebook_path");
            if (filePath != null && (ToolName == "Read" || ToolName == "Write" || ToolName == "Edit" || ToolName == "MultiEdit" || ToolName == "NotebookEdit"))
            {
                var link = new Hyperlink(new Run(_host.ToDisplayPath(filePath))) { ToolTip = filePath, TextDecorations = null, Cursor = Cursors.Hand };
                link.SetResourceReference(TextElement.ForegroundProperty, Theme.LinkKey);
                var line = (int)(Input.Num("offset") ?? 0);
                link.Click += (s, e) => _host.OpenFile(filePath, line);
                summary.Inlines.Add(link);
            }
            else
            {
                summary.Text = Summarize(ToolName, Input, _host);
                summary.ToolTip = summary.Text;
            }

            return summary;
        }

        private void BuildInputDetails()
        {
            switch (ToolName)
            {
                case "Bash":
                case "PowerShell":
                    AddDetail(Ui.CodeBox(Input.Str("command") ?? string.Empty));
                    if (Input.Str("description") is string description)
                    {
                        AddDetail(Ui.Text(description, Theme.MutedTextKey, 0.9));
                    }

                    break;
                case "Edit":
                    AddDetail(Ui.Diff(Input.Str("old_string"), Input.Str("new_string")));
                    break;
                case "MultiEdit":
                    foreach (var edit in Input.Arr("edits")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    {
                        AddDetail(Ui.Diff(edit.Str("old_string"), edit.Str("new_string")));
                    }

                    break;
                case "Write":
                    AddDetail(Ui.Diff(null, Input.Str("content"), maxLinesPerSide: 40));
                    break;
                case "TodoWrite":
                    foreach (var todo in Input.Arr("todos")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    {
                        var status = todo.Str("status");
                        var mark = status == "completed" ? "☑" : status == "in_progress" ? "◐" : "☐";
                        AddDetail(Ui.Text($"{mark}  {todo.Str("content")}", Theme.TextKey, 0.95));
                    }

                    break;
                case "Task":
                case "Agent":
                    if (Input.Str("prompt") is string prompt)
                    {
                        AddDetail(Ui.SelectableText(Ui.Truncate(prompt, 1500), brushKey: Theme.MutedTextKey));
                    }

                    break;
                case "Read":
                case "Glob":
                case "Grep":
                case "WebSearch":
                case "WebFetch":
                case "Skill":
                    break;
                default:
                    if (Input.Count > 0)
                    {
                        AddDetail(Ui.CodeBox(Ui.Truncate(Input.ToString(Formatting.Indented), 2000)));
                    }

                    break;
            }
        }

        private void AddDetail(UIElement element)
        {
            _details.Children.Add(element);
            if (!_hasDetails)
            {
                _hasDetails = true;
                _chevron.Opacity = 1;
            }
        }

        private void Toggle()
        {
            if (!_hasDetails)
            {
                return;
            }

            bool expand = _detailsHost.Visibility != Visibility.Visible;
            _detailsHost.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            _chevron.Text = expand ? Theme.Glyph.ChevronDown : Theme.Glyph.ChevronRight;
        }

        private static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var single = Regex.Replace(text.Trim(), @"\s*\r?\n\s*", " ⏎ ");
            return single.Length > 200 ? single.Substring(0, 200) + "…" : single;
        }
    }
}
