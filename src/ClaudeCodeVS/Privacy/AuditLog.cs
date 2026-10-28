using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Privacy
{
    /// <summary>
    /// Local, append-only record of what was handed to the model: prompts, attached files and the
    /// files/commands Claude's tools touched. Written only to this machine (one JSON line per event).
    /// </summary>
    internal sealed class AuditLog
    {
        private readonly object _lock = new object();

        public AuditLog(string folder)
        {
            Folder = folder;
        }

        public static string DefaultFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCodeVS", "audit");

        public string Folder { get; }

        public bool Enabled { get; set; }

        public void Prompt(string workingDirectory, string sessionId, string text, IEnumerable<string> attachments, int maskedSecrets)
        {
            Write(new JObject
            {
                ["event"] = "prompt",
                ["cwd"] = workingDirectory,
                ["session"] = sessionId,
                ["text"] = text,
                ["attachments"] = new JArray(attachments ?? Enumerable.Empty<string>()),
                ["maskedSecrets"] = maskedSecrets,
            });
        }

        public void Tool(string sessionId, string tool, string target)
        {
            Write(new JObject { ["event"] = "tool", ["session"] = sessionId, ["tool"] = tool, ["target"] = target });
        }

        public void Permission(string sessionId, string tool, string target, string decision)
        {
            Write(new JObject { ["event"] = "permission", ["session"] = sessionId, ["tool"] = tool, ["target"] = target, ["decision"] = decision });
        }

        private void Write(JObject entry)
        {
            if (!Enabled)
            {
                return;
            }

            entry.AddFirst(new JProperty("time", DateTimeOffset.Now.ToString("o")));
            var line = entry.ToString(Formatting.None) + Environment.NewLine;
            var path = Path.Combine(Folder, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");

            // Off the UI thread; a failed write must never break the conversation.
            _ = Task.Run(() =>
            {
                try
                {
                    lock (_lock)
                    {
                        Directory.CreateDirectory(Folder);
                        File.AppendAllText(path, line, new UTF8Encoding(false));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            });
        }
    }
}
