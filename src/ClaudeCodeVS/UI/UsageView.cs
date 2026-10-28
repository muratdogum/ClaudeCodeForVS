using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeCodeVS.Cli;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Plan usage (5-hour session, weekly and per-model limits), refreshed independently of the conversation.
    /// <see cref="Summary"/> is a row of compact chips for the header; clicking it reveals <see cref="Details"/>.
    /// </summary>
    internal sealed class UsageView
    {
        private readonly StackPanel _chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly UniformGrid _tiles = new UniformGrid { Rows = 1 };
        private readonly Border _details;
        private readonly TextBlock _stamp;
        private readonly Button _refresh;
        private UsageInfo _last;

        public UsageView()
        {
            var chipsHost = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(4), Child = _chips };
            chipsHost.ToolTip = "Kullanım ayrıntılarını göster/gizle";
            chipsHost.MouseEnter += (s, e) => chipsHost.Background = Theme.Hover;
            chipsHost.MouseLeave += (s, e) => chipsHost.Background = Brushes.Transparent;
            chipsHost.MouseLeftButtonUp += (s, e) => ToggleDetails();

            _refresh = Ui.IconButton(Theme.Glyph.Refresh, () => RefreshRequested?.Invoke(), "Kullanım bilgisini yenile");
            _refresh.FontSize = 11;

            var summary = new StackPanel { Orientation = Orientation.Horizontal };
            summary.Children.Add(chipsHost);
            summary.Children.Add(_refresh);
            Summary = summary;

            _stamp = Ui.Text(string.Empty, Theme.MutedTextKey, 0.85, wrap: false);
            var detailsStack = new StackPanel();
            detailsStack.Children.Add(_tiles);
            detailsStack.Children.Add(_stamp.WithMargin(2, 6, 0, 0));
            _details = new Border
            {
                Child = detailsStack,
                Margin = new Thickness(10, 8, 10, 2),
                Padding = new Thickness(12, 10, 12, 8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed,
            };
            _details.Bind(Border.BackgroundProperty, Theme.PanelKey);
            _details.Bind(Border.BorderBrushProperty, Theme.PanelBorderKey);
            Details = _details;

            _chips.Children.Add(Ui.Text("Kullanım…", Theme.MutedTextKey, 0.9, wrap: false).WithMargin(6, 0, 6, 0));
        }

        public event Action RefreshRequested;

        public FrameworkElement Summary { get; }

        public FrameworkElement Details { get; }

        public void ShowLoading()
        {
            _refresh.Opacity = 0.4;
            _refresh.ToolTip = "Yenileniyor…";
        }

        public void Show(UsageInfo info)
        {
            _last = info;
            _refresh.Opacity = 1;
            _refresh.ToolTip = "Kullanım bilgisini yenile";
            _chips.Children.Clear();
            _tiles.Children.Clear();

            if (!info.Available || info.Limits.Count == 0)
            {
                _chips.Children.Add(Ui.Text("Limit bilgisi yok", Theme.MutedTextKey, 0.9, wrap: false).WithMargin(6, 0, 6, 0));
                _tiles.Children.Add(Ui.Text("Bu hesap için plan kullanım limiti bilgisi yok (API anahtarıyla kullanımda limit gösterilmez).", Theme.MutedTextKey, 0.9));
            }

            foreach (var limit in info.Limits)
            {
                _chips.Children.Add(BuildChip(limit));
                _tiles.Children.Add(BuildTile(limit));
            }

            _tiles.Columns = Math.Max(1, Math.Min(3, info.Limits.Count));
            _stamp.Text = "Son güncelleme " + info.FetchedAt.ToLocalTime().ToString("HH:mm");
        }

        public void ShowError(string message)
        {
            _refresh.Opacity = 1;
            _refresh.ToolTip = "Kullanım alınamadı: " + message + "\nTekrar denemek için tıklayın.";
            if (_last == null)
            {
                _chips.Children.Clear();
                var failed = Ui.Text("Kullanım alınamadı", Theme.MutedTextKey, 0.9, wrap: false).WithMargin(6, 0, 6, 0);
                failed.ToolTip = message;
                _chips.Children.Add(failed);
            }
        }

        private void ToggleDetails()
        {
            _details.Visibility = _details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private static UIElement BuildChip(UsageLimit limit)
        {
            var percent = Clamp(limit.Percent);
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = Theme.BaseFontSize * 0.92 };
            text.Inlines.Add(new Run(limit.ShortLabel + " ").BindContent(TextElement.ForegroundProperty, Theme.MutedTextKey));
            text.Inlines.Add(new Run($"%{Math.Round(limit.Percent)}") { FontWeight = FontWeights.SemiBold, Foreground = BarBrush(limit, percent) });
            var reset = ShortReset(limit.ResetsAt);
            if (reset.Length > 0)
            {
                text.Inlines.Add(new Run(" · " + reset).BindContent(TextElement.ForegroundProperty, Theme.MutedTextKey));
            }

            var stack = new StackPanel { Margin = new Thickness(7, 2, 7, 2) };
            stack.Children.Add(text);
            stack.Children.Add(Bar(percent, BarBrush(limit, percent), 2).WithMargin(0, 2, 0, 0));
            stack.ToolTip = $"{limit.Label}: %{Math.Round(limit.Percent)} kullanıldı\n{LongReset(limit.ResetsAt)}";
            return stack;
        }

        private static UIElement BuildTile(UsageLimit limit)
        {
            var percent = Clamp(limit.Percent);
            var tile = new StackPanel { Margin = new Thickness(2, 0, 14, 0) };
            tile.Children.Add(Ui.Text(limit.Label.ToUpperInvariant(), Theme.MutedTextKey, 0.78, FontWeights.SemiBold, wrap: false));
            var value = Ui.Text($"%{Math.Round(limit.Percent)}", sizeFactor: 1.6, weight: FontWeights.SemiBold, wrap: false).WithMargin(0, 2, 0, 3);
            value.Foreground = BarBrush(limit, percent);
            tile.Children.Add(value);
            tile.Children.Add(Bar(percent, BarBrush(limit, percent), 4));
            var reset = Ui.Text(LongReset(limit.ResetsAt), Theme.MutedTextKey, 0.85).WithMargin(0, 4, 0, 0);
            reset.ToolTip = limit.ResetsAt?.ToLocalTime().ToString("f");
            tile.Children.Add(reset);
            return tile;
        }

        private static FrameworkElement Bar(double percent, Brush fill, double height)
        {
            var bar = new Grid { Height = height, MinWidth = 40 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(percent, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - percent, GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(height / 2) };
            track.Bind(Border.BackgroundProperty, Theme.PanelBorderKey);
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            if (percent > 0)
            {
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = fill, MinWidth = height });
            }

            return bar;
        }

        private static double Clamp(double percent) => Math.Max(0, Math.Min(100, percent));

        private static Brush BarBrush(UsageLimit limit, double percent)
        {
            var severity = limit.Severity ?? string.Empty;
            if (percent >= 90 || severity.IndexOf("critical", StringComparison.OrdinalIgnoreCase) >= 0 || severity.IndexOf("over", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Theme.Error;
            }

            if (percent >= 75 || severity.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Theme.Warning;
            }

            return Theme.Accent;
        }

        private static string ShortReset(DateTimeOffset? resetsAt)
        {
            if (resetsAt == null)
            {
                return string.Empty;
            }

            var remaining = resetsAt.Value - DateTimeOffset.Now;
            if (remaining.TotalMinutes < 1)
            {
                return "şimdi";
            }

            if (remaining.TotalHours < 1)
            {
                return $"{(int)Math.Ceiling(remaining.TotalMinutes)} dk";
            }

            return remaining.TotalDays < 1 ? $"{(int)remaining.TotalHours} sa" : $"{(int)remaining.TotalDays} g";
        }

        private static string LongReset(DateTimeOffset? resetsAt)
        {
            if (resetsAt == null)
            {
                return string.Empty;
            }

            var remaining = resetsAt.Value - DateTimeOffset.Now;
            if (remaining.TotalMinutes < 1)
            {
                return "Birazdan sıfırlanır";
            }

            if (remaining.TotalHours < 1)
            {
                return $"{(int)Math.Ceiling(remaining.TotalMinutes)} dk sonra sıfırlanır";
            }

            return remaining.TotalDays < 1
                ? $"{(int)remaining.TotalHours} saat sonra sıfırlanır"
                : $"{(int)remaining.TotalDays} gün sonra sıfırlanır";
        }
    }
}
