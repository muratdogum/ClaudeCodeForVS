using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudeCodeVS.Cli
{
    /// <summary>Finds claude.exe: the configured path, PATH, the native installer, npm, then editor-bundled copies.</summary>
    internal static class ClaudeCliLocator
    {
        public static string Locate(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                var path = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
                return File.Exists(path) ? path : null;
            }

            return Candidates().FirstOrDefault(File.Exists);
        }

        public static string NotFoundMessage(string configuredPath)
        {
            return !string.IsNullOrWhiteSpace(configuredPath)
                ? $"Ayarlarda belirtilen claude.exe bulunamadı: {configuredPath}\n(Araçlar > Seçenekler > Claude Code)"
                : "claude.exe bulunamadı. Claude Code'u kurun (PowerShell: irm https://claude.ai/install.ps1 | iex) " +
                  "veya yolunu Araçlar > Seçenekler > Claude Code altında belirtin.";
        }

        private static IEnumerable<string> Candidates()
        {
            var pathValues = new[]
            {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            };

            var directories = pathValues
                .Where(value => value != null)
                .SelectMany(value => value.Split(';'))
                .Select(dir => Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')))
                .Where(dir => dir.Length > 0 && dir.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var directory in directories)
            {
                yield return Path.Combine(directory, "claude.exe");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".local", "bin", "claude.exe");

            var npmRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@anthropic-ai");
            foreach (var file in FindFiles(npmRoot, "claude.exe", 4))
            {
                yield return file;
            }

            foreach (var editor in new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" })
            {
                foreach (var file in BundledBinaries(Path.Combine(home, editor, "extensions")))
                {
                    yield return file;
                }
            }
        }

        private static IEnumerable<string> FindFiles(string root, string fileName, int depth)
        {
            if (depth < 0 || !Directory.Exists(root))
            {
                yield break;
            }

            var direct = Path.Combine(root, fileName);
            if (File.Exists(direct))
            {
                yield return direct;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var child in children)
            {
                foreach (var file in FindFiles(child, fileName, depth - 1))
                {
                    yield return file;
                }
            }
        }

        private static IEnumerable<string> BundledBinaries(string extensionsDirectory)
        {
            if (!Directory.Exists(extensionsDirectory))
            {
                return Enumerable.Empty<string>();
            }

            try
            {
                return Directory.GetDirectories(extensionsDirectory, "anthropic.claude-code-*")
                    .OrderByDescending(dir => ParseVersion(Path.GetFileName(dir)))
                    .Select(dir => Path.Combine(dir, "resources", "native-binary", "claude.exe"))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Enumerable.Empty<string>();
            }
        }

        private static Version ParseVersion(string folderName)
        {
            var match = Regex.Match(folderName, @"anthropic\.claude-code-(\d+(?:\.\d+){1,3})");
            return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : new Version(0, 0);
        }
    }
}
