using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Cli
{
    internal sealed class SessionSummary
    {
        public string Id { get; set; }

        public string Title { get; set; }

        public DateTime Updated { get; set; }

        public string Source { get; set; }

        public int PromptCount { get; set; }

        public string FilePath { get; set; }
    }

    /// <summary>
    /// Reads the conversations Claude Code stores per project folder
    /// (%USERPROFILE%\.claude\projects\&lt;encoded working directory&gt;\&lt;session id&gt;.jsonl), from any client.
    /// </summary>
    internal static class SessionHistory
    {
        private static readonly Regex IdeContextBlock = new Regex(@"^\s*<ide_context>[\s\S]*?</ide_context>\s*", RegexOptions.Compiled);
        private static readonly JsonSerializerSettings ReadSettings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None, MaxDepth = 512 };
        private static readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, SessionSummary Summary)> Cache =
            new ConcurrentDictionary<string, (DateTime, long, SessionSummary)>(StringComparer.OrdinalIgnoreCase);

        public static string ProjectsRoot
        {
            get
            {
                var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                var root = string.IsNullOrEmpty(config)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                    : config;
                return Path.Combine(root, "projects");
            }
        }

        public static string ProjectFolder(string workingDirectory) =>
            Path.Combine(ProjectsRoot, Regex.Replace(workingDirectory.TrimEnd('\\', '/'), "[^a-zA-Z0-9]", "-"));

        /// <summary>Newest first. Sessions without a single user prompt are skipped.</summary>
        public static List<SessionSummary> List(string workingDirectory, int max = 150)
        {
            var folder = ProjectFolder(workingDirectory);
            if (!Directory.Exists(folder))
            {
                return new List<SessionSummary>();
            }

            return new DirectoryInfo(folder).GetFiles("*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(max * 2)
                .Select(Summarize)
                .Where(s => s != null && s.PromptCount > 0)
                .Take(max)
                .ToList();
        }

        /// <summary>The main-thread user/assistant records of a session, in order (subagent and meta records removed).</summary>
        public static List<JObject> LoadTranscript(string filePath)
        {
            var records = new List<JObject>();
            foreach (var record in ReadRecords(filePath))
            {
                var type = record.Str("type");
                if ((type == "user" || type == "assistant") && !record.Bool("isSidechain") && !record.Bool("isMeta"))
                {
                    records.Add(record);
                }
            }

            return records;
        }

        /// <summary>The text a person typed, or null for tool results, commands and other generated user records.</summary>
        public static string PromptText(JObject record)
        {
            if (record.Str("type") != "user" || record.Bool("isMeta") || record.Bool("isSidechain"))
            {
                return null;
            }

            var content = record.Obj("message")?["content"];
            string text;
            if (content?.Type == JTokenType.String)
            {
                text = (string)content;
            }
            else if (content is JArray blocks && blocks.Count > 0 && blocks.OfType<JObject>().All(b => b.Str("type") == "text" || b.Str("type") == "image"))
            {
                text = string.Join("\n", blocks.OfType<JObject>().Where(b => b.Str("type") == "text").Select(b => b.Str("text")));
            }
            else
            {
                return null;
            }

            text = IdeContextBlock.Replace(text, string.Empty).Trim();
            return text.Length == 0 || text.StartsWith("<", StringComparison.Ordinal) || text.StartsWith("Caveat:", StringComparison.Ordinal)
                ? null
                : text;
        }

        public static int ImageCount(JObject record) =>
            (record.Obj("message")?["content"] as JArray)?.OfType<JObject>().Count(b => b.Str("type") == "image") ?? 0;

        public static string SourceName(string entrypoint)
        {
            switch (entrypoint)
            {
                case "sdk-vs": return "Visual Studio";
                case "claude-vscode": return "VS Code";
                case "cli": return "Terminal";
                case null: return null;
                default: return entrypoint.StartsWith("sdk", StringComparison.Ordinal) ? "SDK" : entrypoint;
            }
        }

        private static SessionSummary Summarize(FileInfo file)
        {
            if (Cache.TryGetValue(file.FullName, out var cached) && cached.Stamp == file.LastWriteTimeUtc && cached.Length == file.Length)
            {
                return cached.Summary;
            }

            var summary = new SessionSummary
            {
                Id = Path.GetFileNameWithoutExtension(file.Name),
                Updated = file.LastWriteTime,
                FilePath = file.FullName,
            };

            string firstPrompt = null, aiTitle = null, lastPrompt = null;
            foreach (var record in ReadRecords(file.FullName))
            {
                switch (record.Str("type"))
                {
                    case "ai-title":
                        aiTitle = record.Str("aiTitle") ?? aiTitle;
                        break;
                    case "last-prompt":
                        lastPrompt = record.Str("lastPrompt") ?? lastPrompt;
                        break;
                    case "user":
                        var prompt = PromptText(record);
                        if (prompt != null)
                        {
                            summary.PromptCount++;
                            firstPrompt = firstPrompt ?? prompt;
                            summary.Source = summary.Source ?? SourceName(record.Str("entrypoint"));
                        }

                        break;
                }
            }

            var title = aiTitle ?? firstPrompt ?? lastPrompt ?? "(başlıksız)";
            title = Regex.Replace(title, @"\s+", " ").Trim();
            summary.Title = title.Length > 90 ? title.Substring(0, 90) + "…" : title;
            Cache[file.FullName] = (file.LastWriteTimeUtc, file.Length, summary);
            return summary;
        }

        private static IEnumerable<JObject> ReadRecords(string filePath)
        {
            FileStream stream;
            try
            {
                // The CLI may be appending to the file right now.
                stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                yield break;
            }

            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] != '{')
                    {
                        continue;
                    }

                    JObject record = null;
                    try
                    {
                        record = JsonConvert.DeserializeObject<JObject>(line, ReadSettings);
                    }
                    catch (JsonException)
                    {
                    }

                    if (record != null)
                    {
                        yield return record;
                    }
                }
            }
        }
    }
}
