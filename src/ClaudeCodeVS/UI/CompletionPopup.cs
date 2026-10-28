using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeCodeVS.UI
{
    internal sealed class CompletionItem
    {
        public string InsertText { get; set; }

        public string Label { get; set; }

        public string Detail { get; set; }

        public string Glyph { get; set; }
    }

    /// <summary>Suggestion list shown above the composer for "/" commands and "@" file mentions.</summary>
    internal sealed class CompletionPopup
    {
        private readonly Popup _popup;
        private readonly FrameworkElement _target;
        private readonly StackPanel _rows = new StackPanel();
        private readonly ScrollViewer _scroll;
        private List<CompletionItem> _items = new List<CompletionItem>();
        private int _index;

        public CompletionPopup(FrameworkElement target)
        {
            _target = target;
            _scroll = new ScrollViewer
            {
                Content = _rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 290,
            };
            var border = new Border
            {
                Child = _scroll,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
            };
            border.Bind(Border.BackgroundProperty, Theme.PanelKey);
            border.Bind(Border.BorderBrushProperty, Theme.PanelBorderKey);
            _popup = new Popup
            {
                Child = border,
                PlacementTarget = target,
                Placement = PlacementMode.Top,
                AllowsTransparency = true,
                StaysOpen = true,
                VerticalOffset = -6,
            };
        }

        public event Action<CompletionItem> Accepted;

        public bool IsOpen => _popup.IsOpen;

        public void Show(IReadOnlyList<CompletionItem> items)
        {
            if (items == null || items.Count == 0)
            {
                Close();
                return;
            }

            _items = new List<CompletionItem>(items);
            _index = 0;
            _rows.Children.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                _rows.Children.Add(BuildRow(_items[i], i));
            }

            Highlight();
            _popup.Width = Math.Max(240, _target.ActualWidth);
            _popup.IsOpen = true;
        }

        public void Close() => _popup.IsOpen = false;

        /// <summary>Handles navigation keys while open; returns true when the key was consumed.</summary>
        public bool HandleKey(Key key)
        {
            if (!IsOpen)
            {
                return false;
            }

            switch (key)
            {
                case Key.Down:
                    _index = (_index + 1) % _items.Count;
                    Highlight();
                    return true;
                case Key.Up:
                    _index = (_index - 1 + _items.Count) % _items.Count;
                    Highlight();
                    return true;
                case Key.Enter:
                case Key.Tab:
                    Accept(_index);
                    return true;
                case Key.Escape:
                    Close();
                    return true;
                default:
                    return false;
            }
        }

        private UIElement BuildRow(CompletionItem item, int index)
        {
            var row = new DockPanel();
            var glyph = Ui.Glyph(item.Glyph ?? Theme.Glyph.Document, 11).Bind(TextBlock.ForegroundProperty, Theme.MutedTextKey).WithMargin(0, 1, 9, 0);
            glyph.Width = 14;
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            var label = Ui.Text(item.Label, Theme.PanelTextKey, wrap: false);
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(Ui.Text(item.Detail ?? string.Empty, Theme.MutedTextKey, 0.9, wrap: false));

            var border = new Border { Child = row, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 5, 8, 5), Cursor = Cursors.Hand, ToolTip = item.Detail };
            border.MouseEnter += (s, e) =>
            {
                _index = index;
                Highlight();
            };
            border.MouseLeftButtonUp += (s, e) => Accept(index);
            return border;
        }

        private void Highlight()
        {
            for (int i = 0; i < _rows.Children.Count; i++)
            {
                var row = (Border)_rows.Children[i];
                row.Background = i == _index ? Theme.AccentSoft : Brushes.Transparent;
                if (i == _index)
                {
                    row.BringIntoView();
                }
            }
        }

        private void Accept(int index)
        {
            if (index < 0 || index >= _items.Count)
            {
                return;
            }

            var item = _items[index];
            Close();
            Accepted?.Invoke(item);
        }
    }
}
