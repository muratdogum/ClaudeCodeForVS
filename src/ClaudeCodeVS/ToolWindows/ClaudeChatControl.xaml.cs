using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeCodeVS.Cli;
using ClaudeCodeVS.Ide;
using ClaudeCodeVS.Privacy;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;
using ClaudeCodeVS.Localization;

namespace ClaudeCodeVS.ToolWindows
{
    /// <summary>
    /// The Claude Code chat: renders the CLI's message stream, turns permission requests into cards,
    /// and keeps a plan usage strip that is refreshed independently of the conversation.
    /// </summary>
    public partial class ClaudeChatControl : UserControl, IDisposable, IChatHost
    {
        private const string IdeSystemPrompt =
            "You are running inside Visual Studio through the \"Claude Code for Visual Studio\" extension. " +
            "The user reads your replies in a Visual Studio tool window that renders Markdown. " +
            "Files you change on disk are reloaded by Visual Studio automatically. " +
            "When you mention a code location, write it as a Markdown link with a path relative to the working directory, " +
            "for example [Program.cs:42](src/Program.cs#L42), so the user can click it to open the file.";

        private const int MaxInlineSelectionLength = 40000;


        private readonly ClaudeCodePackage _package;
        private readonly IdeService _ide;
        private readonly MarkdownRenderer _markdown;
        private readonly ClaudeSession _session = new ClaudeSession();
        private readonly ConcurrentQueue<Action> _inbox = new ConcurrentQueue<Action>();
        private readonly Dictionary<string, ToolCallView> _toolCalls = new Dictionary<string, ToolCallView>();
        private readonly Dictionary<string, PermissionCardView> _pendingCards = new Dictionary<string, PermissionCardView>();
        private readonly List<ContextItem> _attachedContext = new List<ContextItem>();
        private readonly List<string> _history = new List<string>();
        private readonly DispatcherTimer _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private readonly DispatcherTimer _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        private readonly UsageView _usage = new UsageView();
        private readonly AuditLog _audit = new AuditLog(AuditLog.DefaultFolder);
        private readonly List<string> _turnShared = new List<string>();
        private string _sessionPolicyKey;
        private UIElement _privacySummary;
        private AuthStatus _auth;
        private DateTime _lastAuthCheck = DateTime.MinValue;
        private bool _authInFlight;
        private Process _loginProcess;
        private UIElement _accountCard;
        private PermissionCardView _loginCard;
        private readonly HistoryView _historyView = new HistoryView();
        private readonly List<ImageAttachment> _images = new List<ImageAttachment>();
        private readonly List<AssistantTextView> _turnTexts = new List<AssistantTextView>();
        private CompletionPopup _completion;
        private int _completionStart;
        private bool _suppressCompletion;
        private JArray _commands;
        private FileIndex _fileIndex;
        private bool _fileIndexBuilding;
        private bool _replaying;

        // Commands that open interactive terminal screens and do nothing useful in this panel.
        private static readonly HashSet<string> HiddenCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "login", "logout", "config", "settings", "theme", "vim", "terminal-setup", "ide", "exit", "quit", "resume", "doctor",
            "upgrade", "install-github-app", "install-slack-app", "privacy-settings", "statusline", "permissions", "hooks", "mcp",
            "agents", "plugin", "plugins", "rewind", "release-notes", "feedback", "bug", "share", "remote-control", "mobile", "keybindings-help",
        };
        private readonly TextBlock _contextGlyph = Ui.Glyph(Theme.Glyph.Document, 10);
        private readonly TextBlock _contextLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _thinking = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        private readonly TextBlock _thinkingGlyph = new TextBlock { Width = 16, FontSize = 14, Foreground = Theme.Accent, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _thinkingText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Accent };

        private int _drainScheduled;
        private int _historyIndex = -1;
        private int _spinnerFrame;
        private AssistantTextView _streamingText;
        private TextBlock _welcomeStatus;
        private UIElement _welcome;
        private bool _needsTurnHeader;
        private ContextItem _activeContext;
        private EnvDTE.WindowEvents _windowEvents;
        private IDisposable _solutionWatch;
        private string _cwd;
        private string _statusText = string.Empty;
        private string _activeModel;
        private string _announcedSessionId;
        private bool _busy;
        private bool _turnHadText;
        private bool _interruptRequested;
        private bool _stoppedByUser;
        private bool _stickToBottom = true;
        private bool _suppressComboEvents;
        private bool _usageInFlight;
        private bool? _billedPerToken;
        private DateTime _lastUsageFetch = DateTime.MinValue;
        private bool _disposed;

        public ClaudeChatControl(ClaudeCodePackage package)
        {
            _package = package;
            _ide = package.Ide;
            _markdown = new MarkdownRenderer(this);
            InitializeComponent();
            ApplyTheme();

            _session.MessageReceived += message => Post(() => HandleMessage(message));
            _session.Exited += e => Post(() => HandleExit(e));
            _spinnerTimer.Tick += (s, e) =>
            {
                _spinnerFrame++;
                _thinkingGlyph.Text = Theme.SparkleFrames[_spinnerFrame % Theme.SparkleFrames.Length];
            };
            _usageTimer.Tick += (s, e) =>
            {
                if (IsVisible)
                {
                    RequestUsageRefresh(force: false);
                }
            };
            _usage.RefreshRequested += () => RequestUsageRefresh(force: true);
            UsageSummaryHost.Child = _usage.Summary;
            UsageDetailsHost.Child = _usage.Details;

            _thinkingText.FontSize = Theme.ChatFontSize * 0.95;
            _thinking.Children.Add(_thinkingGlyph);
            _thinking.Children.Add(_thinkingText);

            var contextContent = new StackPanel { Orientation = Orientation.Horizontal };
            contextContent.Children.Add(_contextGlyph.WithMargin(0, 1, 6, 0));
            contextContent.Children.Add(_contextLabel);
            ContextToggle.Content = contextContent;

            var options = package.Options;
            ContextToggle.IsChecked = options.IncludeActiveFile;
            ContextToggle.Checked += (s, e) => SaveIncludeActiveFile(true);
            ContextToggle.Unchecked += (s, e) => SaveIncludeActiveFile(false);
            InputBox.GotKeyboardFocus += (s, e) => ComposerBorder.BorderBrush = Theme.Accent;
            InputBox.LostKeyboardFocus += (s, e) =>
            {
                ComposerBorder.Bind(Border.BorderBrushProperty, Theme.PanelBorderKey);
                _completion.Close();
            };

            HistoryHost.Child = _historyView;
            _historyView.Closed += HideHistory;
            _historyView.SessionSelected += session => Fire(() => LoadSessionAsync(session));
            _completion = new CompletionPopup(ComposerBorder);
            _completion.Accepted += AcceptCompletion;
            InputBox.SelectionChanged += (s, e) =>
            {
                if (InputBox.IsKeyboardFocused)
                {
                    UpdateCompletion();
                }
            };
            PopulateModeCombo(options.PermissionMode);
            PopulateModelCombo(null, string.IsNullOrWhiteSpace(options.Model) ? "default" : options.Model.Trim());

            IsKeyboardFocusWithinChanged += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if ((bool)e.NewValue)
                {
                    RefreshWorkingDirectory();
                    RefreshActiveContext();
                    UpdatePrivacyBadge();
                    UpdateAccountBadge();
                    RequestAuthRefresh(force: false);
                    PopulateModeCombo(PermissionModes.FromCli(SelectedModeValue()));
                }
            };
            IsVisibleChanged += (s, e) =>
            {
                if ((bool)e.NewValue && DateTime.UtcNow - _lastUsageFetch > _usageTimer.Interval)
                {
                    RequestUsageRefresh(force: false);
                }
            };
            Loaded += OnLoaded;
            L.LanguageChanged += OnLanguageChanged;

