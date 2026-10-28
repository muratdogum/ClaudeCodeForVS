using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ClaudeCodeVS.Cli;
using ClaudeCodeVS.Localization;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Ide.Bridge
{
    /// <summary>
    /// openDiff over the IDE connection: the proposed contents open in the Visual Studio diff viewer with an
    /// Accept / Reject bar, and the call returns when the person decides (or the CLI closes the tab itself).
    /// </summary>
    internal sealed class DiffSessions
    {
        public const string FileSaved = "FILE_SAVED";
        public const string DiffRejected = "DIFF_REJECTED";
        public const string TabClosed = "TAB_CLOSED";

        private readonly AsyncPackage _package;
        private readonly Dictionary<string, Session> _open = new Dictionary<string, Session>(StringComparer.Ordinal);

        public DiffSessions(AsyncPackage package)
        {
            _package = package;
        }

        public async Task<JObject> OpenAsync(JObject args, object owner)
        {
            var newPath = args.Str("new_file_path") ?? args.Str("old_file_path");
            var oldPath = args.Str("old_file_path") ?? newPath;
            if (string.IsNullOrEmpty(newPath))
            {
                throw new McpException(McpException.InvalidParams, "new_file_path is required");
            }

            var contents = args.Str("new_file_contents") ?? string.Empty;
            var tab = args.Str("tab_name") ?? Path.GetFileName(newPath);
            var name = Path.GetFileName(newPath);

            var folder = Path.Combine(DiffPreview.TempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
            var left = Path.Combine(folder, "a", name);
            var right = Path.Combine(folder, "b", name);
            Directory.CreateDirectory(Path.GetDirectoryName(left));
            Directory.CreateDirectory(Path.GetDirectoryName(right));
            File.WriteAllText(left, File.Exists(oldPath) ? File.ReadAllText(oldPath) : string.Empty, new UTF8Encoding(false));
            File.WriteAllText(right, contents, new UTF8Encoding(false));

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_open.TryGetValue(tab, out var previous))
            {
                previous.Finish(IdeTools.Text(TabClosed), close: true);
            }

            if (!(await _package.GetServiceAsync(typeof(SVsDifferenceService)) is IVsDifferenceService diff))
            {
                throw new McpException(McpException.InternalError, "The Visual Studio diff service is not available.");
            }

            var frame = diff.OpenComparisonWindow2(
                left,
                right,
                tab,
                newPath,
                L.F("{0} (mevcut)", name),
                L.F("{0} (Claude önerisi)", name),
                null,
                null,
                (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary);

            var session = new Session(this, tab, frame, right, owner);
            _open[tab] = session;
            await session.AttachAsync(_package);
            return await session.Result;
        }

        /// <summary>Closes the diff the CLI no longer needs (the person answered in the terminal); the pending call gets TAB_CLOSED.</summary>
        public async Task<bool> CloseAsync(string tab)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (tab == null || !_open.TryGetValue(tab, out var session))
            {
                return false;
            }

            session.Finish(IdeTools.Text(TabClosed), close: true);
            return true;
        }

        public async Task<int> CloseAllAsync(object owner = null)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var sessions = _open.Values.Where(s => owner == null || s.Owner == owner).ToList();
            foreach (var session in sessions)
            {
                session.Finish(IdeTools.Text(TabClosed), close: true);
            }

            return sessions.Count;
        }

        private sealed class Session : IVsInfoBarUIEvents, IVsWindowFrameNotify
        {
            private readonly DiffSessions _owner;
            private readonly string _tab;
            private readonly IVsWindowFrame _frame;
            private readonly string _rightFile;
            private readonly TaskCompletionSource<JObject> _result = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            private IVsInfoBarUIElement _bar;
            private uint _barCookie;
            private uint _frameCookie;

            public Session(DiffSessions owner, string tab, IVsWindowFrame frame, string rightFile, object client)
            {
                _owner = owner;
                _tab = tab;
                _frame = frame;
                _rightFile = rightFile;
                Owner = client;
            }

            public object Owner { get; }

            public Task<JObject> Result => _result.Task;

            public async Task AttachAsync(AsyncPackage package)
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_frame is IVsWindowFrame2 frame2)
                {
                    frame2.Advise(this, out _frameCookie);
                }

                if (await package.GetServiceAsync(typeof(SVsInfoBarUIFactory)) is IVsInfoBarUIFactory factory
                    && ErrorHandler.Succeeded(_frame.GetProperty((int)__VSFPROPID7.VSFPROPID_InfoBarHost, out object hostObject))
                    && hostObject is IVsInfoBarHost host)
                {
                    var model = new InfoBarModel(
                        new[] { new InfoBarTextSpan(L.T("Claude Code bu değişikliği öneriyor. Kabul etmeden önce sağ taraftaki metni düzenleyebilirsiniz.")) },
                        new IVsInfoBarActionItem[] { new InfoBarButton(L.T("Kabul et"), true), new InfoBarButton(L.T("Reddet"), false) },
                        KnownMonikers.StatusInformation,
                        isCloseButtonVisible: false);
                    _bar = factory.CreateInfoBar(model);
                    _bar.Advise(this, out _barCookie);
                    host.AddInfoBar(_bar);
                }

                _frame.Show();
            }

            public void Finish(JObject result, bool close)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!_result.TrySetResult(result))
                {
                    return;
                }

                _owner._open.Remove(_tab);
                if (_bar != null)
                {
                    _bar.Unadvise(_barCookie);
                    _bar = null;
                }

                if (_frame is IVsWindowFrame2 frame2 && _frameCookie != 0)
                {
                    frame2.Unadvise(_frameCookie);
                    _frameCookie = 0;
                }

                if (close)
                {
                    _frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                }
            }

            public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (actionItem.ActionContext is bool accepted && accepted)
                {
                    Finish(IdeTools.Text(FileSaved, ReadProposal()), close: true);
                }
                else
                {
                    Finish(IdeTools.Text(DiffRejected, _tab), close: true);
                }
            }

            public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
            {
            }

            // Closing the diff window without choosing counts as a rejection, as in VS Code.
            public int OnShow(int fShow)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinClosed)
                {
                    Finish(IdeTools.Text(DiffRejected, _tab), close: false);
                }

                return VSConstants.S_OK;
            }

            public int OnMove() => VSConstants.S_OK;

            public int OnSize() => VSConstants.S_OK;

            public int OnDockableChange(int fDockable) => VSConstants.S_OK;

            /// <summary>The right side as the person left it, including unsaved edits in the diff view.</summary>
            private string ReadProposal()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable table
                    && ErrorHandler.Succeeded(table.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, _rightFile, out _, out _, out IntPtr data, out _))
                    && data != IntPtr.Zero)
                {
                    try
                    {
                        if (Marshal.GetObjectForIUnknown(data) is IVsTextLines lines)
                        {
                            return IdeTools.BufferText(lines);
                        }
                    }
                    finally
                    {
                        Marshal.Release(data);
                    }
                }

                return File.ReadAllText(_rightFile);
            }
        }
    }
}
