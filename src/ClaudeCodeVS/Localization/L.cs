using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace ClaudeCodeVS.Localization
{
    /// <summary>
    /// UI text lookup. The Turkish source text is the key; Localization\&lt;language&gt;.json files (embedded in
    /// the assembly) map it to translations. Missing entries fall back to English, then to the Turkish key.
    /// </summary>
    internal static class L
    {
        /// <summary>Supported languages: code and native name.</summary>
        public static readonly (string Code, string Name)[] Languages =
        {
            ("tr", "Türkçe"), ("en", "English"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español"), ("it", "Italiano"),
            ("pt-BR", "Português (Brasil)"), ("ru", "Русский"), ("pl", "Polski"), ("cs", "Čeština"), ("ja", "日本語"), ("ko", "한국어"),
            ("zh-Hans", "中文(简体)"), ("zh-Hant", "中文(繁體)"), ("nl", "Nederlands"), ("uk", "Українська"),
        };

        private static readonly object Gate = new object();
        private static Dictionary<string, string> _strings;
        private static Dictionary<string, string> _english;

        public static string Language { get; private set; }

        /// <summary>Raised on the calling thread when <see cref="Initialize"/> switches to a different language.</summary>
        public static event EventHandler LanguageChanged;

        /// <summary>Selects the UI language: a code from <see cref="Languages"/>, or null/"auto" for Visual Studio's language.</summary>
        public static void Initialize(string preference)
        {
            string previous;
            lock (Gate)
            {
                previous = Language;
                Language = Resolve(string.IsNullOrEmpty(preference) || preference == "auto" ? CultureInfo.CurrentUICulture : null, preference);
                _strings = Language == "tr" ? new Dictionary<string, string>() : Load(Language);
                _english = Language == "en" ? _strings : Load("en");
            }

            if (previous != null && previous != Language)
            {
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static string T(string key)
        {
            EnsureInitialized();
            if (key == null)
            {
                return null;
            }

            return _strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value
                : Language != "tr" && _english.TryGetValue(key, out var english) && !string.IsNullOrEmpty(english) ? english
                : key;
        }

        public static string F(string format, params object[] args)
        {
            var text = T(format);
            try
            {
                return string.Format(CultureInfo.CurrentCulture, text, args);
            }
            catch (FormatException)
            {
                // A broken translation must never break the UI.
                return string.Format(CultureInfo.CurrentCulture, format, args);
            }
        }

        private static void EnsureInitialized()
        {
            if (_strings == null)
            {
                Initialize(null);
            }
        }

        private static string Resolve(CultureInfo culture, string preference)
        {
            if (culture == null)
            {
                return Languages.Any(l => l.Code == preference) ? preference : "en";
            }

            var name = culture.Name;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return name.EndsWith("TW", StringComparison.OrdinalIgnoreCase) || name.EndsWith("HK", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("MO", StringComparison.OrdinalIgnoreCase) || name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "zh-Hant"
                    : "zh-Hans";
            }

            if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
            {
                return "pt-BR";
            }

            var neutral = culture.TwoLetterISOLanguageName;
            return Languages.Any(l => l.Code == neutral) ? neutral : "en";
        }

        private static Dictionary<string, string> Load(string language)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".Localization." + language + ".json", StringComparison.OrdinalIgnoreCase));
            if (resource == null)
            {
                return new Dictionary<string, string>();
            }

            using (var stream = assembly.GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream))
            {
                try
                {
                    return JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd()) ?? new Dictionary<string, string>();
                }
                catch (JsonException)
                {
                    return new Dictionary<string, string>();
                }
            }
        }
    }
}
