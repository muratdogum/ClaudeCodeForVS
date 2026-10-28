using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using ClaudeCodeVS.Localization;

namespace ClaudeCodeVS.Privacy
{
    public enum ModelProvider
    {
        Anthropic,
        AmazonBedrock,
        GoogleVertexAI,
        MicrosoftFoundry,
        CorporateGateway,
    }

    /// <summary>
    /// Turns the privacy options into what the CLI understands: environment variables (no telemetry,
    /// error reports or feedback uploads; model provider) and permission deny rules (no tools that send
    /// data to third parties or publish it, no reading protected files, no obvious network commands).
    /// </summary>
    internal sealed class PrivacyPolicy
    {
        public const string DefaultProtectedPatterns =
            ".env; .env.*; *.pem; *.key; *.pfx; *.p12; *.snk; *.kdbx; id_rsa*; id_ed25519*; *.publishsettings; *.pubxml.user; " +
            "secrets.json; appsettings.Production.json; **/secrets/**; **/.ssh/**";

        /// <summary>Tools that send data somewhere other than the model, or publish/share it.</summary>
        private static readonly string[] ExternalTools =
        {
            "Artifact", "ArtifactComments", "ArtifactData", "ShareOnboardingGuide", "PublishPlugin",
            "RemoteTrigger", "DesignSync", "PushNotification", "SendFeedback",
        };

        private static readonly string[] WebTools = { "WebFetch", "WebSearch" };

        /// <summary>Shell commands whose main purpose is moving data over the network.</summary>
        private static readonly string[] NetworkCommands =
        {
            "curl", "wget", "Invoke-WebRequest", "iwr", "Invoke-RestMethod", "irm", "Start-BitsTransfer",
            "scp", "sftp", "ftp", "nc", "ncat", "Send-MailMessage",
        };

        public bool Enabled { get; set; }

        public bool AllowWebTools { get; set; }

        public bool AllowMcpServers { get; set; }

        /// <summary>Deny shell commands whose purpose is moving data over the network (curl, Invoke-WebRequest, scp…).</summary>
        public bool BlockNetworkCommands { get; set; }

        public bool ScanSecrets { get; set; }

        public bool AuditLog { get; set; }

        public ModelProvider Provider { get; set; }

        public IReadOnlyList<string> ProtectedPatterns { get; set; } = Array.Empty<string>();

        public IDictionary<string, string> ExtraEnvironment { get; set; } = new Dictionary<string, string>();

        public static string ManagedSettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode", "managed-settings.json");

        public static IReadOnlyList<string> ParsePatterns(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().Replace('\\', '/'))
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IDictionary<string, string> ParseEnvironment(string value)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in (value ?? string.Empty).Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                if (equals > 0)
                {
                    result[pair.Substring(0, equals).Trim()] = pair.Substring(equals + 1).Trim();
                }
            }

