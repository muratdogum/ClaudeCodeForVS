using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ClaudeCodeVS.Cli;

namespace ClaudeCodeVS.Ide.Bridge
{
    /// <summary>A selection in a text editor; lines and characters are 0-based like the editor protocol.</summary>
    internal sealed class SelectionSnapshot
    {
        public string FilePath { get; set; }

        public int StartLine { get; set; }

        public int StartCharacter { get; set; }

        public int EndLine { get; set; }

        public int EndCharacter { get; set; }

        public string Text { get; set; }

        public bool IsEmpty => StartLine == EndLine && StartCharacter == EndCharacter;

        public bool SameAs(SelectionSnapshot other) =>
            other != null && string.Equals(FilePath, other.FilePath, StringComparison.OrdinalIgnoreCase)
            && StartLine == other.StartLine && StartCharacter == other.StartCharacter
            && EndLine == other.EndLine && EndCharacter == other.EndCharacter && Text == other.Text;

        public JObject ToJson() => new JObject
        {
            ["text"] = Text ?? string.Empty,
            ["filePath"] = FilePath,
            ["fileUrl"] = IdeTools.FileUri(FilePath),
            ["selection"] = new JObject
            {
                ["start"] = new JObject { ["line"] = StartLine, ["character"] = StartCharacter },
                ["end"] = new JObject { ["line"] = EndLine, ["character"] = EndCharacter },
                ["isEmpty"] = IsEmpty,
            },
        };
    }

    /// <summary>The editor tools Claude Code calls over the IDE connection. Every member runs on the UI thread.</summary>
    internal sealed class IdeTools
    {
        private readonly AsyncPackage _package;
        private readonly DTE2 _dte;
        private readonly IVsTextManager _textManager;
        private readonly Func<string, bool> _isProtected;

        public IdeTools(AsyncPackage package, DTE2 dte, IVsTextManager textManager, Func<string, bool> isProtected)
        {
            _package = package;
            _dte = dte;
            _textManager = textManager;
            _isProtected = isProtected;
        }

        /// <summary>The folder Claude Code sessions are matched against; null when no solution or folder is open.</summary>
        public string Workspace { get; set; }

        /// <summary>The last selection seen in any editor (kept when focus moves to another window).</summary>
        public SelectionSnapshot LatestSelection { get; private set; }

        public static string FileUri(string path) => string.IsNullOrEmpty(path) ? null : new Uri(path).AbsoluteUri;

        /// <summary>Tool list for tools/list. Only getDiagnostics is offered to the model; the CLI uses the rest itself.</summary>
        public static JArray Definitions()
        {
            JObject Tool(string name, string description, JObject properties = null, params string[] required)
            {
                var schema = new JObject { ["type"] = "object", ["properties"] = properties ?? new JObject() };
                if (required.Length > 0)
                {
                    schema["required"] = new JArray(required);
                }

                return new JObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
            }

            JObject Str(string description) => new JObject { ["type"] = "string", ["description"] = description };
            JObject Bool(string description) => new JObject { ["type"] = "boolean", ["description"] = description };

            return new JArray
            {
                Tool("getDiagnostics",
                    "Get the errors, warnings and messages Visual Studio currently shows in its Error List (build and IntelliSense). " +
                    "Returns a JSON array with one entry per file. Pass `uri` to check one file, or omit it to get every file in the list. " +
                    "The Error List only covers what Visual Studio has analyzed and its current filter, so a missing file is not proof that it is clean.",
                    new JObject { ["uri"] = Str("file:// URI or path of the one file to check. Omit to get every file.") }),
                Tool("openDiff", "Open a diff of the proposed file contents in Visual Studio and wait for the user to accept or reject it",
                    new JObject
                    {
                        ["old_file_path"] = Str("Path of the current file."),
                        ["new_file_path"] = Str("Path the new contents will be written to."),
                        ["new_file_contents"] = Str("Proposed contents of the file."),
                        ["tab_name"] = Str("Caption of the diff window; also used by close_tab."),
                    },
                    "old_file_path", "new_file_path", "new_file_contents", "tab_name"),
                Tool("close_tab", "Close a diff window opened by openDiff", new JObject { ["tab_name"] = Str("Caption given to openDiff.") }, "tab_name"),
                Tool("closeAllDiffTabs", "Close all diff windows opened by openDiff"),
                Tool("openFile", "Open a file in the editor and optionally select a range of text",
                    new JObject
                    {
                        ["filePath"] = Str("Path to the file to open"),
                        ["preview"] = Bool("Whether to open the file in preview mode"),
                        ["startText"] = Str("Text pattern to find the start of the selection range."),
                        ["endText"] = Str("Text pattern to find the end of the selection range."),
                        ["selectToEndOfLine"] = Bool("Extend the selection to the end of the line containing the endText match."),
                        ["makeFrontmost"] = Bool("Whether to make the file the active editor."),
                    },
                    "filePath"),
                Tool("getOpenEditors", "Get information about currently open editors"),
                Tool("getWorkspaceFolders", "Get the solution or folder currently open in Visual Studio"),
                Tool("getCurrentSelection", "Get the current text selection in the active editor"),
                Tool("getLatestSelection", "Get the most recent text selection (even if not in the active editor)"),
                Tool("checkDocumentDirty", "Check if a document has unsaved changes", new JObject { ["filePath"] = Str("Path to the file to check") }, "filePath"),
                Tool("saveDocument", "Save a document with unsaved changes", new JObject { ["filePath"] = Str("Path to the file to save") }, "filePath"),
            };
        }

