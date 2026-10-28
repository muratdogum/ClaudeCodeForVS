using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Cli
{
    /// <summary>
    /// One conversation with Claude Code: owns the CLI process, remembers the session id so a crashed
    /// or stopped process can be resumed, and exposes the control requests the UI needs.
    /// Events are raised on background threads.
    /// </summary>
    internal sealed class ClaudeSession : IDisposable
    {
        private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(30);

        private volatile ClaudeProcess _process;

        public event Action<JObject> MessageReceived;

        public event Action<ClaudeExitedEventArgs> Exited;

        public string SessionId { get; private set; }

        public string WorkingDirectory { get; private set; }

        public JArray Models { get; private set; }

        /// <summary>Slash commands reported by the CLI (name, description, argumentHint).</summary>
        public JArray Commands { get; private set; }

        public bool IsRunning
        {
            get
            {
                var process = _process;
                return process != null && !process.HasExited;
            }
        }

        private ClaudeProcess Current
        {
            get
            {
                var process = _process;
                if (process == null || process.HasExited)
                {
                    throw new ClaudeProcessException("Claude Code oturumu çalışmıyor.");
                }

                return process;
            }
        }

        public async Task StartAsync(ClaudeLaunchOptions options)
        {
            Stop();

            var process = ClaudeProcess.Start(options);
            process.MessageReceived += message =>
            {
                if (_process != process)
                {
                    return;
                }

                var sessionId = message.Str("session_id");
                if (!string.IsNullOrEmpty(sessionId))
                {
                    SessionId = sessionId;
                }

                MessageReceived?.Invoke(message);
            };
            process.Exited += e =>
            {
                if (_process == process)
                {
                    Exited?.Invoke(e);
                }
            };

            _process = process;
            WorkingDirectory = options.WorkingDirectory;
            if (!string.IsNullOrEmpty(options.ResumeSessionId))
            {
                SessionId = options.ResumeSessionId;
            }

            process.BeginReading();

            try
            {
                var init = await process.SendControlRequestAsync(Initialize(), InitializeTimeout).ConfigureAwait(false);
                Models = init.Arr("models");
                Commands = init.Arr("commands");
            }
            catch
            {
                if (_process == process)
                {
                    Stop();
                }

                throw;
            }
        }

        public void SendUserMessage(string text, IReadOnlyList<ImageContent> images = null) => Current.SendUserMessage(text, images);

        /// <summary>Points the next start at an earlier conversation (picked from history) without starting a process.</summary>
        public void PrepareResume(string sessionId, string workingDirectory)
        {
            Stop();
            SessionId = sessionId;
            WorkingDirectory = workingDirectory;
        }

        public Task<JObject> GetContextUsageAsync() =>
            Current.SendControlRequestAsync(new JObject { ["subtype"] = "get_context_usage" }, ControlTimeout);

        public void AllowTool(string requestId, JToken input, JArray updatedPermissions) =>
            Current.AllowTool(requestId, input, updatedPermissions);

        public void DenyTool(string requestId, string message, bool interrupt) =>
            Current.DenyTool(requestId, message, interrupt);

        public void RejectControlRequest(string requestId, string error) => Current.RespondWithError(requestId, error);

        public Task<JObject> InterruptAsync() =>
            Current.SendControlRequestAsync(new JObject { ["subtype"] = "interrupt" }, ControlTimeout);

        public Task<JObject> SetPermissionModeAsync(string mode) =>
            Current.SendControlRequestAsync(new JObject { ["subtype"] = "set_permission_mode", ["mode"] = mode }, ControlTimeout);

        public Task<JObject> SetModelAsync(string model) =>
            Current.SendControlRequestAsync(new JObject { ["subtype"] = "set_model", ["model"] = model }, ControlTimeout);

        public Task<JObject> GetUsageAsync() =>
            Current.SendControlRequestAsync(new JObject { ["subtype"] = "get_usage" }, UsageTimeout);

        /// <summary>Stops the process but keeps the session id so the conversation can be resumed.</summary>
        public void Stop()
        {
            var process = _process;
            _process = null;
            process?.Dispose();
        }

        /// <summary>Stops the process and forgets the conversation.</summary>
        public void Reset()
        {
            Stop();
            SessionId = null;
            WorkingDirectory = null;
            Models = null;
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Reads plan usage through a short-lived control-only process, independent of any chat session.
        /// No model request is made, so this does not consume usage itself.
        /// </summary>
        /// <summary>Returns the get_usage response and the slash commands from initialize.</summary>
        public static async Task<(JObject Usage, JArray Commands)> FetchUsageAsync(string executablePath, string workingDirectory, IDictionary<string, string> environment = null)
        {
            using (var process = ClaudeProcess.Start(new ClaudeLaunchOptions
            {
                ExecutablePath = executablePath,
                WorkingDirectory = workingDirectory,
                ControlOnly = true,
                Environment = environment,
            }))
            {
                process.BeginReading();
                var init = await process.SendControlRequestAsync(Initialize(), InitializeTimeout).ConfigureAwait(false);
                var usage = await process.SendControlRequestAsync(new JObject { ["subtype"] = "get_usage" }, UsageTimeout).ConfigureAwait(false);
                return (usage, init.Arr("commands"));
            }
        }

        private static JObject Initialize() => new JObject { ["subtype"] = "initialize", ["hooks"] = JValue.CreateNull() };
    }
}
