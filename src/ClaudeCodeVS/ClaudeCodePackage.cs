using System;
using System.Runtime.InteropServices;
using System.Threading;
using ClaudeCodeVS.Commands;
using ClaudeCodeVS.Ide;
using ClaudeCodeVS.Ide.Bridge;
using ClaudeCodeVS.Localization;
using ClaudeCodeVS.Options;
using ClaudeCodeVS.ToolWindows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Codecraftopia Agent for Claude Code", "Use the Claude Code CLI inside Visual Studio (unofficial).", "0.9.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(ClaudeToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer)]
    [ProvideOptionPage(typeof(GeneralOptionsPage), "Claude Code", "General", 0, 0, true)]
    [Guid(PackageGuids.PackageString)]
    public sealed class ClaudeCodePackage : AsyncPackage
    {
        private IDisposable _bridgeSolutionWatch;

        internal IdeService Ide { get; private set; }

        /// <summary>The connection terminal Claude Code sessions use (/ide); null when turned off in the options.</summary>
        internal IdeBridge Bridge { get; private set; }

        internal GeneralOptionsPage Options => (GeneralOptionsPage)GetDialogPage(typeof(GeneralOptionsPage));

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            L.Initialize(Options.Language.Code());
            Ide = await IdeService.CreateAsync(this);
            await ClaudeCommands.InitializeAsync(this);
            GeneralOptionsPage.Applied += OnOptionsApplied;
            await UpdateBridgeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GeneralOptionsPage.Applied -= OnOptionsApplied;
                StopBridge();
            }

            base.Dispose(disposing);
        }

        private void OnOptionsApplied(GeneralOptionsPage page) =>
            JoinableTaskFactory.RunAsync(UpdateBridgeAsync).FileAndForget("ClaudeCodeVS/bridge/options");

        private async Task UpdateBridgeAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!Options.IdeConnection)
            {
                StopBridge();
                return;
            }

            if (Bridge != null)
            {
                return;
            }

            try
            {
                Bridge = await IdeBridge.StartAsync(this, Ide.GetOpenFolder(), path => Options.CreatePrivacyPolicy().IsProtected(path));
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                System.Diagnostics.Debug.WriteLine("IDE bridge: " + ex.Message);
                return;
            }

            Bridge.ClientsChanged += OnBridgeClientsChanged;
            _bridgeSolutionWatch = Ide.WatchSolutionChanges(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Bridge?.SetWorkspace(Ide.GetOpenFolder());
            });
        }

        private void StopBridge()
        {
            if (ThreadHelper.CheckAccess())
            {
#pragma warning disable VSTHRD010 // guarded by CheckAccess above
                _bridgeSolutionWatch?.Dispose();
#pragma warning restore VSTHRD010
            }

            _bridgeSolutionWatch = null;
            Bridge?.Dispose();
            Bridge = null;
        }

        private void OnBridgeClientsChanged(int count)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (GetService(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
            {
                statusBar.SetText(count > 0
                    ? L.F("Terminaldeki Claude Code bu Visual Studio'ya bağlı ({0} oturum).", count)
                    : L.T("Terminaldeki Claude Code ile bağlantı kapandı."));
            }
        }

        public override IVsAsyncToolWindowFactory GetAsyncToolWindowFactory(Guid toolWindowType)
        {
            return toolWindowType == typeof(ClaudeToolWindow).GUID ? this : null;
        }

        protected override string GetToolWindowTitle(Type toolWindowType, int id)
        {
            return toolWindowType == typeof(ClaudeToolWindow) ? ClaudeToolWindow.Title : base.GetToolWindowTitle(toolWindowType, id);
        }

        protected override System.Threading.Tasks.Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken)
        {
            // The pane receives the package as its constructor argument.
            return System.Threading.Tasks.Task.FromResult<object>(this);
        }

        internal async System.Threading.Tasks.Task<ClaudeChatControl> ShowChatAsync()
        {
            var pane = await ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, true, DisposalToken);
            return (pane as ClaudeToolWindow)?.Chat;
        }
    }
}