            ShowWelcome();
            RenderStatus();
            UpdatePrivacyBadge();
        }

        MarkdownRenderer IChatHost.Markdown => _markdown;

        internal void FocusInput()
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                InputBox.Focus();
                Keyboard.Focus(InputBox);
            }).FileAndForget("ClaudeCodeVS/focus");
        }

        internal void AttachContext(ContextItem item)
        {
            if (item == null)
            {
                return;
            }

            if (CurrentPolicy().IsProtected(item.FilePath))
            {
                AddNotice(L.F("{0} korunan bir dosya; gizlilik ayarları gereği Claude'a eklenmedi.", item.FileName), NoticeKind.Info);
                return;
            }

            _attachedContext.Remove(item);
            _attachedContext.Add(item);
            RenderAttachedContext();
        }

        internal void SendPrompt(string text, ContextItem context)
        {
            AttachContext(context);
            if (_busy)
            {
                InputBox.Text = text;
                FocusInput();
                return;
            }

            Fire(() => SendAsync(text));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            L.LanguageChanged -= OnLanguageChanged;
            _spinnerTimer.Stop();
            _usageTimer.Stop();
            if (_windowEvents != null && ThreadHelper.CheckAccess())
            {
#pragma warning disable VSTHRD010 // guarded by CheckAccess above
                _windowEvents.WindowActivated -= OnWindowActivated;
#pragma warning restore VSTHRD010
                _windowEvents = null;
            }

            if (_solutionWatch != null && ThreadHelper.CheckAccess())
            {
                _solutionWatch.Dispose();
                _solutionWatch = null;
            }

            _session.Dispose();
        }

        // ---------------------------------------------------------------- setup

        private void ApplyTheme()
        {
            this.Bind(BackgroundProperty, Theme.BackgroundKey);
            this.Bind(ForegroundProperty, Theme.TextKey);
            this.Bind(FontFamilyProperty, Theme.FontFamilyKey);
            this.Bind(FontSizeProperty, Theme.FontSizeKey);
            HeaderBorder.Bind(Border.BorderBrushProperty, Theme.BorderKey);
            ComposerBorder.Bind(Border.BackgroundProperty, Theme.PanelKey);
            ComposerBorder.Bind(Border.BorderBrushProperty, Theme.PanelBorderKey);
            InputBox.FontSize = Theme.ChatFontSize;
            InputPlaceholder.FontSize = Theme.ChatFontSize;
            InputPlaceholder.Bind(TextBlock.ForegroundProperty, Theme.MutedTextKey);
            StatusText.FontSize = Theme.BaseFontSize * 0.9;
            StatusText.Bind(TextBlock.ForegroundProperty, Theme.MutedTextKey);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Loaded -= OnLoaded;
            try
            {
                _cwd = _ide.GetWorkingDirectory();
                _windowEvents = _ide.GetWindowEvents();
                if (_windowEvents != null)
                {
                    _windowEvents.WindowActivated += OnWindowActivated;
                }

                // The panel can be restored before the solution or folder finishes opening.
                _solutionWatch = _ide.WatchSolutionChanges(OnSolutionChanged);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            RefreshActiveContext();
            UpdateWelcome();
            RenderStatus();
            _usageTimer.Start();
            RequestUsageRefresh(force: true);
            RequestAuthRefresh(force: true);
        }

        // XAML texts follow the language through bindings; everything built in code is rebuilt here.
        // Messages already in the transcript keep the language they were written in.
        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Post(() => OnLanguageChanged(sender, e));
                return;
            }

            ThreadHelper.ThrowIfNotOnUIThread();
            PopulateModeCombo(PermissionModes.FromCli(SelectedModeValue()));
            PopulateModelCombo(_session.Models, SelectedModelValue());
            _historyView.ApplyLanguage();
            _usage.ApplyLanguage();
            if (_welcome != null)
            {
                ShowWelcome();
                if (_auth != null && !_auth.LoggedIn)
                {
                    _loginCard = null;
                    ShowLoginCard(null);
                }
            }

            RenderAttachedContext();
            RefreshActiveContext();
            UpdatePrivacyBadge();
            UpdateAccountBadge();
            RenderStatus();
            RequestUsageRefresh(force: true);
        }

        private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RefreshActiveContext();
        }

        private void OnSolutionChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RefreshWorkingDirectory();
            RefreshActiveContext();
        }

        private void RefreshWorkingDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string current;
            try
            {
                current = _ide.GetWorkingDirectory();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return;
            }

            if (SamePath(current, _cwd))
            {
                return;
            }

            _cwd = current;
            _fileIndex = null;
            UpdateWelcome();
            RenderStatus();
            RequestUsageRefresh(force: false);
        }

        private void ShowWelcome()
        {
            Transcript.Children.Clear();
            var panel = new StackPanel { Margin = new Thickness(0, 28, 0, 12), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Center };

            var logo = new TextBlock { Text = Theme.Sparkle, FontSize = 40, Foreground = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(logo);
            var title = Ui.Text("Claude Code", Theme.TextKey, 1.7, FontWeights.SemiBold, wrap: false).WithMargin(0, 2, 0, 6);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(title);
            var subtitle = Ui.Text(L.T("Kodunuz hakkında soru sorun, değişiklik isteyin veya bir görevi devredin. Claude'un yapmak istediği işlemler için burada onayınız istenir."), Theme.MutedTextKey);
            subtitle.TextAlignment = TextAlignment.Center;
            subtitle.FontSize = Theme.ChatFontSize * 0.95;
            panel.Children.Add(subtitle.WithMargin(0, 0, 0, 20));

            var suggestions = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            foreach (var (glyph, label, prompt) in new[]
            {
                (Theme.Glyph.Document, L.T("Bu projeyi açıkla"), L.T("Bu projenin ne yaptığını, yapısını ve önemli dosyalarını kısaca açıkla.")),
                (Theme.Glyph.Bug, L.T("Hata ara"), L.T("Kod tabanında olası hataları ve riskli noktaları bul; önem sırasına göre listele.")),
                (Theme.Glyph.Check, L.T("Test yaz"), L.T("Aktif dosyadaki kod için birim testleri öner ve yaz.")),
                (Theme.Glyph.Lightning, L.T("Kodu iyileştir"), L.T("Aktif dosyadaki kodu okunabilirlik ve performans açısından incele; iyileştirmeler öner.")),
            })
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(Ui.Glyph(glyph, 12, Theme.Accent).WithMargin(0, 1, 9, 0));
                content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                var button = new Button { Content = content, ToolTip = prompt, Margin = new Thickness(3) };
                button.SetResourceReference(StyleProperty, Theme.SuggestionButtonStyle);
                button.Click += (s, e) => Fire(() => SendAsync(prompt));
                suggestions.Children.Add(button);
            }

            panel.Children.Add(suggestions);

            var hints = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
            foreach (var (key, action) in new[] { ("Enter", L.T("gönder")), ("Shift+Enter", L.T("yeni satır")), ("Esc", L.T("durdur")), ("↑", L.T("önceki mesaj")), ("/clear", L.T("yeni sohbet")) })
            {
                hints.Children.Add(KeyHint(key, action));
            }

            panel.Children.Add(hints);

            _welcomeStatus = Ui.Text(string.Empty, Theme.MutedTextKey, 0.85).WithMargin(0, 16, 0, 0);
            _welcomeStatus.TextAlignment = TextAlignment.Center;
            panel.Children.Add(_welcomeStatus);
            _welcome = panel;
            Transcript.Children.Add(panel);
            UpdateWelcome();
        }

        private static UIElement KeyHint(string key, string action)
        {
            var keyCap = new Border
            {
                Child = Ui.Text(key, Theme.PanelTextKey, 0.85, wrap: false),
                Padding = new Thickness(5, 0, 5, 1),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1, 1, 1, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            keyCap.Bind(Border.BackgroundProperty, Theme.PanelKey);
            keyCap.Bind(Border.BorderBrushProperty, Theme.PanelBorderKey);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
            row.Children.Add(keyCap);
            row.Children.Add(Ui.Text(action, Theme.MutedTextKey, 0.85, wrap: false).WithMargin(5, 0, 0, 0));
            return row;
        }

        private void UpdateWelcome()
        {
            if (_welcomeStatus == null)
            {
                return;
            }

            var configured = _package.Options.ClaudeExecutablePath;
            var executable = ClaudeCliLocator.Locate(configured);
            if (executable != null)
            {
                _welcomeStatus.Text = L.T("Çalışma klasörü: ") + (_cwd ?? "-");
                _welcomeStatus.ToolTip = "claude: " + executable;
                _welcomeStatus.SetResourceReference(TextBlock.ForegroundProperty, Theme.MutedTextKey);
            }
            else
            {
                _welcomeStatus.Text = ClaudeCliLocator.NotFoundMessage(configured);
                _welcomeStatus.ToolTip = null;
                _welcomeStatus.Foreground = Theme.Warning;
            }
        }

        private void RemoveWelcome()
        {
            if (_welcome != null)
            {
                Transcript.Children.Remove(_welcome);
                _welcome = null;
                _welcomeStatus = null;
            }
        }

        // ---------------------------------------------------------------- sending

        private void SubmitInput()
        {
            if (_busy)
            {
                return;
            }

            var text = InputBox.Text.Trim();
            if (text.Length == 0 && _attachedContext.Count == 0 && _images.Count == 0)
            {
                return;
            }

            _completion.Close();

            InputBox.Clear();
            if (text.Length > 0)
            {
                _history.Add(text);
            }

            _historyIndex = -1;
            Fire(() => SendAsync(text));
        }

        private async Task SendAsync(string text)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_busy)
            {
                return;
            }

            if (text == "/clear" || text == "/new" || text == "/reset")
            {
                StartNewSession();
                return;
            }

            bool isCommand = text.StartsWith("/", StringComparison.Ordinal);
            var policy = CurrentPolicy();

            if (_auth != null && !_auth.LoggedIn)
            {
                InputBox.Text = text;
                ShowLoginCard(L.T("Mesaj göndermek için önce Claude Code'a giriş yapın."));
                return;
            }

            if (policy.Enabled && _auth != null && _auth.LoggedIn && !_auth.MatchesOrganization(_package.Options.ExpectedOrganization))
            {
                InputBox.Text = text;
                AddNotice(L.F("Claude Code \"{0}\" hesabıyla açık; şirket ayarlarında beklenen kuruluş \"{1}\". Gizlilik modunda başka bir hesaba kod gönderilmez. Hesabı değiştirin veya ayarı güncelleyin.", _auth.ShortName, _package.Options.ExpectedOrganization), NoticeKind.Error);
                ShowAccountCard();
                return;
            }

            // "@path" mentions are expanded by the CLI itself; never let one reach a protected file.
            var mentioned = MentionedFiles(text);
            var blocked = mentioned.Where(policy.IsProtected).ToList();
            if (blocked.Count > 0)
            {
                InputBox.Text = text;
                AddNotice(L.T("Korunan dosyalar gizlilik ayarları gereği Claude'a gönderilemez: ") + string.Join(", ", blocked.Select(Path.GetFileName)), NoticeKind.Info);
                FocusInput();
                return;
            }

            var attached = new List<ContextItem>(_attachedContext);
            var contexts = new List<ContextItem>(attached);
            var images = isCommand ? new List<ImageAttachment>() : new List<ImageAttachment>(_images);
            _attachedContext.Clear();
            _images.Clear();
            RenderAttachedContext();

            if (!isCommand && ContextToggle.IsChecked == true)
            {
                RefreshActiveContext();
                if (_activeContext != null
                    && !policy.IsProtected(_activeContext.FilePath)
                    && !contexts.Any(c => string.Equals(c.FilePath, _activeContext.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    contexts.Insert(0, _activeContext);
                }
            }

            contexts.RemoveAll(c => policy.IsProtected(c.FilePath));

            if (text.Length == 0)
            {
                text = images.Count > 0 && contexts.Count == 0 ? L.T("Eklenen görseli incele.") : L.T("Eklenen kodu incele.");
            }

            int maskedSecrets = 0;
            if (policy.Enabled && policy.ScanSecrets && !isCommand)
            {
                var findings = new List<(string Source, IReadOnlyList<SecretMatch> Matches)> { (L.T("Mesaj"), SecretScanner.Scan(text)) };
                findings.AddRange(contexts.Where(c => c.HasSelection).Select(c => (c.DisplayName, SecretScanner.Scan(c.SelectedText))));
                if (findings.Any(f => f.Matches.Count > 0))
                {
                    var decision = await AskAboutSecretsAsync(findings.Where(f => f.Matches.Count > 0).ToList());
                    if (decision == SecretDecision.Cancel)
                    {
                        InputBox.Text = text;
                        _attachedContext.AddRange(attached);
                        _images.AddRange(images);
                        RenderAttachedContext();
                        FocusInput();
                        return;
                    }

                    if (decision == SecretDecision.Mask)
                    {
                        maskedSecrets = findings.Sum(f => f.Matches.Count);
                        text = SecretScanner.Redact(text, findings[0].Matches);
                        contexts = contexts.Select(c => c.HasSelection ? c.WithSelectedText(SecretScanner.Redact(c.SelectedText, SecretScanner.Scan(c.SelectedText))) : c).ToList();
                    }
                }
            }

            _turnShared.Clear();
            _turnShared.AddRange(contexts.Select(c => c.FilePath));
            _turnShared.AddRange(mentioned);
            _audit.Enabled = policy.Enabled && policy.AuditLog;

            AddUserMessage(text, isCommand ? new List<ContextItem>() : contexts, images);
            if (maskedSecrets > 0)
            {
                AddNotice(L.F("{0} gizli değer {1} ile maskelenerek gönderildi.", maskedSecrets, SecretScanner.Mask), NoticeKind.Info);
            }
            _turnHadText = false;
            SetBusy(true, L.T("Başlatılıyor…"));

            try
            {
                if (_package.Options.SaveAllBeforeSend)
                {
                    _ide.SaveOpenDocuments();
                }

                await EnsureSessionAsync();
                var auditAttachments = contexts.Select(c => c.HasSelection ? $"{c.FilePath}:{c.StartLine}-{c.EndLine}" : c.FilePath)
                    .Concat(mentioned)
                    .Concat(images.Select(i => L.F("[görsel] {0}", i.Name)));
                _audit.Prompt(_cwd, _session.SessionId, text, auditAttachments, maskedSecrets);
                _session.SendUserMessage(isCommand ? text : ComposePrompt(text, contexts), images.Select(i => i.Content).ToList());
                SetStatus(L.T("Claude düşünüyor…"));
            }
            catch (Exception ex)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddNotice(ex.Message, NoticeKind.Error);
                if (LooksLikeAuthError(ex.Message))
                {
                    HandleAuthProblem();
                }

                SetBusy(false);
            }
        }

        private async Task EnsureSessionAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            _cwd = _ide.GetWorkingDirectory();
            var policy = CurrentPolicy();
            var environment = policy.BuildEnvironment();
            var settings = policy.BuildSettings();
            bool strictMcp = policy.Enabled && !policy.AllowMcpServers;
            var policyKey = string.Join("|", environment.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value))
                + "|" + settings?.ToString(Newtonsoft.Json.Formatting.None) + "|" + strictMcp;
            bool sameDirectory = SamePath(_session.WorkingDirectory, _cwd);

            if (_session.IsRunning && sameDirectory && policyKey == _sessionPolicyKey)
            {
                return;
            }

            string resume = null;
            if (_session.IsRunning && !sameDirectory)
            {
                AddNotice(L.F("Çalışma klasörü değişti, yeni oturum başlatılıyor: {0}", _cwd), NoticeKind.Info);
                _session.Reset();
            }
            else if (_session.IsRunning)
            {
                AddNotice(L.T("Gizlilik/sağlayıcı ayarları değişti; konuşma yeni ayarlarla sürdürülüyor."), NoticeKind.Info);
                resume = _session.SessionId;
                _session.Stop();
            }
            else if (_session.SessionId != null && sameDirectory)
            {
                resume = _session.SessionId;
            }

            var options = _package.Options;
            var executable = ClaudeCliLocator.Locate(options.ClaudeExecutablePath)
                ?? throw new ClaudeProcessException(ClaudeCliLocator.NotFoundMessage(options.ClaudeExecutablePath));

            var model = SelectedModelValue();
            var mode = SelectedModeValue();
            if (policy.Enabled && (mode == "bypassPermissions" || mode == "auto"))
            {
                mode = "default";
            }

            var launch = new ClaudeLaunchOptions
            {
                ExecutablePath = executable,
                WorkingDirectory = _cwd,
                Model = model == "default" ? null : model,
                PermissionMode = mode,
                ResumeSessionId = resume,
                AppendSystemPrompt = options.AppendIdeSystemPrompt ? IdeSystemPrompt : null,
                AdditionalArguments = options.AdditionalArguments,
                Environment = environment,
                Settings = settings,
                StrictMcpConfig = strictMcp,
            };
            _sessionPolicyKey = policyKey;

            SetStatus(resume != null ? L.T("Oturum sürdürülüyor…") : L.T("Claude Code başlatılıyor…"));
            try
            {
                await _session.StartAsync(launch);
            }
            catch (Exception) when (resume != null)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddNotice(L.T("Önceki oturum sürdürülemedi, yeni oturum başlatılıyor."), NoticeKind.Info);
                _session.Reset();
                launch.ResumeSessionId = null;
                await _session.StartAsync(launch);
            }

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            PopulateModelCombo(_session.Models, SelectedModelValue());
        }

        private static string ComposePrompt(string text, IReadOnlyList<ContextItem> contexts)
        {
            if (contexts.Count == 0)
            {
                return text;
            }

            var builder = new StringBuilder();
            builder.AppendLine("<ide_context>");
            foreach (var context in contexts)
            {
                if (!context.HasSelection)
                {
                    builder.AppendLine($"The user has {context.FilePath} open in Visual Studio.");
                }
                else if (context.SelectedText.Length > MaxInlineSelectionLength)
                {
                    builder.AppendLine($"The user selected lines {context.StartLine}-{context.EndLine} of {context.FilePath} (too large to include; read the file).");
                }
                else
                {
                    builder.AppendLine($"The user selected lines {context.StartLine}-{context.EndLine} of {context.FilePath}:");
                    builder.AppendLine("```" + context.Language);
                    builder.AppendLine(context.SelectedText.TrimEnd('\r', '\n'));
                    builder.AppendLine("```");
                }
            }

            builder.AppendLine("</ide_context>");
            builder.AppendLine();
            builder.Append(text);
            return builder.ToString();
        }

        private void StartNewSession()
        {
            _session.Reset();
            _toolCalls.Clear();
            CancelPendingCards(null);
            _streamingText = null;
            _activeModel = null;
            _announcedSessionId = null;
            _turnTexts.Clear();
            ContextUsageButton.Visibility = Visibility.Collapsed;
            HistoryHost.Visibility = Visibility.Collapsed;
            SetBusy(false);
            ShowWelcome();
            RenderStatus();
        }

        private async Task StopAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!_busy)
            {
                return;
            }

            _interruptRequested = true;
            SetStatus(L.T("Durduruluyor…"));
            try
            {
                await _session.InterruptAsync();
            }
            catch (Exception)
            {
                // The CLI did not answer: kill it. The session id is kept, so the next message resumes it.
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _session.Stop();
                FinishStreaming();
                CancelPendingCards(L.T("Durduruldu"));
                AddNotice(L.T("Claude Code durduruldu."), NoticeKind.Muted);
                SetBusy(false);
            }
        }

        // ---------------------------------------------------------------- incoming messages

        private void Post(Action action)
        {
            _inbox.Enqueue(action);
            if (Interlocked.Exchange(ref _drainScheduled, 1) != 0)
            {
                return;
            }

            // A single drain preserves message order and batches bursts of stream events.
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                Interlocked.Exchange(ref _drainScheduled, 0);
                while (!_disposed && _inbox.TryDequeue(out var next))
                {
                    try
                    {
                        next();
                    }
                    catch (Exception ex)
                    {
                        AddNotice(L.T("Arayüz hatası: ") + ex.Message, NoticeKind.Error);
                    }
                }
            }).FileAndForget("ClaudeCodeVS/drain");
        }

        private void HandleMessage(JObject message)
        {
            switch (message.Str("type"))
            {
                case "system":
                    HandleSystem(message);
                    break;
                case "stream_event":
                    HandleStreamEvent(message);
                    break;
                case "assistant":
                    HandleAssistant(message);
                    break;
                case "user":
                    HandleToolResults(message);
                    break;
                case "result":
                    HandleResult(message);
                    break;
                case "control_request":
                    HandleControlRequest(message);
                    break;
                case "control_cancel_request":
                    var id = message.Str("request_id");
                    if (id != null && _pendingCards.TryGetValue(id, out var card))
                    {
                        card.Resolve(L.T("İstek iptal edildi"), Theme.Warning);
                        _pendingCards.Remove(id);
                    }

                    break;
                case "rate_limit_event":
                    RequestUsageRefresh(force: false);
                    break;
            }
        }

        private void HandleSystem(JObject message)
        {
            switch (message.Str("subtype"))
            {
                case "init":
                    _activeModel = message.Str("model") ?? _activeModel;
                    SyncModeCombo(message.Str("permissionMode"));
                    var sessionId = message.Str("session_id");
                    if (sessionId != null && sessionId != _announcedSessionId)
                    {
                        _announcedSessionId = sessionId;
                        AddNotice(L.F("Oturum hazır · {0} · {1}", _activeModel, message.Str("cwd")), NoticeKind.Muted);
                    }

                    break;
                case "status":
                    SyncModeCombo(message.Str("permissionMode"));
                    if (message.Str("status") == "compacting")
                    {
                        SetStatus(L.T("Konuşma özetleniyor…"));
                    }

                    break;
                case "compact_boundary":
                    AddNotice(L.T("Konuşma geçmişi özetlendi."), NoticeKind.Muted);
                    break;
            }
        }

        private void HandleStreamEvent(JObject message)
        {
            if (message.Str("parent_tool_use_id") != null)
            {
                return; // subagent output is summarised on its Task card instead
            }

            var streamEvent = message.Obj("event");
            switch (streamEvent?.Str("type"))
            {
                case "content_block_start":
                    var block = streamEvent.Obj("content_block");
                    switch (block?.Str("type"))
                    {
                        case "text":
                            FinishStreaming();
                            _streamingText = AddAssistantText();
                            break;
                        case "thinking":
                            SetStatus(L.T("Düşünüyor…"));
                            break;
                        case "tool_use":
                            SetStatus(L.F("{0} hazırlanıyor…", block.Str("name")));
                            break;
                    }

                    break;
                case "content_block_delta":
                    var delta = streamEvent.Obj("delta");
                    if (delta?.Str("type") == "text_delta")
                    {
                        if (_streamingText == null)
                        {
                            _streamingText = AddAssistantText();
                        }

                        _streamingText.Append(delta.Str("text"));
                        _turnHadText = true;
                        SetStatus(L.T("Yazıyor…"));
                    }

                    break;
            }
        }

        private void HandleAssistant(JObject message)
        {
            var parent = message.Str("parent_tool_use_id");
            var blocks = message.Obj("message")?.Arr("content");
            if (blocks == null)
            {
                return;
            }

            foreach (var block in blocks.OfType<JObject>())
            {
                var type = block.Str("type");
                if (type == "tool_use")
                {
                    TrackToolUse(block.Str("name"), block.Obj("input"));
                }

                if (parent != null)
                {
                    if (type == "tool_use" && _toolCalls.TryGetValue(parent, out var owner))
                    {
                        owner.AddChildActivity($"{block.Str("name")}  {ToolCallView.Summarize(block.Str("name"), block.Obj("input"), this)}");
                    }

                    continue;
                }

                if (type == "text")
                {
                    var text = block.Str("text") ?? string.Empty;
                    if (_streamingText != null)
                    {
                        if (text.Trim().Length == 0)
                        {
                            Transcript.Children.Remove(_streamingText);
                        }
                        else
                        {
                            _streamingText.SetFinal(text);
                        }

                        _streamingText = null;
                    }
                    else if (text.Trim().Length > 0)
                    {
                        AddAssistantText().SetFinal(text);
                    }

                    _turnHadText |= text.Trim().Length > 0;
                }
                else if (type == "tool_use")
                {
                    FinishStreaming();
                    var id = block.Str("id");
                    var view = new ToolCallView(id, block.Str("name"), block.Obj("input"), this);
                    if (id != null)
                    {
                        _toolCalls[id] = view;
                    }

                    EnsureTurnHeader();
                    Append(view);
                    SetStatus(L.F("{0} çalışıyor…", block.Str("name")));
                }
            }
        }

        private void HandleToolResults(JObject message)
        {
            if (!(message.Obj("message")?["content"] is JArray blocks))
            {
                return;
            }

            foreach (var block in blocks.OfType<JObject>())
            {
                if (block.Str("type") == "tool_result"
                    && block.Str("tool_use_id") is string id
                    && _toolCalls.TryGetValue(id, out var view))
                {
                    view.SetResult(block["content"], block.Bool("is_error"));
                }
            }

            if (_busy)
            {
                SetStatus(L.T("Claude düşünüyor…"));
            }
        }

        private void HandleResult(JObject message)
        {
            FinishStreaming();
            CancelPendingCards(L.T("İptal edildi"));

            var subtype = message.Str("subtype") ?? string.Empty;
            bool isError = message.Bool("is_error") || subtype.StartsWith("error", StringComparison.Ordinal);
            var resultText = message.Str("result");

            if (_interruptRequested)
            {
                AddNotice(L.T("■ Durduruldu."), NoticeKind.Muted);
            }
            else if (_stoppedByUser)
            {
                // A denial with interrupt ends the turn as an error; it is the user's choice, not a failure.
                AddNotice(L.T("■ Reddedildi. Claude talimatınızı bekliyor."), NoticeKind.Muted);
            }
            else if (isError)
            {
                var errorText = resultText;
                if (string.IsNullOrWhiteSpace(errorText) && message.Arr("errors") is JArray errors && errors.Count > 0)
                {
                    errorText = string.Join("\n", errors.Select(e => e.Type == JTokenType.String ? (string)e : e.ToString()));
                }

                AddNotice(string.IsNullOrWhiteSpace(errorText) ? L.F("Claude Code bir hata bildirdi ({0}).", subtype) : errorText, NoticeKind.Error);
                if (LooksLikeAuthError(errorText))
                {
                    HandleAuthProblem();
                }
            }
            else if (!_turnHadText && !string.IsNullOrWhiteSpace(resultText))
            {
                // Slash commands such as /cost or /context answer only through the result.
                AddAssistantText().SetFinal(resultText);
            }

            if (!_interruptRequested && !_stoppedByUser)
            {
                var seconds = (message.Num("duration_ms") ?? 0) / 1000.0;
                var steps = (int)(message.Num("num_turns") ?? 0);
                var cost = message.Num("total_cost_usd");
                var stats = L.F("{0} {1} adım · {2:0.0} sn", (isError ? "✗" : "✓"), steps, seconds);

                // total_cost_usd is the CLI's API-price estimate. On a Pro/Max/Team/Enterprise login nothing is
                // billed per token (usage counts against the plan limits shown in the header), so it is only
                // shown when the account is known to pay per token.
                if (cost > 0 && _billedPerToken == true)
                {
                    stats += L.F(" · API maliyeti ${0:0.0000}", cost);
                }

                var shared = _turnShared.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (shared.Count > 0)
                {
                    stats += L.F(" · ↑ {0} dosya/konum modelle paylaşıldı", shared.Count);
                }

                var statsText = Ui.Text(stats, Theme.MutedTextKey, 0.82, wrap: false);
                statsText.VerticalAlignment = VerticalAlignment.Center;
                if (shared.Count > 0)
                {
                    statsText.ToolTip = L.T("Bu turda içeriği modele gönderilen dosyalar ve aranan konumlar:\n")
                        + string.Join("\n", shared.Take(30).Select(p => "• " + ((IChatHost)this).ToDisplayPath(p)))
                        + (shared.Count > 30 ? L.F("\n… ve {0} tane daha", shared.Count - 30) : string.Empty);
                }

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
                row.Children.Add(statsText);
                var turnTexts = _turnTexts.ToList();
                if (turnTexts.Count > 0)
                {
                    Button copy = null;
                    copy = Ui.IconButton(Theme.Glyph.Copy, () =>
                    {
                        try
                        {
                            Clipboard.SetText(string.Join("\n\n", turnTexts.Select(t => t.Text.Trim())));
                            copy.Content = Theme.Glyph.Check;
                        }
                        catch (System.Runtime.InteropServices.COMException)
                        {
                        }
                    }, L.T("Claude'un bu turdaki yanıtını kopyala"));
                    copy.FontSize = 10;
                    copy.Padding = new Thickness(5, 2, 5, 2);
                    copy.Margin = new Thickness(6, 0, 0, 0);
                    row.Children.Add(copy);
                }

                Append(row);
            }

            SetBusy(false);
            RequestUsageRefresh(force: true);
            RequestContextUsage();
        }

        /// <summary>Records what a tool exposes to the model, for the turn summary and the local audit log.</summary>
        private void TrackToolUse(string tool, JObject input)
        {
            if (input == null || _replaying)
            {
                return;
            }

            string target;
            switch (tool)
            {
                case "Read":
                case "NotebookRead":
                    target = input.Str("file_path") ?? input.Str("notebook_path");
                    _turnShared.Add(target);
                    break;
                case "Grep":
                case "Glob":
                    var location = input.Str("path") ?? _session.WorkingDirectory ?? _cwd;
                    target = $"{input.Str("pattern")} @ {location}";
                    _turnShared.Add(location);
                    break;
                case "Edit":
                case "MultiEdit":
                case "Write":
                case "NotebookEdit":
                    target = input.Str("file_path") ?? input.Str("notebook_path");
                    break;
                case "Bash":
                case "PowerShell":
                    target = input.Str("command");
                    break;
                case "WebFetch":
                    target = input.Str("url");
                    break;
                case "WebSearch":
                    target = input.Str("query");
                    break;
                default:
                    target = ToolCallView.Summarize(tool, input, this);
                    break;
            }

            _audit.Tool(_session.SessionId, tool, target);
        }

        private void HandleExit(ClaudeExitedEventArgs e)
        {
            FinishStreaming();
            CancelPendingCards(L.T("Oturum sonlandı"));
            if (_busy || e.ExitCode != 0)
            {
                var text = L.F("Claude Code süreci kapandı (çıkış kodu {0}).", e.ExitCode?.ToString() ?? "?");
                if (!string.IsNullOrWhiteSpace(e.StandardErrorTail))
                {
                    text += "\n" + e.StandardErrorTail;
                }

                AddNotice(text, NoticeKind.Error);
                if (LooksLikeAuthError(text))
                {
                    HandleAuthProblem();
                }
            }

            SetBusy(false);
        }

        // ---------------------------------------------------------------- permission requests

        private void HandleControlRequest(JObject message)
        {
            var requestId = message.Str("request_id");
            var request = message.Obj("request");
            if (requestId == null || request == null)
            {
                return;
            }

            if (request.Str("subtype") != "can_use_tool")
            {
                try
                {
                    _session.RejectControlRequest(requestId, L.T("Visual Studio eklentisi bu isteği desteklemiyor: ") + request.Str("subtype"));
                }
                catch (ClaudeProcessException)
                {
                }

                return;
            }

            FinishStreaming();
            var tool = request.Str("tool_name") ?? "?";
            var input = request.Obj("input") ?? new JObject();
            if (request.Str("tool_use_id") is string toolUseId && _toolCalls.TryGetValue(toolUseId, out var call))
            {
                call.SetAwaitingPermission();
            }

            PermissionCardView card;
            if (tool == "AskUserQuestion")
            {
                card = BuildQuestionCard(requestId, input);
            }
            else if (tool == "ExitPlanMode")
            {
                card = BuildPlanCard(requestId, input);
            }
            else
            {
                card = BuildToolPermissionCard(requestId, request, tool, input);
            }

            _pendingCards[requestId] = card;
            EnsureTurnHeader();
            Append(card);
            _stickToBottom = true;
            SetStatus(L.T("Onayınız bekleniyor…"));
        }

        private PermissionCardView BuildToolPermissionCard(string requestId, JObject request, string tool, JObject input)
        {
            var body = new StackPanel();
            var detail = tool == "Bash" || tool == "PowerShell" ? input.Str("command") : ToolCallView.Summarize(tool, input, this);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                body.Children.Add(Ui.CodeBox(Ui.Truncate(detail, 3000)));
            }

            var description = request.Str("description");
            if (!string.IsNullOrWhiteSpace(description) && description != detail && !(detail ?? string.Empty).EndsWith(description, StringComparison.Ordinal))
            {
                body.Children.Add(Ui.Text(description, Theme.MutedTextKey, 0.95).WithMargin(0, 2, 0, 0));
            }

            var reason = request.Str("decision_reason") ?? request.Str("blocked_path");
            if (!string.IsNullOrWhiteSpace(reason))
            {
                body.Children.Add(Ui.Text(L.T("Neden: ") + reason, Theme.MutedTextKey, 0.9).WithMargin(0, 2, 0, 0));
            }

            bool isShell = tool == "Bash" || tool == "PowerShell";
            bool networkRisk = isShell && CurrentPolicy().Enabled && PrivacyPolicy.LooksLikeNetworkCommand(detail);
            if (networkRisk)
            {
                var warning = new DockPanel();
                var icon = Ui.Glyph(Theme.Glyph.Warning, 12, Theme.Warning).WithMargin(0, 1, 8, 0);
                icon.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(icon, Dock.Left);
                warning.Children.Add(icon);
                warning.Children.Add(Ui.Text(L.T("Bu komut ağ üzerinden veri gönderebilir. Şirket içi kod veya bilgilerin dışarı çıkmadığından emin değilseniz reddedin."), Theme.TextKey, 0.95));
                body.Children.Add(new Border
                {
                    Child = warning,
                    Background = Theme.AccentSoft,
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(9, 6, 9, 6),
                    Margin = new Thickness(0, 6, 0, 0),
                });
            }

            var target = isShell ? detail : ToolCallView.Summarize(tool, input, this);
            void Decide(string decision) => _audit.Permission(_session.SessionId, tool, target, decision);

            // A risky command keeps "Evet" as a plain button so it is never the most prominent choice.
            var actions = new List<CardAction>
            {
                new CardAction(L.T("Evet"), () => { _session.AllowTool(requestId, input, null); Decide("allowed"); }, L.T("İzin verildi"),
                    kind: networkRisk ? CardActionKind.Secondary : CardActionKind.Primary) { Glyph = Theme.Glyph.Check },
            };

            var suggestions = request.Arr("permission_suggestions");
            if (suggestions != null && suggestions.Count > 0 && !networkRisk)
            {
                actions.Add(new CardAction(DescribeSuggestions(suggestions), () => { _session.AllowTool(requestId, input, suggestions); Decide("allowed+rule"); }, L.T("İzin verildi, kural eklendi")));
            }

            if (DiffPreview.CanPreview(tool))
            {
                actions.Add(new CardAction(L.T("Değişikliği göster"), () => Fire(() => DiffPreview.ShowAsync(_package, tool, input)), kind: CardActionKind.Ghost)
                {
                    Glyph = Theme.Glyph.View,
                    ToolTip = L.T("Önerilen değişikliği Visual Studio diff görünümünde aç"),
                });
            }

            actions.Add(new CardAction(L.T("Hayır"), () =>
            {
                _stoppedByUser = true;
                _session.DenyTool(requestId, "The user denied this action. Stop and wait for the user's instructions.", interrupt: true);
                Decide("denied");
                FocusInput();
            }, L.T("Reddedildi"), Theme.Error, CardActionKind.Danger));

            var title = L.F("İzin gerekiyor · {0}", request.Str("display_name") ?? tool);
            return new PermissionCardView(requestId, Theme.Glyph.Lock, title, body, actions, ex => AddNotice(ex.Message, NoticeKind.Error));
        }

        private PermissionCardView BuildPlanCard(string requestId, JObject input)
        {
            var plan = new AssistantTextView(_markdown);
            plan.SetFinal(input.Str("plan") ?? string.Empty);
            var actions = new[]
            {
                new CardAction(L.T("Onayla, düzenlemeleri otomatik kabul et"), () => _session.AllowTool(requestId, input, ModeUpdate("acceptEdits")), L.T("Plan onaylandı"), kind: CardActionKind.Primary) { Glyph = Theme.Glyph.Check },
                new CardAction(L.T("Onayla, her düzenlemeyi sor"), () => _session.AllowTool(requestId, input, ModeUpdate("default")), L.T("Plan onaylandı")),
                new CardAction(L.T("Planlamaya devam et"), () =>
                {
                    _stoppedByUser = true;
                    _session.DenyTool(requestId, "The user wants to keep planning. Wait for their feedback on the plan.", interrupt: true);
                    FocusInput();
                }, L.T("Plan reddedildi"), Theme.Error, CardActionKind.Danger),
            };
            return new PermissionCardView(requestId, Theme.Glyph.Document, L.T("Plan onayı"), plan, actions, ex => AddNotice(ex.Message, NoticeKind.Error));
        }

        private PermissionCardView BuildQuestionCard(string requestId, JObject input)
        {
            var form = new QuestionForm(input.Arr("questions") ?? new JArray());
            var actions = new[]
            {
                new CardAction(L.T("Yanıtla"), () =>
                {
                    var answered = (JObject)input.DeepClone();
                    answered["answers"] = form.GetAnswers();
                    _session.AllowTool(requestId, answered, null);
                }, L.T("Yanıtlandı"), kind: CardActionKind.Primary) { Glyph = Theme.Glyph.Send },
                new CardAction(L.T("Atla"), () => _session.DenyTool(requestId, "The user skipped these questions. Continue with your best judgment.", interrupt: false), L.T("Atlandı"), Theme.Warning, CardActionKind.Ghost),
            };
            return new PermissionCardView(requestId, Theme.Glyph.Help, L.T("Claude bir soru soruyor"), form, actions, ex => AddNotice(ex.Message, NoticeKind.Error));
        }

        private static JArray ModeUpdate(string mode) =>
            new JArray(new JObject { ["type"] = "setMode", ["mode"] = mode, ["destination"] = "session" });

        private static string DescribeSuggestions(JArray suggestions)
        {
            var parts = new List<string>();
            foreach (var suggestion in suggestions.OfType<JObject>())
            {
                switch (suggestion.Str("type"))
                {
                    case "setMode" when suggestion.Str("mode") == "acceptEdits":
                        parts.Add(L.T("bu oturumda düzenlemeleri otomatik kabul et"));
                        break;
                    case "setMode":
                        parts.Add(L.T("modu değiştir: ") + suggestion.Str("mode"));
                        break;
                    case "addRules":
                        var rules = suggestion.Arr("rules")?.OfType<JObject>()
                            .Select(r => r.Str("ruleContent") is string content ? $"{r.Str("toolName")}({content})" : r.Str("toolName"))
                            ?? Enumerable.Empty<string>();
                        parts.Add(L.T("bir daha sorma: ") + string.Join(", ", rules));
                        break;
                    case "addDirectories":
                        parts.Add(L.T("klasöre erişim ver: ") + string.Join(", ", suggestion.Arr("directories")?.Select(d => d.ToString()) ?? Enumerable.Empty<string>()));
                        break;
                    default:
                        parts.Add(L.T("bir daha sorma"));
                        break;
                }
            }

            return L.T("Evet, ") + string.Join(" + ", parts.Distinct());
        }

        private void CancelPendingCards(string reason)
        {
            foreach (var card in _pendingCards.Values.Where(c => !c.IsResolved))
            {
                card.Resolve(reason ?? L.T("İptal edildi"), Theme.Warning);
            }

            _pendingCards.Clear();
        }

        // ---------------------------------------------------------------- usage

        private void RequestUsageRefresh(bool force)
        {
            if (_disposed || _usageInFlight || (!force && DateTime.UtcNow - _lastUsageFetch < TimeSpan.FromSeconds(30)))
            {
                return;
            }

            var executable = ClaudeCliLocator.Locate(_package.Options.ClaudeExecutablePath);
            if (executable == null && !_session.IsRunning)
            {
                _usage.ShowError(ClaudeCliLocator.NotFoundMessage(_package.Options.ClaudeExecutablePath));
                return;
            }

            _usageInFlight = true;
            _lastUsageFetch = DateTime.UtcNow;
            _usage.ShowLoading();
            var workingDirectory = _cwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var environment = CurrentPolicy().BuildEnvironment();

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                UsageInfo info = null;
                JArray commands = null;
                string error = null;
                try
                {
                    JObject response;
                    if (_session.IsRunning)
                    {
                        response = await _session.GetUsageAsync();
                        commands = _session.Commands;
                    }
                    else
                    {
                        var snapshot = await Task.Run(() => ClaudeSession.FetchUsageAsync(executable, workingDirectory, environment));
                        response = snapshot.Usage;
                        commands = snapshot.Commands;
                    }

                    info = UsageInfo.Parse(response);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _usageInFlight = false;
                if (commands != null)
                {
                    _commands = commands;
                }

                if (info != null)
                {
                    _usage.Show(info);
                    _billedPerToken = string.IsNullOrEmpty(info.SubscriptionType) && !info.Available;
                }
                else
                {
                    _usage.ShowError(error);
                }
            }).FileAndForget("ClaudeCodeVS/usage");
        }

        // ---------------------------------------------------------------- transcript helpers

        private enum NoticeKind
        {
            Muted,
            Info,
            Error,
            Stats,
        }

        /// <summary>Adds to the end of the transcript, keeping the "thinking" indicator last.</summary>
        private void Append(UIElement element)
        {
            RemoveWelcome();
            int thinking = Transcript.Children.IndexOf(_thinking);
            if (thinking >= 0)
            {
                Transcript.Children.Insert(thinking, element);
            }
            else
            {
                Transcript.Children.Add(element);
            }
        }

        /// <summary>Adds the small "✻ Claude" label before the first output of each turn.</summary>
        private void EnsureTurnHeader()
        {
            if (!_needsTurnHeader)
            {
                return;
            }

            _needsTurnHeader = false;
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 2) };
            header.Children.Add(new TextBlock { Text = Theme.Sparkle, Foreground = Theme.Accent, FontSize = Theme.ChatFontSize, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            var name = Ui.Text("Claude", Theme.TextKey, 0.95, FontWeights.SemiBold, wrap: false);
            name.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(name);
            Append(header);
        }

        private AssistantTextView AddAssistantText()
        {
            EnsureTurnHeader();
            var view = new AssistantTextView(_markdown);
            Append(view);
            _turnTexts.Add(view);
            return view;
        }

        private void FinishStreaming()
        {
            if (_streamingText == null)
            {
                return;
            }

            if (_streamingText.Text.Trim().Length == 0)
            {
                Transcript.Children.Remove(_streamingText);
            }
            else
            {
                _streamingText.Complete();
            }

            _streamingText = null;
        }

        private void AddUserMessage(string text, IReadOnlyList<ContextItem> contexts, IReadOnlyList<ImageAttachment> images = null, int pastImages = 0)
        {
            var stack = new StackPanel();
            if (images != null && images.Count > 0)
            {
                var thumbnails = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                foreach (var image in images)
                {
                    thumbnails.Children.Add(new Border
                    {
                        Child = new System.Windows.Controls.Image { Source = image.Thumbnail, MaxHeight = 72, MaxWidth = 120, Stretch = Stretch.Uniform },
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 0, 6, 0),
                        ToolTip = image.Name,
                        ClipToBounds = true,
                    });
                }

                stack.Children.Add(thumbnails);
            }

            stack.Children.Add(Ui.SelectableText(text, fontSize: Theme.ChatFontSize));
            if (contexts.Count > 0 || pastImages > 0)
            {
                var chips = new WrapPanel { Margin = new Thickness(0, 7, 0, -4) };
                foreach (var context in contexts)
                {
                    chips.Children.Add(Ui.Chip(Theme.Glyph.Document, context.DisplayName, context.FilePath, null));
                }

                if (pastImages > 0)
                {
                    chips.Children.Add(Ui.Chip("", pastImages == 1 ? L.T("görsel") : L.F("{0} görsel", pastImages), null, null));
                }

                stack.Children.Add(chips);
            }

            _turnTexts.Clear();

            var border = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 9),
                Margin = new Thickness(0, 18, 0, 2),
            };
            border.Bind(Border.BackgroundProperty, Theme.PanelKey);
            border.Bind(Border.BorderBrushProperty, Theme.PanelBorderKey);
            Append(border);
            _stickToBottom = true;
            _needsTurnHeader = true;
        }

        private FrameworkElement AddNotice(string text, NoticeKind kind)
        {
            FrameworkElement element;
            switch (kind)
            {
                case NoticeKind.Error:
                    var errorRow = new DockPanel();
                    var errorIcon = Ui.Glyph(Theme.Glyph.Warning, 12, Theme.Error).WithMargin(0, 2, 9, 0);
                    errorIcon.VerticalAlignment = VerticalAlignment.Top;
                    DockPanel.SetDock(errorIcon, Dock.Left);
                    errorRow.Children.Add(errorIcon);
                    var errorText = Ui.SelectableText(text);
                    errorText.Foreground = Theme.Error;
                    errorRow.Children.Add(errorText);
                    element = new Border
                    {
                        Child = errorRow,
                        Background = Theme.ErrorSoft,
                        BorderBrush = Theme.ErrorBorder,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 7, 10, 7),
                        Margin = new Thickness(0, 6, 0, 6),
                    };
                    break;
                case NoticeKind.Info:
                    var infoRow = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
                    var infoIcon = Ui.Glyph(Theme.Glyph.Info, 11).Bind(TextBlock.ForegroundProperty, Theme.MutedTextKey).WithMargin(0, 2, 8, 0);
                    infoIcon.VerticalAlignment = VerticalAlignment.Top;
                    DockPanel.SetDock(infoIcon, Dock.Left);
                    infoRow.Children.Add(infoIcon);
                    infoRow.Children.Add(Ui.Text(text, Theme.MutedTextKey));
                    element = infoRow;
                    break;
                case NoticeKind.Stats:
                    element = Ui.Text(text, Theme.MutedTextKey, 0.82, wrap: false).WithMargin(0, 4, 0, 4);
                    break;
                default:
                    element = Ui.Text(text, Theme.MutedTextKey, 0.88).WithMargin(0, 4, 0, 4);
                    break;
            }

            Append(element);
            return element;
        }

        // ---------------------------------------------------------------- privacy

        private enum SecretDecision
        {
            Cancel,
            Mask,
            SendAsIs,
        }

        private PrivacyPolicy CurrentPolicy() => _package.Options.CreatePrivacyPolicy();

        /// <summary>Shows what looks like a credential (never the value itself) and waits for the user's choice.</summary>
        private System.Threading.Tasks.Task<SecretDecision> AskAboutSecretsAsync(IReadOnlyList<(string Source, IReadOnlyList<SecretMatch> Matches)> findings)
        {
            var decision = new System.Threading.Tasks.TaskCompletionSource<SecretDecision>();
            var body = new StackPanel();
            body.Children.Add(Ui.Text(L.F("Gönderilecek içerikte gizli olabilecek değerler bulundu. Maskelenirse değerlerin yerine {0} gönderilir.", SecretScanner.Mask), Theme.MutedTextKey, 0.95));
            var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (source, matches) in findings)
            {
                foreach (var match in matches.Take(8))
                {
                    list.Children.Add(Ui.Text(L.F("•  {0}  —  {1}, satır {2}", match.Kind, source, match.Line), Theme.TextKey, 0.95));
                }
            }

            body.Children.Add(list);
            var actions = new[]
            {
                new CardAction(L.T("Maskeleyerek gönder"), () => decision.TrySetResult(SecretDecision.Mask), L.T("Maskelendi"), kind: CardActionKind.Primary) { Glyph = Theme.Glyph.Lock },
                new CardAction(L.T("Olduğu gibi gönder"), () => decision.TrySetResult(SecretDecision.SendAsIs), L.T("Maskelenmeden gönderildi"), Theme.Warning),
                new CardAction(L.T("Vazgeç"), () => decision.TrySetResult(SecretDecision.Cancel), L.T("Gönderilmedi"), Theme.Warning, CardActionKind.Ghost),
            };
            Append(new PermissionCardView(null, Theme.Glyph.Warning, L.T("Gizli bilgi olabilir"), body, actions, ex => AddNotice(ex.Message, NoticeKind.Error)));
            _stickToBottom = true;
            return decision.Task;
        }

        private void UpdatePrivacyBadge()
        {
            var policy = CurrentPolicy();
            bool managed = PrivacyPolicy.IsManagedPolicyPresent();
            PrivacyBadgeGlyph.Text = policy.Enabled ? Theme.Glyph.Lock : Theme.Glyph.Warning;
            PrivacyBadgeGlyph.Foreground = policy.Enabled ? Theme.Success : Theme.Warning;
            PrivacyBadgeText.Text = policy.Enabled ? L.T("Gizli mod") : L.T("Gizlilik kapalı");
            PrivacyBadge.ToolTip = string.Join("\n", policy.Describe())
                + L.T("\nModel sağlayıcısı: ") + PrivacyPolicy.ProviderName(policy.Provider)
                + (managed ? L.T("\nKurumsal Claude Code politikası bu bilgisayarda etkin.") : string.Empty)
                + L.T("\n\nAyrıntılar için tıklayın.");
        }

        private void OnPrivacyBadgeClick(object sender, RoutedEventArgs e) => ShowPrivacySummary();

        private void ShowPrivacySummary()
        {
            var policy = CurrentPolicy();
            bool managed = PrivacyPolicy.IsManagedPolicyPresent();
            var body = new StackPanel();

            void Section(string title)
            {
                body.Children.Add(Ui.Text(title.ToUpperInvariant(), Theme.MutedTextKey, 0.8, FontWeights.SemiBold).WithMargin(0, 8, 0, 3));
            }

            void Line(string glyph, Brush brush, string text)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var icon = Ui.Glyph(glyph, 10, brush).WithMargin(0, 2, 8, 0);
                icon.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
                row.Children.Add(Ui.Text(text, Theme.TextKey, 0.95));
                body.Children.Add(row);
            }

            Section(L.T("Bu eklentideki korumalar"));
            foreach (var line in policy.Describe())
            {
                Line(policy.Enabled ? Theme.Glyph.Check : Theme.Glyph.Warning, policy.Enabled ? Theme.Success : Theme.Warning, line);
            }

            Section(L.T("Veri nereye gider"));
            Line(Theme.Glyph.Info, Theme.Accent, L.F("Claude'un yanıt üretmek için gördüğü her şey (mesajlar, eklenen kod, araçların okuduğu dosyalar ve komut çıktıları) model sağlayıcısına gönderilir: {0}.",
                PrivacyPolicy.ProviderName(policy.Provider)));
            Line(Theme.Glyph.Info, Theme.Accent, L.T("Ticari hesaplarda (Team, Enterprise, API, Bedrock/Vertex/Foundry) Anthropic kod ve istemleri model eğitiminde kullanmaz; standart saklama süresi 30 gündür. Sıfır veri saklama (ZDR) Claude for Enterprise'da Anthropic'in onayıyla açılabilir. Bedrock/Vertex/Foundry'de bulut sağlayıcınızın sözleşmesi geçerlidir."));
            Line(Theme.Glyph.Info, Theme.Accent, L.T("Claude Code konuşma kayıtlarını bu bilgisayarda %USERPROFILE%\\.claude\\projects altında düz metin olarak 30 gün tutar (cleanupPeriodDays ile ayarlanır)."));

            Section(L.T("Kurumsal politika"));
            Line(managed ? Theme.Glyph.Check : Theme.Glyph.Info, managed ? Theme.Success : Theme.Accent, managed
                ? L.T("Bu bilgisayarda BT tarafından dağıtılmış bir Claude Code politikası var; kullanıcı ayarları onu geçersiz kılamaz.")
                : L.F("Merkezi politika bulunamadı. Bu eklentinin ayarlarını kullanıcı değiştirebilir; zorunlu kılmak için BT ekibi {0} dosyasını dağıtmalıdır (README'de örnek var).",
                    PrivacyPolicy.ManagedSettingsPath));

            var actions = new List<CardAction>
            {
                new CardAction(L.T("Gizlilik ayarları"), () => _package.ShowOptionPage(typeof(Options.GeneralOptionsPage)), kind: CardActionKind.Secondary) { Glyph = Theme.Glyph.Lock },
            };
            if (policy.Enabled && policy.AuditLog)
            {
                actions.Add(new CardAction(L.T("Gönderim günlüğünü aç"), () =>
                {
                    Directory.CreateDirectory(_audit.Folder);
                    Process.Start(new ProcessStartInfo(_audit.Folder) { UseShellExecute = true });
                }, kind: CardActionKind.Ghost) { Glyph = Theme.Glyph.Document });
            }

            // One summary at a time: clicking the badge again moves a fresh one to the end.
            if (_privacySummary != null)
            {
                Transcript.Children.Remove(_privacySummary);
            }

            _privacySummary = new PermissionCardView(null, policy.Enabled ? Theme.Glyph.Lock : Theme.Glyph.Warning,
                policy.Enabled ? L.T("Gizlilik modu açık") : L.T("Gizlilik modu kapalı"), body, actions, ex => AddNotice(ex.Message, NoticeKind.Error));
            Append(_privacySummary);
            _stickToBottom = true;
        }

        private void RenderAttachedContext()
        {
            ContextChips.Children.Clear();
            foreach (var context in _attachedContext.ToList())
            {
                ContextChips.Children.Add(Ui.Chip(Theme.Glyph.Document, context.DisplayName, context.FilePath, () =>
                {
                    _attachedContext.Remove(context);
                    RenderAttachedContext();
                }));
            }

            foreach (var image in _images.ToList())
            {
                var chip = Ui.Chip("", $"{image.Name} · {image.Bytes / 1024} KB", null, () =>
                {
                    _images.Remove(image);
                    RenderAttachedContext();
                });
                chip.ToolTip = new System.Windows.Controls.Image { Source = image.Thumbnail, MaxHeight = 96 };
                ContextChips.Children.Add(chip);
            }

            ContextChips.Visibility = _attachedContext.Count + _images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AttachImage(ImageAttachment image)
        {
            if (_images.Count >= 10)
            {
                AddNotice(L.T("Bir mesaja en fazla 10 görsel eklenebilir."), NoticeKind.Info);
                return;
            }

            _images.Add(image);
            RenderAttachedContext();
            FocusInput();
        }

        // ---------------------------------------------------------------- account

        private void RequestAuthRefresh(bool force)
        {
            if (_disposed || _authInFlight || (!force && DateTime.UtcNow - _lastAuthCheck < TimeSpan.FromSeconds(60)))
            {
                return;
            }

            var executable = ClaudeCliLocator.Locate(_package.Options.ClaudeExecutablePath);
            if (executable == null)
            {
                return;
            }

            _authInFlight = true;
            _lastAuthCheck = DateTime.UtcNow;
            var environment = CurrentPolicy().BuildEnvironment();
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                var status = await AuthStatus.QueryAsync(executable, environment);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _authInFlight = false;
                if (status.Error != null)
                {
                    Debug.WriteLine("auth status failed: " + status.Error);
                    return;
                }

                bool wasLoggedOut = _auth != null && !_auth.LoggedIn;
                _auth = status;
                UpdateAccountBadge();

                if (!status.LoggedIn && _welcome != null)
                {
                    ShowLoginCard(null);
                }
                else if (status.LoggedIn && (wasLoggedOut || (_loginCard != null && !_loginCard.IsResolved)))
                {
                    _loginCard?.Resolve(L.F("Giriş yapıldı · {0}", status.ShortName), Theme.Success);
                    _loginCard = null;
                    AddNotice(L.F("Claude Code'a giriş yapıldı: {0}", status.ShortName) + (status.PlanName != null ? $" ({status.PlanName})" : string.Empty), NoticeKind.Info);
                    RequestUsageRefresh(force: true);
                }
            }).FileAndForget("ClaudeCodeVS/auth");
        }

        private void UpdateAccountBadge()
        {
            var policy = CurrentPolicy();
            AccountBadgeText.Visibility = Visibility.Collapsed;
            if (_auth == null)
            {
                AccountBadgeGlyph.Text = "";
                AccountBadgeGlyph.SetResourceReference(TextBlock.ForegroundProperty, Theme.MutedTextKey);
                AccountBadge.ToolTip = L.T("Hesap durumu alınıyor…");
                return;
            }

            if (!_auth.LoggedIn)
            {
                AccountBadgeGlyph.Text = Theme.Glyph.Warning;
                AccountBadgeGlyph.Foreground = Theme.Warning;
                AccountBadgeText.Text = L.T("Giriş yap");
                AccountBadgeText.Foreground = Theme.Warning;
                AccountBadgeText.Visibility = Visibility.Visible;
                AccountBadge.ToolTip = L.T("Claude Code bu bilgisayarda oturum açmamış. Giriş yapmak için tıklayın.");
                return;
            }

            bool wrongOrg = policy.Enabled && !_auth.MatchesOrganization(_package.Options.ExpectedOrganization);
            bool consumer = policy.Enabled && _auth.IsConsumerPlan;
            AccountBadgeGlyph.Text = "";
            if (wrongOrg || consumer)
            {
                AccountBadgeGlyph.Foreground = wrongOrg ? Theme.Error : Theme.Warning;
            }
            else
            {
                AccountBadgeGlyph.SetResourceReference(TextBlock.ForegroundProperty, Theme.MutedTextKey);
            }

            var lines = new List<string> { _auth.ShortName };
            if (!string.IsNullOrEmpty(_auth.Email))
            {
                lines.Add(_auth.Email);
            }

            lines.Add(_auth.MethodName + (_auth.PlanName != null ? " · " + _auth.PlanName : string.Empty));
            if (wrongOrg)
            {
                lines.Add(L.T("⚠ Beklenen kuruluş değil: ") + _package.Options.ExpectedOrganization);
            }
            else if (consumer)
            {
                lines.Add(L.T("⚠ Kişisel abonelik; şirket kodu için kuruluş hesabı önerilir."));
            }

            lines.Add(L.T("Ayrıntılar için tıklayın."));
            AccountBadge.ToolTip = string.Join("\n", lines);
        }

        private void OnAccountBadgeClick(object sender, RoutedEventArgs e)
        {
            if (_auth != null && !_auth.LoggedIn)
            {
                ShowLoginCard(null);
            }
            else
            {
                ShowAccountCard();
            }
        }

        private void ShowAccountCard()
        {
            if (_auth == null)
            {
                RequestAuthRefresh(force: true);
                AddNotice(L.T("Hesap durumu alınıyor…"), NoticeKind.Muted);
                return;
            }

            var policy = CurrentPolicy();
            var body = new StackPanel();

            void Row(string label, string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }

                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var name = Ui.Text(label, Theme.MutedTextKey, 0.95, wrap: false);
                name.Width = 120;
                DockPanel.SetDock(name, Dock.Left);
                row.Children.Add(name);
                row.Children.Add(Ui.SelectableText(value));
                body.Children.Add(row);
            }

            Row(L.T("Kuruluş"), _auth.OrgName);
            Row("E-posta", _auth.Email);
            Row(L.T("Giriş yöntemi"), _auth.MethodName);
            Row(L.T("Abonelik"), _auth.PlanName);
            Row(L.T("Model sağlayıcısı"), _auth.ProviderName);

            if (policy.Enabled && !_auth.MatchesOrganization(_package.Options.ExpectedOrganization))
            {
                body.Children.Add(Warning(L.F("Beklenen kuruluş \"{0}\" değil. Gizlilik modunda bu hesapla mesaj gönderilmez.", _package.Options.ExpectedOrganization), Theme.Error));
            }
            else if (_auth.IsConsumerPlan)
            {
                body.Children.Add(Warning(L.T("Kişisel bir abonelikle (Free/Pro/Max) giriş yapılmış. Bu planlarda Anthropic, kullanıcının tercihine bağlı olarak verileri model eğitiminde kullanabilir. Şirket kodu için kuruluşunuzun Team/Enterprise hesabıyla giriş yapın."), Theme.Warning));
            }

            body.Children.Add(Ui.Text(L.T("Bu oturum bilgisayardaki Claude Code ile ortaktır (terminal ve VS Code eklentisi de aynı girişi kullanır)."), Theme.MutedTextKey, 0.9).WithMargin(0, 8, 0, 0));

            var actions = new List<CardAction>();
            if (!_auth.UsesCloudProvider)
            {
                actions.Add(new CardAction(L.T("Başka hesapla giriş"), () => ShowLoginCard(L.T("Başka bir hesapla giriş yapın; mevcut oturumun yerini alır.")), kind: CardActionKind.Secondary) { Glyph = "" });
                actions.Add(new CardAction(L.T("Çıkış yap"), () => Fire(LogoutAsync), L.T("Çıkış yapılıyor…"), Theme.Warning, CardActionKind.Danger));
            }

            actions.Add(new CardAction(L.T("Yenile"), () => RequestAuthRefresh(force: true), kind: CardActionKind.Ghost) { Glyph = Theme.Glyph.Refresh });

            if (_accountCard != null)
            {
                Transcript.Children.Remove(_accountCard);
            }

            _accountCard = new PermissionCardView(null, "", L.T("Claude Code hesabı"), body, actions, ex => AddNotice(ex.Message, NoticeKind.Error));
            Append(_accountCard);
            _stickToBottom = true;
        }

        private void ShowLoginCard(string reason)
        {
            if (_loginCard != null && !_loginCard.IsResolved)
            {
                Transcript.Children.Remove(_loginCard);
            }

            var policy = CurrentPolicy();
            var body = new StackPanel();
            body.Children.Add(Ui.Text(reason ?? L.T("Claude Code bu bilgisayarda oturum açmamış."), Theme.TextKey));
            body.Children.Add(Ui.Text(L.T("Giriş; terminal ve VS Code eklentisiyle ortaktır, bir kez yapmanız yeterli. Butona basınca bir pencere açılır ve tarayıcıda giriş sayfası gelir; giriş tamamlanınca bu panel kendiliğinden güncellenir."), Theme.MutedTextKey, 0.95).WithMargin(0, 4, 0, 0));
            if (policy.Provider != ModelProvider.Anthropic)
            {
                body.Children.Add(Warning(L.F("Seçili sağlayıcı {0}: Claude hesabıyla giriş gerekmez, kimlik bilgileri ortam değişkenlerinden gelir (Araçlar > Seçenekler > Claude Code).", PrivacyPolicy.ProviderName(policy.Provider)), Theme.Accent));
            }

            if (!string.IsNullOrWhiteSpace(_package.Options.ExpectedOrganization))
            {
                body.Children.Add(Ui.Text(L.F("Şirket hesabıyla giriş yapın: {0}.", _package.Options.ExpectedOrganization), Theme.TextKey, 0.95, FontWeights.SemiBold).WithMargin(0, 6, 0, 0));
            }

            var actions = new[]
            {
                new CardAction(L.T("Claude hesabıyla giriş"), () => StartLogin(LoginMethod.ClaudeAccount), kind: CardActionKind.Primary) { Glyph = "", ToolTip = L.T("Claude Pro/Max/Team/Enterprise aboneliği") },
                new CardAction(L.T("SSO ile giriş"), () => StartLogin(LoginMethod.Sso)) { ToolTip = L.T("Kuruluşunuz tek oturum açma (SSO) kullanıyorsa") },
                new CardAction(L.T("Console (API) ile giriş"), () => StartLogin(LoginMethod.Console), kind: CardActionKind.Ghost) { ToolTip = L.T("Anthropic Console hesabı; kullanım API üzerinden faturalanır") },
                new CardAction(L.T("Durumu yenile"), () => RequestAuthRefresh(force: true), kind: CardActionKind.Ghost) { Glyph = Theme.Glyph.Refresh },
            };
            _loginCard = new PermissionCardView(null, Theme.Glyph.Lock, L.T("Claude Code'a giriş yapın"), body, actions, ex => AddNotice(ex.Message, NoticeKind.Error));
            Append(_loginCard);
            _stickToBottom = true;
        }

        private void StartLogin(LoginMethod method)
        {
            var executable = ClaudeCliLocator.Locate(_package.Options.ClaudeExecutablePath);
            if (executable == null)
            {
                AddNotice(ClaudeCliLocator.NotFoundMessage(_package.Options.ClaudeExecutablePath), NoticeKind.Error);
                return;
            }

            if (_loginProcess != null && !_loginProcess.HasExited)
            {
                AddNotice(L.T("Giriş penceresi zaten açık; oradaki adımları tamamlayın."), NoticeKind.Info);
                return;
            }

            try
            {
                _loginProcess = AuthStatus.StartLogin(executable, method, CurrentPolicy().BuildEnvironment());
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                AddNotice(L.T("Giriş başlatılamadı: ") + ex.Message, NoticeKind.Error);
                return;
            }

            if (_loginProcess != null)
            {
                _loginProcess.Exited += (s, e) => Post(() =>
                {
                    // A running session still holds the old credentials; the next message restarts (and resumes) it.
                    _session.Stop();
                    RequestAuthRefresh(force: true);
                });
            }

            AddNotice(L.T("Açılan pencerede ve tarayıcıda girişi tamamlayın; bitince bu panel kendiliğinden güncellenir."), NoticeKind.Info);
        }

        private async Task LogoutAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var executable = ClaudeCliLocator.Locate(_package.Options.ClaudeExecutablePath);
            if (executable == null)
            {
                return;
            }

            _session.Stop();
            await AuthStatus.LogoutAsync(executable, CurrentPolicy().BuildEnvironment());
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            AddNotice(L.T("Claude Code oturumu kapatıldı (terminal ve VS Code eklentisi için de geçerlidir)."), NoticeKind.Info);
            RequestAuthRefresh(force: true);
        }

        /// <summary>The CLI reported an authentication problem: re-check the sign-in state and offer to log in.</summary>
        private void HandleAuthProblem()
        {
            RequestAuthRefresh(force: true);
            ShowLoginCard(L.T("Claude Code oturumu açık değil ya da süresi dolmuş."));
        }

        private static UIElement Warning(string text, Brush brush)
        {
            var row = new DockPanel();
            var icon = Ui.Glyph(Theme.Glyph.Warning, 11, brush).WithMargin(0, 2, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(Ui.Text(text, Theme.TextKey, 0.95));
            return new Border
            {
                Child = row,
                Background = brush == Theme.Error ? Theme.ErrorSoft : Theme.AccentSoft,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 8, 0, 0),
            };
        }

        // ---------------------------------------------------------------- history

        private void OnHistoryClick(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (HistoryHost.Visibility == Visibility.Visible)
            {
                HideHistory();
                return;
            }

            RefreshWorkingDirectory();
            var cwd = _cwd;
            var current = _session.SessionId;
            HistoryHost.Visibility = Visibility.Visible;
            _historyView.ShowLoading();
            _historyView.FocusSearch();
            Fire(async () =>
            {
                var sessions = await Task.Run(() => cwd == null ? new List<SessionSummary>() : SessionHistory.List(cwd));
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _historyView.SetSessions(sessions, current);
            });
        }

        private void HideHistory()
        {
            HistoryHost.Visibility = Visibility.Collapsed;
            FocusInput();
        }

        /// <summary>Shows an earlier conversation and arranges for the next message to resume it.</summary>
        private async Task LoadSessionAsync(SessionSummary summary)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            HideHistory();
            if (_busy)
            {
                AddNotice(L.T("Claude çalışırken başka bir sohbete geçilemez; önce durdurun."), NoticeKind.Info);
                return;
            }

            var records = await Task.Run(() => SessionHistory.LoadTranscript(summary.FilePath));
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

            _session.PrepareResume(summary.Id, _cwd);
            _sessionPolicyKey = null;
            _announcedSessionId = summary.Id;
            _activeModel = null;
            Transcript.Children.Clear();
            _welcome = null;
            _welcomeStatus = null;
            _privacySummary = null;
            _streamingText = null;
            _toolCalls.Clear();
            _pendingCards.Clear();
            _turnTexts.Clear();

            AddNotice(L.F("Geçmiş sohbet · {0} · {1:g}", summary.Title, summary.Updated), NoticeKind.Info);
            const int MaxRecords = 400;
            int skip = Math.Max(0, records.Count - MaxRecords);
            if (skip > 0)
            {
                AddNotice(L.F("Daha eski {0} kayıt gösterilmiyor.", skip), NoticeKind.Muted);
            }

            _replaying = true;
            try
            {
                foreach (var record in records.Skip(skip))
                {
                    var prompt = SessionHistory.PromptText(record);
                    if (prompt != null)
                    {
                        AddUserMessage(prompt, new List<ContextItem>(), pastImages: SessionHistory.ImageCount(record));
                    }
                    else if (record.Str("type") == "assistant")
                    {
                        HandleAssistant(record);
                    }
                    else
                    {
                        HandleToolResults(record);
                    }
                }
            }
            finally
            {
                _replaying = false;
                FinishStreaming();
            }

            AddNotice(L.T("Kaldığı yerden devam etmek için yazmanız yeterli."), NoticeKind.Muted);
            _stickToBottom = true;
            TranscriptScroll.ScrollToEnd();
            RenderStatus();
        }

        // ---------------------------------------------------------------- "/" and "@" completion

        private void UpdateCompletion()
        {
            if (_suppressCompletion)
            {
                return;
            }

            var text = InputBox.Text;
            int caret = InputBox.CaretIndex;
            if (text.StartsWith("/", StringComparison.Ordinal) && caret > 0 && text.Substring(0, caret).IndexOfAny(new[] { ' ', '\n', '\r', '\t' }) < 0)
            {
                _completionStart = 0;
                _completion.Show(CommandItems(text.Substring(1, caret - 1)));
                return;
            }

            int start = caret;
            while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            {
                start--;
            }

            var token = text.Substring(start, caret - start);
            if (token.StartsWith("@", StringComparison.Ordinal))
            {
                _completionStart = start;
                var index = EnsureFileIndex();
                if (index != null)
                {
                    _completion.Show(index.Search(token.Substring(1), 40).Select(path =>
                    {
                        int slash = path.LastIndexOf('/');
                        return new CompletionItem
                        {
                            InsertText = "@" + path,
                            Label = slash >= 0 ? path.Substring(slash + 1) : path,
                            Detail = slash >= 0 ? path.Substring(0, slash) : string.Empty,
                            Glyph = Theme.Glyph.Document,
                        };
                    }).ToList());
                }

                return;
            }

            _completion.Close();
        }

        private List<CompletionItem> CommandItems(string query)
        {
            var items = new List<CompletionItem>
            {
                new CompletionItem { InsertText = "/clear", Label = "/clear", Detail = L.T("Yeni sohbet başlat"), Glyph = Theme.Glyph.Add },
            };

            foreach (var command in _commands?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var name = command.Str("name");
                if (string.IsNullOrEmpty(name) || HiddenCommands.Contains(name) || name == "clear")
                {
                    continue;
                }

                var hint = command.Str("argumentHint");
                items.Add(new CompletionItem
                {
                    InsertText = "/" + name,
                    Label = "/" + name + (string.IsNullOrEmpty(hint) ? string.Empty : " " + hint),
                    Detail = command.Str("description"),
                    Glyph = Theme.Glyph.Lightning,
                });
            }

            return items
                .Where(i => query.Length == 0 || i.InsertText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(i => i.InsertText.StartsWith("/" + query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(i => i.InsertText.Length)
                .Take(60)
                .ToList();
        }

        private void AcceptCompletion(CompletionItem item)
        {
            var text = InputBox.Text;
            int caret = Math.Min(InputBox.CaretIndex, text.Length);
            int start = Math.Min(_completionStart, caret);
            var insert = item.InsertText + " ";
            _suppressCompletion = true;
            InputBox.Text = text.Substring(0, start) + insert + text.Substring(caret);
            InputBox.CaretIndex = start + insert.Length;
            _suppressCompletion = false;
            FocusInput();
        }

        /// <summary>Returns the file list for "@" mentions, building it in the background on first use.</summary>
        private FileIndex EnsureFileIndex()
        {
            var root = _cwd;
            if (root == null)
            {
                return null;
            }

            if (_fileIndex != null && SamePath(_fileIndex.Root, root))
            {
                return _fileIndex;
            }

            if (!_fileIndexBuilding)
            {
                _fileIndexBuilding = true;
                var policy = CurrentPolicy();
                Fire(async () =>
                {
                    var index = await Task.Run(() => FileIndex.Build(root, policy.IsProtected));
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _fileIndex = index;
                    _fileIndexBuilding = false;
                    if (InputBox.IsKeyboardFocused)
                    {
                        UpdateCompletion();
                    }
                });
            }

            return null;
        }

        /// <summary>Full paths of existing files referenced as "@path" in a message.</summary>
        private List<string> MentionedFiles(string text)
        {
            var files = new List<string>();
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"(?<=^|\s)@(?:""(?<p>[^""]+)""|(?<p>\S+))"))
            {
                var path = ResolveExistingFile(match.Groups["p"].Value.TrimEnd('.', ',', ';', ':', ')'), out _);
                if (path != null)
                {
                    files.Add(path);
                }
            }

            return files;
        }

        // ---------------------------------------------------------------- context window

        private void RequestContextUsage()
        {
            if (!_session.IsRunning)
            {
                ContextUsageButton.Visibility = Visibility.Collapsed;
                return;
            }

            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                JObject usage;
                try
                {
                    usage = await _session.GetContextUsageAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    return;
                }

                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                var percent = usage.Num("percentage") ?? 0;
                var total = usage.Num("totalTokens") ?? 0;
                var max = usage.Num("maxTokens") ?? 0;
                ContextUsageText.Text = L.F("%{0}", Math.Round(percent));
                Brush brush = percent >= 90 ? Theme.Error : percent >= 75 ? Theme.Warning : null;
                foreach (var block in new[] { ContextUsageText, ContextUsageGlyph })
                {
                    if (brush != null)
                    {
                        block.Foreground = brush;
                    }
                    else
                    {
                        block.SetResourceReference(TextBlock.ForegroundProperty, Theme.MutedTextKey);
                    }
                }

                var categories = usage.Arr("categories")?.OfType<JObject>()
                    .Where(c => c.Str("kind") == "used" && (c.Num("tokens") ?? 0) > 0)
                    .Select(c => $"• {c.Str("name")}: {c.Num("tokens"):N0}") ?? Enumerable.Empty<string>();
                ContextUsageButton.ToolTip = L.F("Bağlam penceresi: {0:N0} / {1:N0} token (%{2})\n", total, max, Math.Round(percent))
                    + string.Join("\n", categories)
                    + L.T("\n\nTıklayın: konuşmayı özetleyip yer açın (/compact).");
                ContextUsageButton.Visibility = Visibility.Visible;
            }).FileAndForget("ClaudeCodeVS/context");
        }

        private void OnContextUsageClick(object sender, RoutedEventArgs e)
        {
            if (!_busy)
            {
                Fire(() => SendAsync("/compact"));
            }
        }

        // ---------------------------------------------------------------- drag & drop images

        private void OnInputDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(ImageAttachment.IsImageFile))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void OnInputDrop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files))
            {
                return;
            }

            e.Handled = true;
            foreach (var file in files.Where(ImageAttachment.IsImageFile))
            {
                try
                {
                    AttachImage(ImageAttachment.FromFile(file));
                }
                catch (Exception ex) when (ex is IOException || ex is NotSupportedException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    AddNotice(L.F("Görsel eklenemedi: {0} ({1})", Path.GetFileName(file), ex.Message), NoticeKind.Error);
                }
            }
        }

        private void RefreshActiveContext()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed)
            {
                return;
            }

            try
            {
                _activeContext = _ide.GetActiveEditorContext();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                _activeContext = null;
            }

            if (_activeContext == null)
            {
                _contextLabel.Text = L.T("Dosya yok");
                ContextToggle.ToolTip = L.T("Açık bir düzenleyici olduğunda aktif dosya ve seçili satırlar mesajla birlikte gönderilir.");
            }
            else if (CurrentPolicy().IsProtected(_activeContext.FilePath))
            {
                _contextLabel.Text = _activeContext.FileName + L.T(" · korumalı");
                ContextToggle.ToolTip = _activeContext.FilePath + L.T("\nBu dosya korunan dosya desenlerine uyuyor; gizlilik ayarları gereği Claude'a gönderilmez.");
            }
            else
            {
                _contextLabel.Text = _activeContext.DisplayName;
                ContextToggle.ToolTip = _activeContext.FilePath
                    + (_activeContext.HasSelection ? L.F("\nSeçili satırlar: {0}-{1}", _activeContext.StartLine, _activeContext.EndLine) : string.Empty)
                    + L.T("\nTıklayarak mesaja eklemeyi açıp kapatın.");
            }

            UpdateContextToggleLook();
        }

        private void UpdateContextToggleLook()
        {
            bool included = ContextToggle.IsChecked == true;
            _contextLabel.TextDecorations = !included && _activeContext != null ? TextDecorations.Strikethrough : null;
            ContextToggle.Opacity = _activeContext == null ? 0.65 : 1;
        }

        private void SaveIncludeActiveFile(bool value)
        {
            _package.Options.IncludeActiveFile = value;
            _package.Options.SaveSettingsToStorage();
            UpdateContextToggleLook();
        }

        // ---------------------------------------------------------------- status

        private void SetStatus(string text)
        {
            _statusText = text;
            RenderStatus();
        }

        private void SetBusy(bool busy, string status = null)
        {
            _busy = busy;
            SendButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                _statusText = status ?? L.T("Claude çalışıyor…");
                _thinkingGlyph.Text = Theme.SparkleFrames[0];
                if (!Transcript.Children.Contains(_thinking))
                {
                    RemoveWelcome();
                    Transcript.Children.Add(_thinking);
                }

                _spinnerTimer.Start();
            }
            else
            {
                _spinnerTimer.Stop();
                Transcript.Children.Remove(_thinking);
                _interruptRequested = false;
                _stoppedByUser = false;
            }

            RenderStatus();
        }

        private void RenderStatus()
        {
            _thinkingText.Text = _statusText;
            if (_busy)
            {
                StatusText.Text = L.T("Claude çalışıyor · durdurmak için Esc");
            }
            else
            {
                var parts = new List<string> { _session.IsRunning ? L.T("Hazır") : L.T("Oturum başlatılmadı") };
                if (!string.IsNullOrEmpty(_activeModel))
                {
                    parts.Add(_activeModel);
                }

                if (!string.IsNullOrEmpty(_cwd))
                {
                    var name = Path.GetFileName(_cwd.TrimEnd('\\'));
                    parts.Add(string.IsNullOrEmpty(name) ? _cwd : name);
                }

                StatusText.Text = string.Join(" · ", parts);
            }

            StatusText.ToolTip = _cwd;
        }

        // ---------------------------------------------------------------- toolbar

        private void PopulateModeCombo(PermissionModeOption selected)
        {
            _suppressComboEvents = true;
            ModeCombo.Items.Clear();
            bool privacy = _package.Options.PrivacyMode;
            foreach (var mode in PermissionModes.All)
            {
                // Privacy mode keeps a human approving every command: no auto mode, no bypass.
                if (privacy && (mode == PermissionModeOption.Auto || mode == PermissionModeOption.BypassPermissions))
                {
                    continue;
                }

                ModeCombo.Items.Add(new ComboBoxItem { Content = PermissionModes.DisplayName(mode), Tag = PermissionModes.ToCli(mode) });
            }

            if (!SelectByTag(ModeCombo, PermissionModes.ToCli(selected)))
            {
                SelectByTag(ModeCombo, PermissionModes.ToCli(PermissionModeOption.Default));
            }

            _suppressComboEvents = false;
        }

        private void PopulateModelCombo(JArray models, string selected)
        {
            _suppressComboEvents = true;
            ModelCombo.Items.Clear();
            if (models != null && models.Count > 0)
            {
                foreach (var model in models.OfType<JObject>())
                {
                    if (model.Str("value") is string value)
                    {
                        ModelCombo.Items.Add(new ComboBoxItem { Content = model.Str("displayName") ?? value, Tag = value, ToolTip = model.Str("description") });
                    }
                }
            }
            else
            {
                foreach (var (label, value) in new[] { (L.T("Varsayılan"), "default"), ("Opus", "opus"), ("Sonnet", "sonnet"), ("Haiku", "haiku") })
                {
                    ModelCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
                }
            }

            if (!SelectByTag(ModelCombo, selected))
            {
                ModelCombo.Items.Add(new ComboBoxItem { Content = selected, Tag = selected });
                SelectByTag(ModelCombo, selected);
            }

            _suppressComboEvents = false;
        }

        private void SyncModeCombo(string mode)
        {
            if (string.IsNullOrEmpty(mode))
            {
                return;
            }

            _suppressComboEvents = true;
            SelectByTag(ModeCombo, mode);
            _suppressComboEvents = false;
        }

        private static bool SelectByTag(ComboBox combo, string tag)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                return false;
            }

            combo.SelectedItem = item;
            return true;
        }

        private string SelectedModelValue() => (ModelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "default";

        private string SelectedModeValue() => (ModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "default";

        private void OnModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressComboEvents)
            {
                return;
            }

            var mode = SelectedModeValue();
            _package.Options.PermissionMode = PermissionModes.FromCli(mode);
            _package.Options.SaveSettingsToStorage();
            if (!_session.IsRunning)
            {
                return;
            }

            Fire(async () =>
            {
                try
                {
                    await _session.SetPermissionModeAsync(mode);
                }
                catch (Exception ex)
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AddNotice(L.F("İzin modu değiştirilemedi: {0}\nBu mod yeni oturumda kullanılacak.", ex.Message), NoticeKind.Error);
                }
            });
        }

        private void OnModelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressComboEvents)
            {
                return;
            }

            var model = SelectedModelValue();
            _package.Options.Model = model == "default" ? string.Empty : model;
            _package.Options.SaveSettingsToStorage();
            if (!_session.IsRunning)
            {
                return;
            }

            Fire(async () =>
            {
                try
                {
                    var response = await _session.SetModelAsync(model);
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _activeModel = response.Str("model") ?? model;
                    AddNotice(L.T("Model değiştirildi: ") + _activeModel, NoticeKind.Muted);
                    RenderStatus();
                }
                catch (Exception ex)
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AddNotice(L.F("Model değiştirilemedi: {0}\nBu model yeni oturumda kullanılacak.", ex.Message), NoticeKind.Error);
                }
            });
        }

        private void OnNewSessionClick(object sender, RoutedEventArgs e) => StartNewSession();

        private void OnSendClick(object sender, RoutedEventArgs e) => SubmitInput();

        private void OnStopClick(object sender, RoutedEventArgs e) => Fire(StopAsync);

        // ---------------------------------------------------------------- input & scrolling

        private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_completion.HandleKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage())
            {
                e.Handled = true;
                try
                {
                    AttachImage(ImageAttachment.FromBitmap(Clipboard.GetImage(), L.T("Ekran görüntüsü")));
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is NotSupportedException || ex is ArgumentException)
                {
                    AddNotice(L.T("Panodaki görsel eklenemedi: ") + ex.Message, NoticeKind.Error);
                }

                return;
            }

            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                SubmitInput();
            }
            else if (e.Key == Key.Escape && _busy)
            {
                e.Handled = true;
                Fire(StopAsync);
            }
            else if (e.Key == Key.Up && _history.Count > 0 && (InputBox.Text.Length == 0 || _historyIndex >= 0)
                     && InputBox.GetLineIndexFromCharacterIndex(InputBox.CaretIndex) <= 0)
            {
                e.Handled = true;
                _historyIndex = _historyIndex < 0 ? _history.Count - 1 : Math.Max(0, _historyIndex - 1);
                InputBox.Text = _history[_historyIndex];
                InputBox.CaretIndex = InputBox.Text.Length;
            }
            else if (e.Key == Key.Down && _historyIndex >= 0)
            {
                e.Handled = true;
                _historyIndex++;
                if (_historyIndex >= _history.Count)
                {
                    _historyIndex = -1;
                    InputBox.Clear();
                }
                else
                {
                    InputBox.Text = _history[_historyIndex];
                    InputBox.CaretIndex = InputBox.Text.Length;
                }
            }
        }

        private void OnInputTextChanged(object sender, TextChangedEventArgs e)
        {
            InputPlaceholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateCompletion();
        }

        private void OnTranscriptMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Rich text boxes and code blocks swallow the wheel; always scroll the transcript instead.
            TranscriptScroll.ScrollToVerticalOffset(TranscriptScroll.VerticalOffset - (e.Delta * 0.5));
            e.Handled = true;
        }

        private void OnTranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            {
                _stickToBottom = TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - 24;
            }
            else if (_stickToBottom)
            {
                TranscriptScroll.ScrollToEnd();
            }
        }

        // ---------------------------------------------------------------- IChatHost

        void IChatHost.OpenLink(string target)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            if (Regex.IsMatch(target, @"^(https?|mailto):", RegexOptions.IgnoreCase))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AddNotice(L.T("Bağlantı açılamadı: ") + ex.Message, NoticeKind.Error);
                }

                return;
            }

            var path = ResolveExistingFile(target, out var line);
            if (path != null)
            {
                OpenFile(path, line);
            }
            else
            {
                AddNotice(L.T("Dosya bulunamadı: ") + target, NoticeKind.Info);
            }
        }

        void IChatHost.OpenFile(string path, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OpenFile(path, line);
        }

        void IChatHost.InsertCode(string code)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_ide.InsertAtCaret(code))
            {
                AddNotice(L.T("Kodu eklemek için önce bir düzenleyici penceresinde imleci konumlandırın."), NoticeKind.Info);
            }
        }

        string IChatHost.ResolveExistingFile(string pathOrLink, out int line) => ResolveExistingFile(pathOrLink, out line);

        string IChatHost.ToDisplayPath(string path)
        {
            var baseDirectory = _session.WorkingDirectory ?? _cwd;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(baseDirectory))
            {
                return path;
            }

            var prefix = baseDirectory.TrimEnd('\\', '/') + "\\";
            var normalized = path.Replace('/', '\\');
            return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalized.Substring(prefix.Length) : path;
        }

        private void OpenFile(string path, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _ide.OpenFile(path, line);
            }
            catch (Exception ex)
            {
                AddNotice(L.F("Dosya açılamadı: {0} ({1})", path, ex.Message), NoticeKind.Error);
            }
        }

        private string ResolveExistingFile(string pathOrLink, out int line)
        {
            if (!FileTarget.TryParse(pathOrLink, out var path, out line))
            {
                return null;
            }

            try
            {
                var baseDirectory = _session.WorkingDirectory ?? _cwd;
                var full = Path.IsPathRooted(path)
                    ? path
                    : baseDirectory != null ? Path.GetFullPath(Path.Combine(baseDirectory, path)) : null;
                return full != null && File.Exists(full) ? full : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- misc

        private void Fire(Func<Task> work)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    AddNotice(ex.Message, NoticeKind.Error);
                }
            }).FileAndForget("ClaudeCodeVS/ui");
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(a?.TrimEnd('\\', '/'), b?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeAuthError(string text)
        {
            return !string.IsNullOrEmpty(text)
                && (text.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("Invalid API key", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("authentication", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
