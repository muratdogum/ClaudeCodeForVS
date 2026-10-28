using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClaudeCodeVS.Cli;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using ClaudeCodeVS.Localization;

namespace ClaudeCodeVS.UI
{
    /// <summary>Renders the questions of Claude's AskUserQuestion tool and collects the answers.</summary>
    internal sealed class QuestionForm : StackPanel
    {
        private readonly List<Question> _questions = new List<Question>();

        public QuestionForm(JArray questions)
        {
            foreach (var question in questions.OfType<JObject>())
            {
                var text = question.Str("question") ?? string.Empty;
                var multiSelect = question.Bool("multiSelect");
                var group = Guid.NewGuid().ToString("N");

                if (question.Str("header") is string header)
                {
                    var chip = Ui.Text(header.ToUpperInvariant(), sizeFactor: 0.8, weight: FontWeights.SemiBold);
                    chip.Foreground = Theme.Accent;
                    chip.Margin = new Thickness(0, _questions.Count == 0 ? 0 : 10, 0, 2);
                    Children.Add(chip);
                }

                Children.Add(Ui.Text(text, Theme.TextKey, weight: FontWeights.SemiBold).WithMargin(0, 0, 0, 4));

                var options = new List<ToggleButton>();
                foreach (var option in question.Arr("options")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var content = new StackPanel();
                    content.Children.Add(Ui.Text(option.Str("label"), Theme.TextKey));
                    if (option.Str("description") is string description)
                    {
                        content.Children.Add(Ui.Text(description, Theme.MutedTextKey, 0.9));
                    }

                    ToggleButton toggle = multiSelect ? (ToggleButton)new CheckBox() : new RadioButton { GroupName = group };
                    toggle.Content = content;
                    toggle.Tag = option.Str("label");
                    toggle.Margin = new Thickness(0, 2, 0, 2);
                    toggle.SetResourceReference(Control.ForegroundProperty, Theme.TextKey);
                    options.Add(toggle);
                    Children.Add(toggle);
                }

                var other = new TextBox { Margin = new Thickness(0, 2, 0, 0), MinWidth = 200, ToolTip = L.T("Seçeneklerin dışında bir yanıt (isteğe bağlı)") };
                other.SetResourceReference(StyleProperty, VsResourceKeys.TextBoxStyleKey);
                var otherRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var otherLabel = Ui.Text(L.T("Diğer:"), Theme.MutedTextKey, wrap: false);
                otherLabel.VerticalAlignment = VerticalAlignment.Center;
                otherLabel.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(otherLabel, Dock.Left);
                otherRow.Children.Add(otherLabel);
                otherRow.Children.Add(other);
                Children.Add(otherRow);

                _questions.Add(new Question(text, options, other));
            }
        }

        /// <summary>Maps each question's text to the chosen labels (comma separated), as AskUserQuestion expects.</summary>
        public JObject GetAnswers()
        {
            var answers = new JObject();
            foreach (var question in _questions)
            {
                var selected = question.Options.Where(o => o.IsChecked == true).Select(o => (string)o.Tag).ToList();
                if (!string.IsNullOrWhiteSpace(question.Other.Text))
                {
                    selected.Add(question.Other.Text.Trim());
                }

                if (selected.Count > 0)
                {
                    answers[question.Text] = string.Join(", ", selected);
                }
            }

            return answers;
        }

        private sealed class Question
        {
            public Question(string text, List<ToggleButton> options, TextBox other)
            {
                Text = text;
                Options = options;
                Other = other;
            }

            public string Text { get; }

            public List<ToggleButton> Options { get; }

            public TextBox Other { get; }
        }
    }
}
