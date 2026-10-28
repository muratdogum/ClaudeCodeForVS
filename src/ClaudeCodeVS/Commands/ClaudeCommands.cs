using System;
using System.ComponentModel.Design;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS.Commands
{
    internal static class ClaudeCommands
    {
        private static ClaudeCodePackage _package;

        public static async Task InitializeAsync(ClaudeCodePackage package)
        {
            _package = package;
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Assumes.Present(commandService);

            commandService.AddCommand(new MenuCommand((s, e) => Run(OpenAsync), new CommandID(PackageGuids.CommandSet, PackageIds.OpenClaudeCommand)));
            commandService.AddCommand(new MenuCommand((s, e) => Run(AddSelectionAsync), new CommandID(PackageGuids.CommandSet, PackageIds.AddSelectionCommand)));
            commandService.AddCommand(new MenuCommand((s, e) => Run(ExplainSelectionAsync), new CommandID(PackageGuids.CommandSet, PackageIds.ExplainSelectionCommand)));
        }

        private static async Task OpenAsync()
        {
            var chat = await _package.ShowChatAsync();
            chat?.FocusInput();
        }

        private static async Task AddSelectionAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var context = _package.Ide.GetActiveEditorContext();
            var chat = await _package.ShowChatAsync();
            if (chat == null)
            {
                return;
            }

            if (context != null)
            {
                chat.AttachContext(context);
            }

            chat.FocusInput();
        }

        private static async Task ExplainSelectionAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var context = _package.Ide.GetActiveEditorContext();
            var chat = await _package.ShowChatAsync();
            chat?.SendPrompt(context != null && context.HasSelection ? "Seçili kodu açıkla." : "Bu dosyayı açıkla.", context);
        }

        private static void Run(Func<Task> action)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    VsShellUtilities.ShowMessageBox(_package, ex.Message, "Claude Code",
                        OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            }).FileAndForget("ClaudeCodeVS/command");
        }
    }
}
