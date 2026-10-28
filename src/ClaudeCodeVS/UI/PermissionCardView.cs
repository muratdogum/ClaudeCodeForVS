using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeCodeVS.UI
{
    internal enum CardActionKind
    {
        Primary,
        Secondary,
        Ghost,
        Danger,
    }

    internal sealed class CardAction
    {
        /// <param name="outcome">Text shown once this action is taken; null for actions that leave the card open (e.g. "show diff").</param>
        public CardAction(string label, Action execute, string outcome = null, Brush outcomeBrush = null, CardActionKind kind = CardActionKind.Secondary)
        {
            Label = label;
            Execute = execute;
            Outcome = outcome;
            OutcomeBrush = outcomeBrush;
            Kind = kind;
        }

        public string Label { get; }

        public Action Execute { get; }

        public string Outcome { get; }

        public Brush OutcomeBrush { get; }

        public CardActionKind Kind { get; }

        public string Glyph { get; set; }

        public string ToolTip { get; set; }
    }

    /// <summary>A card asking the user to decide something Claude Code is waiting on (tool permission, plan, question).</summary>
    internal sealed class PermissionCardView : Border
    {
        private const int MaxButtonLabelLength = 64;

        private readonly WrapPanel _buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        private readonly Border _outcome;
        private readonly TextBlock _outcomeGlyph;
        private readonly TextBlock _outcomeText;

        public PermissionCardView(string requestId, string glyph, string title, UIElement body, IEnumerable<CardAction> actions, Action<Exception> onError)
        {
            RequestId = requestId;
            BorderThickness = new Thickness(1);
            BorderBrush = Theme.Accent;
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(14, 11, 14, 12);
            Margin = new Thickness(0, 6, 0, 8);
            this.Bind(BackgroundProperty, Theme.PanelKey);

            var heading = new StackPanel { Orientation = Orientation.Horizontal };
            heading.Children.Add(Ui.Glyph(glyph, 12, Theme.Accent).WithMargin(0, 1, 8, 0));
            var titleText = Ui.Text(title, Theme.TextKey, weight: FontWeights.SemiBold, wrap: false);
            titleText.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(titleText);

            var stack = new StackPanel();
            stack.Children.Add(heading);

            if (body is FrameworkElement element)
            {
                element.Margin = new Thickness(0, 8, 0, 0);
                stack.Children.Add(element);
            }

            foreach (var action in actions)
            {
                var label = action.Label.Length > MaxButtonLabelLength ? action.Label.Substring(0, MaxButtonLabelLength) + "…" : action.Label;
                var button = Ui.Button(label, () =>
                {
                    if (IsResolved)
                    {
                        return;
                    }

                    try
                    {
                        action.Execute();
                        if (action.Outcome != null)
                        {
                            Resolve(action.Outcome, action.OutcomeBrush ?? Theme.Success);
                        }
                    }
                    catch (Exception ex)
                    {
                        onError?.Invoke(ex);
                    }
                }, action.ToolTip ?? (label != action.Label ? action.Label : null), StyleFor(action.Kind), action.Glyph);
                button.Margin = new Thickness(0, 0, 6, 6);
                _buttons.Children.Add(button);
            }

            stack.Children.Add(_buttons);

            _outcomeGlyph = Ui.Glyph(Theme.Glyph.Check, 10).WithMargin(0, 1, 7, 0);
            _outcomeText = Ui.Text(string.Empty, weight: FontWeights.SemiBold, wrap: false);
            var outcomeRow = new StackPanel { Orientation = Orientation.Horizontal };
            outcomeRow.Children.Add(_outcomeGlyph);
            outcomeRow.Children.Add(_outcomeText);
            _outcome = new Border
            {
                Child = outcomeRow,
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(9, 4, 10, 4),
                CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed,
            };
            stack.Children.Add(_outcome);
            Child = stack;
        }

        public string RequestId { get; }

        public bool IsResolved { get; private set; }

        public void Resolve(string text, Brush brush)
        {
            IsResolved = true;
            _buttons.Visibility = Visibility.Collapsed;
            _outcomeText.Text = text;
            _outcomeText.Foreground = brush;
            _outcomeGlyph.Text = brush == Theme.Success ? Theme.Glyph.Check : brush == Theme.Error ? Theme.Glyph.Cancel : Theme.Glyph.Info;
            _outcomeGlyph.Foreground = brush;
            _outcome.Background = brush == Theme.Error ? Theme.ErrorSoft : Theme.Hover;
            _outcome.Visibility = Visibility.Visible;
            this.Bind(BorderBrushProperty, Theme.PanelBorderKey);
        }

        private static string StyleFor(CardActionKind kind)
        {
            switch (kind)
            {
                case CardActionKind.Primary: return Theme.PrimaryButtonStyle;
                case CardActionKind.Ghost: return Theme.GhostButtonStyle;
                case CardActionKind.Danger: return Theme.DangerButtonStyle;
                default: return Theme.SecondaryButtonStyle;
            }
        }
    }
}
