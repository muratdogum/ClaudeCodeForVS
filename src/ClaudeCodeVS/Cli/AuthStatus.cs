using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Cli
{
    public enum LoginMethod
    {
        ClaudeAccount,
        Sso,
        Console,
    }

    /// <summary>
    /// Claude Code's sign-in state (`claude auth status --json`). The login is shared with the terminal
    /// and the VS Code extension: it lives in %USERPROFILE%\.claude, not in this extension.
    /// </summary>
    internal sealed class AuthStatus
    {
        public bool LoggedIn { get; private set; }

        public string AuthMethod { get; private set; }

        public string ApiProvider { get; private set; }

        public string Email { get; private set; }

        public string OrgId { get; private set; }

        public string OrgName { get; private set; }

        public string SubscriptionType { get; private set; }

        public string Error { get; private set; }

        /// <summary>Signed in with a personal claude.ai plan (Free/Pro/Max) rather than an organization plan.</summary>
        public bool IsConsumerPlan =>
            AuthMethod == "claude.ai" && (SubscriptionType == "free" || SubscriptionType == "pro" || SubscriptionType == "max");

        public bool UsesCloudProvider => ApiProvider != null && ApiProvider != "firstParty";

        public string ShortName =>
            !LoggedIn ? "Giriş yapılmadı"
            : UsesCloudProvider ? ProviderName
            : !string.IsNullOrEmpty(OrgName) ? OrgName
            : !string.IsNullOrEmpty(Email) ? Email
            : MethodName;

        public string MethodName
        {
            get
            {
                switch (AuthMethod)
                {
                    case "claude.ai": return "Claude aboneliği (claude.ai)";
                    case "console": return "Anthropic Console (API faturalı)";
                    case "api_key": return "API anahtarı";
                    case "third_party": return "Bulut sağlayıcısı kimlik bilgileri";
                    case "none": return "Yok";
                    default: return AuthMethod ?? "Bilinmiyor";
                }
            }
        }

        public string ProviderName
        {
            get
            {
                switch (ApiProvider)
                {
                    case "bedrock": return "Amazon Bedrock";
                    case "vertex": return "Google Vertex AI";
                    case "foundry": return "Microsoft Foundry";
                    case "firstParty": return "Anthropic";
                    default: return ApiProvider ?? "Anthropic";
                }
            }
        }

        public string PlanName
        {
            get
            {
                switch (SubscriptionType)
                {
                    case null: return null;
                    case "team": return "Team";
                    case "enterprise": return "Enterprise";
                    case "pro": return "Pro (kişisel)";
                    case "max": return "Max (kişisel)";
                    case "free": return "Free (kişisel)";
                    default: return SubscriptionType;
                }
            }
        }

        /// <summary>Whether the signed-in organization matches the expected name or id (case-insensitive).</summary>
        public bool MatchesOrganization(string expected)
        {
            if (string.IsNullOrWhiteSpace(expected))
            {
                return true;
            }

            expected = expected.Trim();
            return string.Equals(OrgName, expected, StringComparison.OrdinalIgnoreCase)
                || string.Equals(OrgId, expected, StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<AuthStatus> QueryAsync(string executablePath, IDictionary<string, string> environment)
        {
            try
            {
                var output = await RunAsync(executablePath, "auth status --json", environment, 20000).ConfigureAwait(false);
                var json = JsonConvert.DeserializeObject<JObject>(output.Substring(Math.Max(0, output.IndexOf('{'))));
                return new AuthStatus
                {
                    LoggedIn = json.Bool("loggedIn"),
                    AuthMethod = json.Str("authMethod"),
                    ApiProvider = json.Str("apiProvider"),
                    Email = json.Str("email"),
                    OrgId = json.Str("orgId"),
                    OrgName = json.Str("orgName"),
                    SubscriptionType = json.Str("subscriptionType"),
                };
            }
            catch (Exception ex) when (ex is Win32Exception || ex is JsonException || ex is InvalidOperationException || ex is TimeoutException || ex is ArgumentException)
            {
                return new AuthStatus { Error = ex.Message };
            }
        }

        /// <summary>
        /// Starts `claude auth login` in its own console window: the CLI opens the browser and the person
        /// completes sign-in there. Returns the process so the caller can refresh the status when it exits.
        /// </summary>
        public static Process StartLogin(string executablePath, LoginMethod method, IDictionary<string, string> environment)
        {
            var arguments = "auth login " + (method == LoginMethod.Sso ? "--sso" : method == LoginMethod.Console ? "--console" : "--claudeai");
            var startInfo = new ProcessStartInfo(executablePath, arguments) { UseShellExecute = false, CreateNoWindow = false };
            Prepare(startInfo, environment);
            var process = Process.Start(startInfo);
            if (process != null)
            {
                process.EnableRaisingEvents = true;
            }

            return process;
        }

        public static Task LogoutAsync(string executablePath, IDictionary<string, string> environment) =>
            RunAsync(executablePath, "auth logout", environment, 20000);

        private static async Task<string> RunAsync(string executablePath, string arguments, IDictionary<string, string> environment, int timeoutMs)
        {
            var startInfo = new ProcessStartInfo(executablePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            Prepare(startInfo, environment);
            using (var process = Process.Start(startInfo))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                bool exited = await Task.Run(() => process.WaitForExit(timeoutMs)).ConfigureAwait(false);
                if (!exited)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    throw new TimeoutException("claude " + arguments + " yanıt vermedi.");
                }

                await errors.ConfigureAwait(false);
                return await output.ConfigureAwait(false);
            }
        }

        private static void Prepare(ProcessStartInfo startInfo, IDictionary<string, string> environment)
        {
            startInfo.EnvironmentVariables.Remove("CLAUDECODE");
            foreach (var pair in environment ?? new Dictionary<string, string>())
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }
        }
    }
}