            return result;
        }

        /// <summary>True when IT has deployed a Claude Code policy (file or registry) on this machine.</summary>
        public static bool IsManagedPolicyPresent()
        {
            try
            {
                if (File.Exists(ManagedSettingsPath))
                {
                    return true;
                }

                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode"))
                {
                    return key != null;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
            {
                return false;
            }
        }

        public IDictionary<string, string> BuildEnvironment()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Enabled)
            {
                // Names and effects per code.claude.com/docs/en/env-vars and /data-usage.
                env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1"; // updates, telemetry, error reports, /feedback, Remote Control
                env["DISABLE_TELEMETRY"] = "1";
                env["DO_NOT_TRACK"] = "1";
                env["DISABLE_ERROR_REPORTING"] = "1";
                env["DISABLE_FEEDBACK_COMMAND"] = "1"; // /feedback, /bug, /share
                env["CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY"] = "1";
                env["CLAUDE_CODE_SEND_FEEDBACK"] = "0";

                // Turning telemetry off also turns off the feature flag behind the PowerShell tool, and Bash deny
                // rules make the CLI drop it too; force it on so Windows machines keep a shell (and its deny rules).
                env["CLAUDE_CODE_USE_POWERSHELL_TOOL"] = "1";
                if (!AllowWebTools)
                {
                    env["CLAUDE_CODE_DISABLE_WEB_FETCH"] = "1";
                }
            }

            switch (Provider)
            {
                case ModelProvider.AmazonBedrock:
                    env["CLAUDE_CODE_USE_BEDROCK"] = "1";
                    break;
                case ModelProvider.GoogleVertexAI:
                    env["CLAUDE_CODE_USE_VERTEX"] = "1";
                    break;
                case ModelProvider.MicrosoftFoundry:
                    env["CLAUDE_CODE_USE_FOUNDRY"] = "1";
                    break;
            }

            foreach (var pair in ExtraEnvironment)
            {
                env[pair.Key] = pair.Value;
            }

            return env;
        }

        /// <summary>Settings passed with --settings; deny rules from all sources are combined by the CLI.</summary>
        public JObject BuildSettings()
        {
            if (!Enabled)
            {
                return null;
            }

            var deny = new List<string>(ExternalTools);
            if (!AllowWebTools)
            {
                deny.AddRange(WebTools);
            }

            if (BlockNetworkCommands)
            {
                foreach (var command in NetworkCommands)
                {
                    deny.Add($"Bash({command} *)");
                    deny.Add($"PowerShell({command} *)");
                }
            }

            foreach (var pattern in ProtectedPatterns)
            {
                var rulePath = pattern.Contains("/") ? pattern : "**/" + pattern;
                deny.Add($"Read({rulePath})");
                deny.Add($"Edit({rulePath})");
            }

            return new JObject
            {
                ["enableArtifact"] = false,
                ["disableRemoteControl"] = true,
                ["permissions"] = new JObject
                {
                    ["deny"] = new JArray(deny.Distinct()),
                    ["disableBypassPermissionsMode"] = "disable",
                    ["disableAutoMode"] = "disable",
                },
            };
        }

        /// <summary>Whether a shell command looks like it moves data over the network (for warnings on permission cards).</summary>
        public static bool LooksLikeNetworkCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            return NetworkCommands.Any(c => Regex.IsMatch(command, @"(^|[\s;|&(])" + Regex.Escape(c) + @"(\.exe)?(\s|$)", RegexOptions.IgnoreCase))
                || Regex.IsMatch(command, @"https?://|\bgit\s+push\b|\bNet\.WebClient\b|\bHttpClient\b|\burllib\b|\brequests\.(get|post)\b", RegexOptions.IgnoreCase);
        }

        /// <summary>Whether a file must not be offered to Claude as editor context.</summary>
        public bool IsProtected(string path)
        {
            if (!Enabled || string.IsNullOrEmpty(path))
            {
                return false;
            }

            var normalized = path.Replace('\\', '/');
            var name = Path.GetFileName(path);

            // gitignore-like: a pattern without "/" matches the file name in any folder,
            // a pattern with "/" matches a trailing part of the path.
            return ProtectedPatterns.Any(pattern => pattern.Contains("/")
                ? GlobToRegex(pattern, anyPrefix: true).IsMatch(normalized)
                : GlobToRegex(pattern, anyPrefix: false).IsMatch(name));
        }

        public IEnumerable<string> Describe()
        {
            if (!Enabled)
            {
                yield return L.T("Gizlilik modu kapalı: Claude Code kendi varsayılan ayarlarıyla çalışır.");
                yield break;
            }

            yield return L.T("Telemetri, hata raporu ve geri bildirim gönderimi kapalı.");
            yield return L.T("Veri yayınlayan/paylaşan araçlar engelli (artifact, paylaşım, uzak tetikleyici, bildirim).");
            yield return AllowWebTools ? L.T("Web araçları (WebFetch/WebSearch) açık.") : L.T("Web araçları (WebFetch/WebSearch) engelli.");
            yield return AllowMcpServers ? L.T("MCP sunucuları kullanılabilir.") : L.T("MCP sunucuları devre dışı (yalnızca yerleşik araçlar).");
            yield return BlockNetworkCommands
                ? L.T("Ağa veri taşıyan kabuk komutları engelli (curl, wget, Invoke-WebRequest, scp…).")
                : L.T("Kabuk komutları her seferinde onayınızı bekler; ağa veri gönderebilecek komutlar kartta uyarıyla işaretlenir.");
            yield return ProtectedPatterns.Count > 0
                ? L.F("Korunan dosyalar okunamaz ve bağlama eklenmez ({0} desen).", ProtectedPatterns.Count)
                : L.T("Korunan dosya deseni tanımlı değil.");
            yield return ScanSecrets ? L.T("Gönderilen mesajlarda gizli bilgi taraması açık.") : L.T("Gizli bilgi taraması kapalı.");
            yield return L.T("\"İzinleri atla\" ve \"Otomatik\" modları kapalı; her işlem onayınızla yapılır.");
            yield return AuditLog ? L.T("Yerel gönderim günlüğü tutuluyor.") : L.T("Gönderim günlüğü kapalı.");
        }

        public static string ProviderName(ModelProvider provider)
        {
            switch (provider)
            {
                case ModelProvider.AmazonBedrock: return L.T("Amazon Bedrock (kendi AWS hesabınız)");
                case ModelProvider.GoogleVertexAI: return L.T("Google Vertex AI (kendi GCP projeniz)");
                case ModelProvider.MicrosoftFoundry: return L.T("Microsoft Foundry (kendi Azure kaynağınız)");
                case ModelProvider.CorporateGateway: return L.T("Kurumsal ağ geçidi (ANTHROPIC_BASE_URL)");
                default: return "Anthropic";
            }
        }

        private static Regex GlobToRegex(string glob, bool anyPrefix)
        {
            var body = Regex.Escape(glob.TrimStart('/'))
                .Replace(@"\*\*/", "(?:.*/)?")
                .Replace(@"/\*\*", "(?:/.*)?")
                .Replace(@"\*\*", ".*")
                .Replace(@"\*", "[^/]*")
                .Replace(@"\?", "[^/]");
            return new Regex((anyPrefix ? "(?:^|/)" : "^") + body + "$", RegexOptions.IgnoreCase);
        }
    }
}
