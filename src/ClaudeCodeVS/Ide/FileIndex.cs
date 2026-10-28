using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeCodeVS.Ide
{
    /// <summary>Files under the working directory, for "@" mentions. Build off the UI thread.</summary>
    internal sealed class FileIndex
    {
        private const int MaxFiles = 30000;

        private static readonly HashSet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "packages", "dist", "out", "TestResults",
            "__pycache__", ".venv", "venv", ".gradle", "build",
        };

        private readonly List<string> _files;

        private FileIndex(string root, List<string> files)
        {
            Root = root;
            _files = files;
        }

        public string Root { get; }

        public int Count => _files.Count;

        public static FileIndex Build(string root, Func<string, bool> exclude)
        {
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && files.Count < MaxFiles)
            {
                var folder = pending.Pop();
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(folder))
                    {
                        if (!SkippedFolders.Contains(Path.GetFileName(directory)))
                        {
                            pending.Push(directory);
                        }
                    }

                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        if (exclude == null || !exclude(file))
                        {
                            files.Add(file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/'));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            return new FileIndex(root, files);
        }

        /// <summary>File name prefix matches first, then name contains, then path contains; shorter paths first.</summary>
        public IReadOnlyList<string> Search(string query, int max)
        {
            if (string.IsNullOrEmpty(query))
            {
                return _files.OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f.Length).Take(max).ToList();
            }

            return _files
                .Select(path =>
                {
                    var name = path.Substring(path.LastIndexOf('/') + 1);
                    int rank = name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
                        : name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                        : path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                        : -1;
                    return (path, rank);
                })
                .Where(x => x.rank >= 0)
                .OrderBy(x => x.rank)
                .ThenBy(x => x.path.Length)
                .Take(max)
                .Select(x => x.path)
                .ToList();
        }
    }
}
