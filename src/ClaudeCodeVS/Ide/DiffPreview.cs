using System;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeCodeVS.Cli;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS.Ide
{
    /// <summary>Shows a pending Edit/MultiEdit/Write in the Visual Studio diff viewer before it is approved.</summary>
    internal static class DiffPreview
    {
        /// <summary>Folder for the temporary comparison files; never offered to Claude as editor context.</summary>
        public static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS");

        public static bool CanPreview(string toolName) => toolName == "Edit" || toolName == "MultiEdit" || toolName == "Write";

        public static async Task ShowAsync(AsyncPackage package, string toolName, JObject input)
        {
            var path = input.Str("file_path");
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("Önizleme için dosya yolu bulunamadı.");
            }

            var original = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var proposed = Apply(toolName, original, input)
                ?? throw new InvalidOperationException("Önizleme oluşturulamadı: değiştirilecek metin dosyada bulunamadı (dosya bu arada değişmiş olabilir).");

            var folder = Path.Combine(TempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
            var name = Path.GetFileName(path);
            var left = Path.Combine(folder, "mevcut", name);
            var right = Path.Combine(folder, "onerilen", name);
            Directory.CreateDirectory(Path.GetDirectoryName(left));
            Directory.CreateDirectory(Path.GetDirectoryName(right));
            File.WriteAllText(left, original, Encoding.UTF8);
            File.WriteAllText(right, proposed, Encoding.UTF8);

            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(await package.GetServiceAsync(typeof(SVsDifferenceService)) is IVsDifferenceService diff))
            {
                throw new InvalidOperationException("Visual Studio diff servisi kullanılamıyor.");
            }

            diff.OpenComparisonWindow2(
                left,
                right,
                $"{name} (Claude önerisi)",
                path,
                $"{name} (mevcut)",
                $"{name} (önerilen)",
                null,
                null,
                (uint)(__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary | __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary));
        }

        private static string Apply(string toolName, string original, JObject input)
        {
            switch (toolName)
            {
                case "Write":
                    return input.Str("content") ?? string.Empty;
                case "Edit":
                    return ApplyEdit(original, input);
                case "MultiEdit":
                    return input.Arr("edits")?.OfType<JObject>().Aggregate(original, (text, edit) => text == null ? null : ApplyEdit(text, edit));
                default:
                    return null;
            }
        }

        private static string ApplyEdit(string text, JObject edit)
        {
            var oldText = edit.Str("old_string") ?? string.Empty;
            var newText = edit.Str("new_string") ?? string.Empty;
            var replaceAll = edit.Bool("replace_all");

            if (oldText.Length == 0)
            {
                return text.Length == 0 ? newText : null;
            }

            if (!text.Contains(oldText) && text.Contains("\r\n"))
            {
                // Claude usually sends LF; match the file's CRLF line endings.
                oldText = oldText.Replace("\r\n", "\n").Replace("\n", "\r\n");
                newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            }

            int index = text.IndexOf(oldText, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            return replaceAll
                ? text.Replace(oldText, newText)
                : text.Substring(0, index) + newText + text.Substring(index + oldText.Length);
        }
    }
}
