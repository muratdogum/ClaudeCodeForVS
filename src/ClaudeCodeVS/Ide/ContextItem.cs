using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeCodeVS.Ide
{
    /// <summary>A file, optionally with selected lines, sent to Claude along with a message.</summary>
    internal sealed class ContextItem
    {
        private static readonly Dictionary<string, string> Languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "csharp", [".vb"] = "vb", [".fs"] = "fsharp", [".cpp"] = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp",
            [".c"] = "c", [".h"] = "cpp", [".hpp"] = "cpp", [".ts"] = "typescript", [".tsx"] = "tsx", [".js"] = "javascript",
            [".jsx"] = "jsx", [".json"] = "json", [".xml"] = "xml", [".xaml"] = "xml", [".csproj"] = "xml", [".props"] = "xml",
            [".targets"] = "xml", [".config"] = "xml", [".html"] = "html", [".cshtml"] = "cshtml", [".razor"] = "razor",
            [".css"] = "css", [".scss"] = "scss", [".py"] = "python", [".sql"] = "sql", [".ps1"] = "powershell",
            [".sh"] = "bash", [".md"] = "markdown", [".yml"] = "yaml", [".yaml"] = "yaml", [".go"] = "go", [".rs"] = "rust",
            [".java"] = "java", [".kt"] = "kotlin",
        };

        public string FilePath { get; set; }

        public int StartLine { get; set; }

        public int EndLine { get; set; }

        public string SelectedText { get; set; }

        public bool HasSelection => !string.IsNullOrEmpty(SelectedText);

        public string FileName => Path.GetFileName(FilePath);

        public string DisplayName
        {
            get
            {
                if (!HasSelection)
                {
                    return FileName;
                }

                return StartLine == EndLine ? $"{FileName}:{StartLine}" : $"{FileName}:{StartLine}-{EndLine}";
            }
        }

        public string Language => Languages.TryGetValue(Path.GetExtension(FilePath) ?? string.Empty, out var language) ? language : string.Empty;

        public ContextItem WithSelectedText(string text) =>
            new ContextItem { FilePath = FilePath, StartLine = StartLine, EndLine = EndLine, SelectedText = text };

        public override bool Equals(object obj)
        {
            return obj is ContextItem other
                && string.Equals(FilePath, other.FilePath, StringComparison.OrdinalIgnoreCase)
                && StartLine == other.StartLine
                && EndLine == other.EndLine;
        }

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(FilePath ?? string.Empty) ^ (StartLine * 397) ^ EndLine;
    }

    internal static class FileTarget
    {
        private static readonly Regex FragmentLine = new Regex(@"^(?<path>.+?)#L(?<line>\d+)(?:C\d+)?(?:-L?\d+(?:C\d+)?)?$", RegexOptions.Compiled);
        private static readonly Regex SuffixLine = new Regex(@"^(?<path>.+?):(?<line>\d+)(?::\d+)?(?:-\d+)?$", RegexOptions.Compiled);

        /// <summary>Parses "src/a.cs", "src/a.cs:42", "src/a.cs#L42-L50" or a file:// URI.</summary>
        public static bool TryParse(string target, out string path, out int line)
        {
            line = 0;
            path = null;
            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            var value = target.Trim().Trim('<', '>', '`', '"', '\'');
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    value = new Uri(value).LocalPath;
                }
                catch (UriFormatException)
                {
                    return false;
                }
            }

            var match = FragmentLine.Match(value);
            if (!match.Success)
            {
                match = SuffixLine.Match(value);
            }

            if (match.Success && int.TryParse(match.Groups["line"].Value, out var parsedLine))
            {
                value = match.Groups["path"].Value;
                line = parsedLine;
            }

            try
            {
                value = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
            }

            path = value.Replace('/', '\\');
            return path.Length > 0 && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
    }
}
