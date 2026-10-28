using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCodeVS.Cli;

namespace ClaudeCodeVS.UI
{
    /// <summary>Searchable list of earlier conversations in this folder, grouped by day.</summary>
    internal sealed class HistoryView : Border
    {
        private readonly StackPanel _list = new StackPanel { Margin = new Thickness(10, 2, 10, 14) };
        private readonly TextBox _search;
        private readonly TextBlock _placeholder;
        private List<SessionSummary> _all = new List<SessionSummary>();
        private string _currentId;

        public HistoryView()
        {
            this.Bind(BackgroundProperty, Theme.BackgroundKey);

            var header = new DockPanel { Margin = new Thickness(16, 10, 8, 6) };
            var close = Ui.IconButton(Theme.Glyph.Cancel, () => Closed?.Invoke(), "Kapat (Esc)");
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            var title = Ui.Text("Geçmiş sohbetler", Theme.TextKey, 1.15, FontWeights.SemiBold, wrap: false);
            title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title);

            _search = new TextBox { FontSize = Theme.ChatFontSize, Margin = new Thickness(0) };
            _search.SetResourceReference(StyleProperty, "CcComposerTextBox");
            _search.TextChanged += (s, e) =>
            {
                _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Render();
            };
            _search.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    Closed?.Invoke();
                }
                else if (e.Key == Key.Enter && Filtered().FirstOrDefault() is SessionSummary first)
                {
                    e.Handled = true;
                    SessionSelected?.Invoke(first);
                }
            };
            _placeholder = Ui.Text("Sohbetlerde ara…", Theme.MutedTextKey, wrap: false);
            _placeholder.IsHitTestVisible = false;
            _placeholder.FontSize = Theme.ChatFontSize;

            var searchRow = new DockPanel();
            var glyph = Ui.Glyph("", 11).Bind(TextBlock.ForegroundProperty, Theme.MutedTextKey).WithMargin(0, 1, 8, 0);
            DockPanel.SetDock(glyph, Dock.Left);
            searchRow.Children.Add(glyph);
            var field = new Grid();
            field.Children.Add(_search);
            field.Children.Add(_placeholder);
            searchRow.Children.Add(field);
            var searchBox = new Border
            {
                Child = searchRow,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(16, 0, 16, 8),
            };
            searchBox.Bind(Border.BackgroundProperty, Theme.PanelKey);
            searchBox.Bind(Border.BorderBrushProperty, Theme.PanelBorderKey);

            var scroll = new ScrollViewer
            {
                Content = _list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            var dock = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(searchBox, Dock.Top);
            dock.Children.Add(header);
            dock.Children.Add(searchBox);
            dock.Children.Add(scroll);
            Child = dock;
        }

        public event Action<SessionSummary> SessionSelected;

        public event Action Closed;

        public void ShowLoading()
        {
            _list.Children.Clear();
            _list.Children.Add(Ui.Text("Yükleniyor…", Theme.MutedTextKey).WithMargin(6, 8, 0, 0));
        }

        public void SetSessions(List<SessionSummary> sessions, string currentId)
        {
            _all = sessions ?? new List<SessionSummary>();
            _currentId = currentId;
            Render();
        }

        public void FocusSearch()
        {
            _search.Clear();
            _search.Focus();
            Keyboard.Focus(_search);
        }

        private IEnumerable<SessionSummary> Filtered()
        {
            var query = _search.Text.Trim();
            return query.Length == 0 ? _all : _all.Where(s => s.Title.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        private void Render()
        {
            _list.Children.Clear();
            var items = Filtered().ToList();
            if (items.Count == 0)
            {
                var message = _all.Count == 0 ? "Bu klasörde henüz kaydedilmiş sohbet yok." : "Aramayla eşleşen sohbet yok.";
                _list.Children.Add(Ui.Text(message, Theme.MutedTextKey).WithMargin(6, 8, 0, 0));
                return;
            }

            foreach (var group in items.GroupBy(s => Bucket(s.Updated)))
            {
                _list.Children.Add(Ui.Text(group.Key.ToUpperInvariant(), Theme.MutedTextKey, 0.78, FontWeights.SemiBold, wrap: false).WithMargin(6, 10, 0, 4));
                foreach (var session in group)
                {
                    _list.Children.Add(BuildRow(session));
                }
            }
        }

        private UIElement BuildRow(SessionSummary session)
        {
            bool current = session.Id == _currentId;
            var title = Ui.Text(session.Title, Theme.TextKey, wrap: false);
            title.FontSize = Theme.ChatFontSize * 0.98;
            if (current)
            {
                title.FontWeight = FontWeights.SemiBold;
            }

            var meta = new List<string> { session.Updated.ToString(session.Updated.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm") };
            if (!string.IsNullOrEmpty(session.Source))
            {
                meta.Add(session.Source);
            }

            meta.Add($"{session.PromptCount} mesaj");
            var metaText = Ui.Text(string.Join("  ·  ", meta), Theme.MutedTextKey, 0.85, wrap: false).WithMargin(0, 2, 0, 0);

            var stack = new StackPanel();
            var titleRow = new DockPanel();
            if (current)
            {
                var badge = Ui.Text("şu anki", sizeFactor: 0.8, wrap: false).WithMargin(8, 1, 0, 0);
                badge.Foreground = Theme.Accent;
                DockPanel.SetDock(badge, Dock.Right);
                titleRow.Children.Add(badge);
            }

            titleRow.Children.Add(title);
            stack.Children.Add(titleRow);
            stack.Children.Add(metaText);

            var button = new Button
            {
                Content = stack,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 1),
                ToolTip = session.Title + "\n" + session.Id,
            };
            button.SetResourceReference(StyleProperty, Theme.GhostButtonStyle);
            button.Click += (s, e) => SessionSelected?.Invoke(session);
            return button;
        }

        private static string Bucket(DateTime time)
        {
            var days = (DateTime.Today - time.Date).TotalDays;
            if (days < 1)
            {
                return "Bugün";
            }

            if (days < 2)
            {
                return "Dün";
            }

            return days < 7 ? "Son 7 gün" : days < 31 ? "Son 30 gün" : "Daha eski";
        }
    }
}