        public static JObject Text(params string[] texts) =>
            new JObject { ["content"] = new JArray(texts.Select(t => new JObject { ["type"] = "text", ["text"] = t })) };

        public static JObject Json(JToken value) => Text(value.ToString(Formatting.Indented));

        public JObject GetDiagnostics(string uri)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var only = string.IsNullOrEmpty(uri) ? null : ResolvePath(uri);
            var files = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            var items = _dte?.ToolWindows?.ErrorList?.ErrorItems;
            for (int i = 1; items != null && i <= items.Count; i++)
            {
                ErrorItem item;
                try
                {
                    item = items.Item(i);
                }
                catch (ArgumentException)
                {
                    break;
                }

                var file = item.FileName;
                if (string.IsNullOrEmpty(file) || (only != null && !string.Equals(file, only, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!files.TryGetValue(file, out var diagnostics))
                {
                    files[file] = diagnostics = new JArray();
                }

                int line = Math.Max(0, item.Line - 1);
                int character = Math.Max(0, item.Column - 1);
                diagnostics.Add(new JObject
                {
                    ["message"] = item.Description,
                    ["severity"] = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh ? "Error"
                        : item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium ? "Warning" : "Information",
                    ["range"] = new JObject
                    {
                        ["start"] = new JObject { ["line"] = line, ["character"] = character },
                        ["end"] = new JObject { ["line"] = line, ["character"] = character },
                    },
                    ["source"] = string.IsNullOrEmpty(item.Project) ? "Visual Studio" : item.Project,
                });
            }

            if (only != null && files.Count == 0)
            {
                files[only] = new JArray();
            }

            return Json(new JArray(files.Select(f => new JObject { ["uri"] = FileUri(f.Key), ["diagnostics"] = f.Value })));
        }

        public JObject OpenFile(JObject args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = ResolvePath(args.Str("filePath"));
            if (path == null || !File.Exists(path))
            {
                throw new McpException(McpException.InvalidParams, $"File not found: {args.Str("filePath")}");
            }

            VsShellUtilities.OpenDocument(_package, path, Guid.Empty, out _, out _, out IVsWindowFrame frame, out IVsTextView view);
            if (args["makeFrontmost"]?.Type == JTokenType.Boolean && !args.Bool("makeFrontmost"))
            {
                frame?.ShowNoActivate();
            }
            else
            {
                frame?.Show();
            }

            var startText = args.Str("startText");
            if (string.IsNullOrEmpty(startText) || view == null || ErrorHandler.Failed(view.GetBuffer(out IVsTextLines lines)))
            {
                return Text($"Opened file: {path}");
            }

            var text = BufferText(lines);
            int start = text.IndexOf(startText, StringComparison.Ordinal);
            if (start < 0)
            {
                return Text($"Opened file, but text \"{startText}\" not found");
            }

            int end = start + startText.Length;
            var endText = args.Str("endText");
            string message = $"Opened file and selected text \"{startText}\"";
            if (!string.IsNullOrEmpty(endText))
            {
                int found = text.IndexOf(endText, end, StringComparison.Ordinal);
                if (found >= 0)
                {
                    end = found + endText.Length;
                    message = $"Opened file and selected text from \"{startText}\" to \"{endText}\"";
                }
                else
                {
                    end = start;
                    message = $"Opened file and positioned at \"{startText}\" (end text \"{endText}\" not found)";
                }
            }

            lines.GetLineIndexOfPosition(start, out int startLine, out int startColumn);
            lines.GetLineIndexOfPosition(end, out int endLine, out int endColumn);
            if (args.Bool("selectToEndOfLine"))
            {
                lines.GetLengthOfLine(endLine, out endColumn);
            }

            view.SetSelection(startLine, startColumn, endLine, endColumn);
            view.CenterLines(startLine, Math.Max(1, endLine - startLine + 1));
            return Text(message);
        }

        public JObject GetOpenEditors()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var tabs = new JArray();
            var active = SafeActiveDocument();
            foreach (Document document in _dte.Documents)
            {
                var path = SafeFullName(document);
                if (string.IsNullOrEmpty(path) || path.StartsWith(DiffPreview.TempRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                tabs.Add(new JObject
                {
                    ["uri"] = FileUri(path),
                    ["fileName"] = path,
                    ["label"] = Path.GetFileName(path),
                    ["isActive"] = document == active,
                    ["isDirty"] = !document.Saved,
                    ["languageId"] = new ContextItem { FilePath = path }.Language,
                });
            }

            return Json(new JObject { ["tabs"] = tabs });
        }

        public JObject GetWorkspaceFolders()
        {
            var folders = new JArray();
            if (!string.IsNullOrEmpty(Workspace))
            {
                folders.Add(new JObject { ["name"] = Path.GetFileName(Workspace.TrimEnd('\\')), ["uri"] = FileUri(Workspace), ["path"] = Workspace, ["index"] = 0 });
            }

            return Json(new JObject { ["success"] = true, ["folders"] = folders, ["rootPath"] = Workspace });
        }

        public JObject GetSelection(bool latest)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selection = latest ? LatestSelection ?? ReadSelection() : ReadSelection();
            return selection == null || (!latest && selection.IsEmpty)
                ? Json(new JObject { ["success"] = false, ["message"] = latest ? "No selection available" : "No active editor found" })
                : Json(new JObject { ["success"] = true }.Merged(selection.ToJson()));
        }

        public JObject CheckDocumentDirty(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = ResolvePath(filePath);
            var document = FindDocument(path);
            return document == null
                ? Json(new JObject { ["success"] = false, ["message"] = $"Document not open: {path}" })
                : Json(new JObject { ["success"] = true, ["filePath"] = path, ["isDirty"] = !document.Saved, ["isUntitled"] = !File.Exists(path) });
        }

        public JObject SaveDocument(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = ResolvePath(filePath);
            var document = FindDocument(path);
            if (document == null)
            {
                return Json(new JObject { ["success"] = false, ["message"] = $"Document not open: {path}" });
            }

            bool dirty = !document.Saved;
            if (dirty)
            {
                document.Save();
            }

            return Json(new JObject
            {
                ["success"] = true,
                ["filePath"] = path,
                ["saved"] = dirty,
                ["message"] = dirty ? "Document saved successfully" : "Document was not dirty or save failed",
            });
        }

        /// <summary>Reads the selection of the last active text view; protected files report the position but no text.</summary>
        public SelectionSnapshot ReadSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_textManager == null
                || ErrorHandler.Failed(_textManager.GetActiveView(0, null, out IVsTextView view)) || view == null
                || ErrorHandler.Failed(view.GetBuffer(out IVsTextLines lines))
                || !(lines is IPersistFileFormat persist)
                || ErrorHandler.Failed(persist.GetCurFile(out string path, out _))
                || string.IsNullOrEmpty(path)
                || path.StartsWith(DiffPreview.TempRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (ErrorHandler.Failed(view.GetSelection(out int anchorLine, out int anchorColumn, out int endLine, out int endColumn)))
            {
                return null;
            }

            bool reversed = anchorLine > endLine || (anchorLine == endLine && anchorColumn > endColumn);
            string text = null;
            if (!_isProtected(path))
            {
                view.GetSelectedText(out text);
            }

            var snapshot = new SelectionSnapshot
            {
                FilePath = path,
                StartLine = reversed ? endLine : anchorLine,
                StartCharacter = reversed ? endColumn : anchorColumn,
                EndLine = reversed ? anchorLine : endLine,
                EndCharacter = reversed ? anchorColumn : endColumn,
                Text = text ?? string.Empty,
            };
            LatestSelection = snapshot;
            return snapshot;
        }

        internal static string BufferText(IVsTextLines lines)
        {
            lines.GetLastLineIndex(out int lastLine, out int lastIndex);
            lines.GetLineText(0, 0, lastLine, lastIndex, out string text);
            return text ?? string.Empty;
        }

        private string ResolvePath(string pathOrUri)
        {
            if (string.IsNullOrWhiteSpace(pathOrUri))
            {
                return null;
            }

            var path = pathOrUri;
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    path = new Uri(path).LocalPath;
                }
                catch (UriFormatException)
                {
                    return null;
                }
            }

            path = path.Replace('/', '\\');
            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(Workspace))
            {
                path = Path.Combine(Workspace, path);
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        private Document FindDocument(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (path == null)
            {
                return null;
            }

            foreach (Document document in _dte.Documents)
            {
                if (string.Equals(SafeFullName(document), path, StringComparison.OrdinalIgnoreCase))
                {
                    return document;
                }
            }

            return null;
        }

        private Document SafeActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return _dte.ActiveDocument;
            }
            catch (COMException)
            {
                return null;
            }
        }

        private static string SafeFullName(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return document.FullName;
            }
            catch (COMException)
            {
                return null;
            }
        }
    }

    internal static class JObjectMergeExtensions
    {
        public static JObject Merged(this JObject target, JObject source)
        {
            foreach (var property in source.Properties())
            {
                target[property.Name] = property.Value;
            }

            return target;
        }
    }
}
