using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeCodeVS.UI
{
    /// <summary>One block of Claude's text. Re-renders at most every ~90 ms while tokens stream in.</summary>
    internal sealed class AssistantTextView : Border
    {
        private readonly MarkdownRenderer _renderer;
        private readonly RichTextBox _box;
        private readonly StringBuilder _text = new StringBuilder();
        private readonly DispatcherTimer _timer;

        public AssistantTextView(MarkdownRenderer renderer)
        {
            _renderer = renderer;
            _box = new RichTextBox
            {
                IsReadOnly = true,
                IsDocumentEnabled = true,
                IsTabStop = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
            };
            _box.SetResourceReference(Control.ForegroundProperty, Theme.TextKey);
            Child = _box;
            Margin = new Thickness(0, 4, 0, 2);

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
            _timer.Tick += (s, e) =>
            {
                _timer.Stop();
                Render(final: false);
            };
        }

        public string Text => _text.ToString();

        public void Append(string delta)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }

            _text.Append(delta);
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        public void SetFinal(string text)
        {
            _timer.Stop();
            _text.Clear().Append(text ?? string.Empty);
            Render(final: true);
        }

        public void Complete() => SetFinal(_text.ToString());

        private void Render(bool final) => _box.Document = _renderer.Render(_text.ToString(), final);
    }
}
