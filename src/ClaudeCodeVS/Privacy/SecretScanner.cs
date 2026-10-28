using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeCodeVS.Privacy
{
    internal sealed class SecretMatch
    {
        public SecretMatch(string kind, int index, int length, int line)
        {
            Kind = kind;
            Index = index;
            Length = length;
            Line = line;
        }

        public string Kind { get; }

        public int Index { get; }

        public int Length { get; }

        public int Line { get; }
    }

    /// <summary>
    /// Finds credentials in text the user is about to send (prompt and attached selections), so they can be
    /// masked before leaving the machine. A best-effort safety net, not a guarantee.
    /// </summary>
    internal static class SecretScanner
    {
        public const string Mask = "«GİZLİ»";

        // A "secret" group marks the part to mask; without it the whole match is masked.
        private static readonly (string Kind, Regex Pattern)[] Patterns =
        {
            ("Özel anahtar (private key)", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.Compiled)),
            ("AWS erişim anahtarı", new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
            ("GitHub token", new Regex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,})\b", RegexOptions.Compiled)),
            ("Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled)),
            ("Google API anahtarı", new Regex(@"\bAIza[0-9A-Za-z\-_]{35}\b", RegexOptions.Compiled)),
            ("Anthropic API anahtarı", new Regex(@"\bsk-ant-[A-Za-z0-9\-_]{20,}", RegexOptions.Compiled)),
            ("API anahtarı (sk-…)", new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9_\-]{32,}\b", RegexOptions.Compiled)),
            ("Stripe anahtarı", new Regex(@"\b[sr]k_live_[0-9A-Za-z]{20,}", RegexOptions.Compiled)),
            ("JWT", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.Compiled)),
            ("Azure/bağlantı anahtarı", new Regex(@"(?i)\b(?:AccountKey|SharedAccessKey|SharedAccessSignature)\s*=\s*(?<secret>[A-Za-z0-9+/=%&:._-]{20,})", RegexOptions.Compiled)),
            ("Bağlantı dizesi parolası", new Regex(@"(?i)\b(?:password|pwd)\s*=\s*(?<secret>[^;'""\s]{4,})", RegexOptions.Compiled)),
            ("Parola/gizli değer ataması", new Regex(@"(?i)\b(?:api[_-]?key|secret|client[_-]?secret|access[_-]?token|auth[_-]?token|passwd|password)\b[""']?\s*[:=]\s*[""']?(?<secret>[^""'\s,;]{8,})", RegexOptions.Compiled)),
        };

        public static IReadOnlyList<SecretMatch> Scan(string text)
        {
            var matches = new List<SecretMatch>();
            if (string.IsNullOrEmpty(text))
            {
                return matches;
            }

            foreach (var (kind, pattern) in Patterns)
            {
                foreach (Match match in pattern.Matches(text))
                {
                    var secret = match.Groups["secret"];
                    int index = secret.Success ? secret.Index : match.Index;
                    int length = secret.Success ? secret.Length : match.Length;
                    if (length == 0 || IsPlaceholder(text.Substring(index, length)) || matches.Any(m => Overlaps(m, index, length)))
                    {
                        continue;
                    }

                    matches.Add(new SecretMatch(kind, index, length, LineOf(text, index)));
                }
            }

            return matches.OrderBy(m => m.Index).ToList();
        }

        public static string Redact(string text, IReadOnlyList<SecretMatch> matches)
        {
            if (matches.Count == 0)
            {
                return text;
            }

            var builder = new StringBuilder(text);
            foreach (var match in matches.OrderByDescending(m => m.Index))
            {
                builder.Remove(match.Index, match.Length).Insert(match.Index, Mask);
            }

            return builder.ToString();
        }

        private static bool IsPlaceholder(string value)
        {
            var v = value.Trim('"', '\'');
            return v == Mask
                || v.StartsWith("<", System.StringComparison.Ordinal)
                || v.StartsWith("${", System.StringComparison.Ordinal)
                || v.StartsWith("$(", System.StringComparison.Ordinal)
                || v.StartsWith("%", System.StringComparison.Ordinal)
                || Regex.IsMatch(v, @"^(x+|\*+|\.+|your[_-]?\w*|example\w*|changeme|placeholder|null|none|true|false)$", RegexOptions.IgnoreCase);
        }

        private static bool Overlaps(SecretMatch m, int index, int length) => index < m.Index + m.Length && m.Index < index + length;

        private static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }
    }
}
