using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Linq;
using ClaudeCodeVS.Cli;
using ClaudeCodeVS.Ide.Bridge;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Task = System.Threading.Tasks.Task;
using ClaudeCodeVS.Localization;

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

            AddCommand(commandService, PackageIds.OpenClaudeCommand, OpenAsync, null);
            AddCommand(commandService, PackageIds.AddSelectionCommand, AddSelectionAsync, "Seçimi Claude'a Ekle");
            AddCommand(commandService, PackageIds.ExplainSelectionCommand, ExplainSelectionAsync, "Claude ile Açıkla");
            AddCommand(commandService, PackageIds.OpenTerminalCommand, OpenTerminalAsync, "Claude Code'u Terminalde Aç");
        }

        private static void AddCommand(OleMenuCommandService service, int id, Func<Task> action, string text)
        {
            var command = new OleMenuCommand((s, e) => Run(action), new CommandID(PackageGuids.CommandSet, id));
            if (text != null)
            {
                // The .vsct text is English; TextChanges lets the menu show the UI language.
                command.BeforeQueryStatus += (s, e) => command.Text = L.T(text);
            }

            service.AddCommand(command);
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
            chat?.SendPrompt(context != null && context.HasSelection ? L.T("Seçili kodu açıkla.") : L.T("Bu dosyayı açıkla."), context);
        }

        /// <summary>
        /// Starts the Claude Code CLI in a console in the solution folder, with the same privacy rules as the panel and
        /// connected to this Visual Studio (selection, Error List, diffs).
        /// </summary>
        private static async Task OpenTerminalAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var options = _package.Options;
            var executable = ClaudeCliLocator.Locate(options.ClaudeExecutablePath)
                ?? throw new InvalidOperationException(ClaudeCliLocator.NotFoundMessage(options.ClaudeExecutablePath));
            var policy = options.CreatePrivacyPolicy();

            var args = new List<string>();
            var settings = policy.BuildSettings();
            if (settings != null)
            {
                args.Add("--settings");
                args.Add(settings.ToString(Formatting.None));
            }

            if (policy.Enabled && !policy.AllowMcpServers)
            {
                args.Add("--strict-mcp-config");
            }

            if (!string.IsNullOrWhiteSpace(options.Model))
            {
                args.Add("--model");
                args.Add(options.Model.Trim());
            }

            var commandLine = string.Join(" ", args.Select(ClaudeProcess.QuoteArgument));
            if (!string.IsNullOrWhiteSpace(options.AdditionalArguments))
            {
                commandLine += " " + options.AdditionalArguments.Trim();
            }

            var startInfo = new ProcessStartInfo(executable, commandLine)
            {
                WorkingDirectory = _package.Ide.GetWorkingDirectory(),
                UseShellExecute = false,
            };
            startInfo.EnvironmentVariables.Remove("CLAUDECODE");
            foreach (var pair in policy.BuildEnvironment())
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }

            if (_package.Bridge != null)
            {
                startInfo.EnvironmentVariables[IdeBridge.PortVariable] = _package.Bridge.Port.ToString();
                startInfo.EnvironmentVariables[IdeBridge.IntegrationVariable] = "true";
            }

            Process.Start(startInfo)?.Dispose();
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
