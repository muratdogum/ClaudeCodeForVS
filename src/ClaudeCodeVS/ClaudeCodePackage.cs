using System;
using System.Runtime.InteropServices;
using System.Threading;
using ClaudeCodeVS.Commands;
using ClaudeCodeVS.Ide;
using ClaudeCodeVS.Options;
using ClaudeCodeVS.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Codecraftopia Agent for Claude Code", "Claude Code CLI'ı Visual Studio içinde kullanın (resmi olmayan).", "0.9.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ClaudeToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer)]
    [ProvideOptionPage(typeof(GeneralOptionsPage), "Claude Code", "Genel", 0, 0, true)]
    [Guid(PackageGuids.PackageString)]
    public sealed class ClaudeCodePackage : AsyncPackage
    {
        internal IdeService Ide { get; private set; }

        internal GeneralOptionsPage Options => (GeneralOptionsPage)GetDialogPage(typeof(GeneralOptionsPage));

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Ide = await IdeService.CreateAsync(this);
            await ClaudeCommands.InitializeAsync(this);
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
