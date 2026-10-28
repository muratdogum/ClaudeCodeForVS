using System;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS.Ide
{
    /// <summary>Everything the chat needs from Visual Studio. All members must be called on the UI thread.</summary>
    internal sealed class IdeService
    {
        private readonly AsyncPackage _package;
        private readonly DTE2 _dte;
        private readonly IVsSolution _solution;

        private IdeService(AsyncPackage package, DTE2 dte, IVsSolution solution)
        {
            _package = package;
            _dte = dte;
            _solution = solution;
        }

        public static async System.Threading.Tasks.Task<IdeService> CreateAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
            var solution = await package.GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            return new IdeService(package, dte, solution);
        }

        /// <summary>The solution (or opened folder) directory; falls back to the active file's folder, then the user profile.</summary>
        public string GetWorkingDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var folder = GetOpenFolder();
            if (folder != null)
            {
                return folder;
            }

            var activeFile = GetActiveEditorContext()?.FilePath;
            if (!string.IsNullOrEmpty(activeFile))
            {
                return NormalizeDirectory(Path.GetDirectoryName(activeFile));
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>The open solution's directory or the opened folder; null when neither is open.</summary>
        public string GetOpenFolder()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // DTE reports the .sln path, or in "Open Folder" mode the opened folder itself.
            // (IVsSolution's directory is not the opened folder in that mode, so it is only a fallback.)
            try
            {
                var opened = _dte?.Solution?.FullName;
                if (!string.IsNullOrEmpty(opened))
                {
                    if (Directory.Exists(opened))
                    {
                        return NormalizeDirectory(opened);
                    }

                    var folder = Path.GetDirectoryName(opened);
                    if (Directory.Exists(folder))
                    {
                        return NormalizeDirectory(folder);
                    }
                }
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException)
            {
            }

            if (_solution != null
                && ErrorHandler.Succeeded(_solution.GetSolutionInfo(out var directory, out _, out _))
                && !string.IsNullOrEmpty(directory)
                && Directory.Exists(directory))
            {
                return NormalizeDirectory(directory);
            }

            return null;
        }

        public ContextItem GetActiveEditorContext()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var document = _dte?.ActiveDocument;
                if (document == null
                    || string.IsNullOrEmpty(document.FullName)
                    || !File.Exists(document.FullName)
                    || document.FullName.StartsWith(DiffPreview.TempRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var item = new ContextItem { FilePath = document.FullName };
                if (document.Selection is TextSelection selection && !selection.IsEmpty)
                {
                    int start = selection.TopPoint.Line;
                    int end = selection.BottomPoint.Line;
                    if (end > start && selection.BottomPoint.LineCharOffset == 1)
                    {
                        end--; // a selection ending at the start of a line does not include that line
                    }

                    item.StartLine = start;
                    item.EndLine = end;
                    item.SelectedText = selection.Text;
                }

                return item;
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>Saves modified documents that already exist on disk (never prompts for new files).</summary>
        public void SaveOpenDocuments()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_dte == null)
            {
                return;
            }

            foreach (Document document in _dte.Documents)
            {
                try
                {
                    if (!document.Saved && File.Exists(document.FullName))
                    {
                        document.Save();
                    }
                }
                catch (Exception ex) when (ex is COMException || ex is ArgumentException)
                {
                }
            }
        }

        public void OpenFile(string path, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(_package, path, Guid.Empty, out _, out _, out IVsWindowFrame frame, out IVsTextView view);
            frame?.Show();
            if (line > 0 && view != null)
            {
                view.SetCaretPos(line - 1, 0);
                view.CenterLines(line - 1, 1);
            }
        }

        public bool InsertAtCaret(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var document = _dte?.ActiveDocument;
                if (document?.Selection is TextSelection selection)
                {
                    var normalized = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                    selection.Insert(normalized, (int)vsInsertFlags.vsInsertFlagsCollapseToEnd);
                    document.Activate();
                    return true;
                }
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException)
            {
            }

            return false;
        }

        public WindowEvents GetWindowEvents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _dte?.Events?.WindowEvents;
        }

        /// <summary>Calls <paramref name="changed"/> when a solution or folder is opened or closed. Dispose to stop.</summary>
        public IDisposable WatchSolutionChanges(Action changed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_solution == null || ErrorHandler.Failed(_solution.AdviseSolutionEvents(new SolutionChangeSink(changed), out uint cookie)))
            {
                return null;
            }

            return new SolutionAdvice(_solution, cookie);
        }

        private static string NormalizeDirectory(string directory)
        {
            var trimmed = directory.TrimEnd('\\', '/');
            return trimmed.EndsWith(":", StringComparison.Ordinal) ? trimmed + "\\" : trimmed;
        }

        private sealed class SolutionAdvice : IDisposable
        {
            private readonly IVsSolution _solution;
            private uint _cookie;

            public SolutionAdvice(IVsSolution solution, uint cookie)
            {
                _solution = solution;
                _cookie = cookie;
            }

            public void Dispose()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_cookie != 0)
                {
                    _solution.UnadviseSolutionEvents(_cookie);
                    _cookie = 0;
                }
            }
        }

        /// <summary>IVsSolutionEvents7 adds the "Open Folder" notifications that DTE's SolutionEvents do not raise.</summary>
        private sealed class SolutionChangeSink : IVsSolutionEvents, IVsSolutionEvents7
        {
            private readonly Action _changed;

            public SolutionChangeSink(Action changed)
            {
                _changed = changed;
            }

            public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution) => Notify();

            public int OnAfterCloseSolution(object pUnkReserved) => Notify();

            public void OnAfterOpenFolder(string folderPath) => Notify();

            public void OnAfterCloseFolder(string folderPath) => Notify();

            public void OnBeforeCloseFolder(string folderPath)
            {
            }

            public void OnQueryCloseFolder(string folderPath, ref int pfCancel)
            {
            }

            public void OnAfterLoadAllDeferredProjects()
            {
            }

            public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;

            public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;

            public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;

            public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;

            public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;

            public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;

            public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

            public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

            private int Notify()
            {
                _changed();
                return VSConstants.S_OK;
            }
        }
    }
}
