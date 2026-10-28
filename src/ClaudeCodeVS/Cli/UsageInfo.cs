using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using ClaudeCodeVS.Localization;

namespace ClaudeCodeVS.Cli
{
    internal sealed class UsageLimit
    {
        public string Label { get; set; }

        public string ShortLabel { get; set; }

        public double Percent { get; set; }

        public DateTimeOffset? ResetsAt { get; set; }

        public string Severity { get; set; }
    }

    /// <summary>Plan usage windows (5-hour session, weekly, per-model) parsed from the CLI's get_usage response.</summary>
    internal sealed class UsageInfo
    {
        public bool Available { get; private set; }

        public string SubscriptionType { get; private set; }

        public DateTimeOffset FetchedAt { get; private set; }

        public List<UsageLimit> Limits { get; } = new List<UsageLimit>();

        public static UsageInfo Parse(JObject response)
        {
            var rateLimits = response.Obj("rate_limits");
            var info = new UsageInfo
            {
                FetchedAt = DateTimeOffset.Now,
                SubscriptionType = response.Str("subscription_type"),
                Available = response.Value<bool?>("rate_limits_available") ?? rateLimits != null,
            };

            if (rateLimits == null)
            {
                return info;
            }

            var limits = rateLimits.Arr("limits");
            if (limits != null && limits.Count > 0)
            {
                foreach (var limit in limits.OfType<JObject>())
                {
                    var model = limit.Obj("scope")?.Obj("model")?.Str("display_name");
                    var kind = limit.Str("kind");
                    var group = limit.Str("group");
                    string label, shortLabel;
                    if (kind == "session" || group == "session")
                    {
                        label = L.T("Oturum (5 saat)");
                        shortLabel = L.T("Oturum");
                    }
                    else if (group == "weekly")
                    {
                        label = model != null ? L.F("{0} limiti (haftalık)", model) : L.T("Haftalık (tüm modeller)");
                        shortLabel = model ?? L.T("Haftalık");
                    }
                    else
                    {
                        label = shortLabel = model ?? kind ?? group ?? L.T("Limit");
                    }

                    info.Limits.Add(new UsageLimit
                    {
                        Label = label,
                        ShortLabel = shortLabel,
                        Percent = limit.Num("percent") ?? 0,
                        ResetsAt = ParseDate(limit.Str("resets_at")),
                        Severity = limit.Str("severity"),
                    });
                }

                return info;
            }

            // Older CLI builds only expose the named windows.
            AddWindow(info, rateLimits.Obj("five_hour"), L.T("Oturum (5 saat)"), L.T("Oturum"));
            AddWindow(info, rateLimits.Obj("seven_day"), L.T("Haftalık (tüm modeller)"), L.T("Haftalık"));
            foreach (var scoped in rateLimits.Arr("model_scoped")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var name = scoped.Str("display_name") ?? L.T("Model");
                AddWindow(info, scoped, L.F("{0} limiti (haftalık)", name), name);
            }

            return info;
        }

        private static void AddWindow(UsageInfo info, JObject window, string label, string shortLabel)
        {
            if (window == null)
            {
                return;
            }

            info.Limits.Add(new UsageLimit
            {
                Label = label,
                ShortLabel = shortLabel,
                Percent = window.Num("utilization") ?? 0,
                ResetsAt = ParseDate(window.Str("resets_at")),
            });
        }

        private static DateTimeOffset? ParseDate(string value)
        {
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? date
                : (DateTimeOffset?)null;
        }
    }
}
