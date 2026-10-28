using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ClaudeCodeVS.Localization;

namespace ClaudeCodeVS.Cli
{
    internal sealed class ClaudeLaunchOptions
    {
        public string ExecutablePath { get; set; }

        public string WorkingDirectory { get; set; }

        public string Model { get; set; }

        public string PermissionMode { get; set; }

        public string ResumeSessionId { get; set; }

        public string AppendSystemPrompt { get; set; }

        public string AdditionalArguments { get; set; }

        /// <summary>A short-lived process used only for control requests: no MCP servers, no hooks, no tool prompts.</summary>
        public bool ControlOnly { get; set; }

        /// <summary>Extra environment variables for the CLI (privacy switches, model provider).</summary>
        public IDictionary<string, string> Environment { get; set; }

        /// <summary>Settings passed with --settings (merged by the CLI with the user's and managed settings).</summary>
        public JObject Settings { get; set; }

        /// <summary>Skip user/project MCP servers.</summary>
        public bool StrictMcpConfig { get; set; }
    }

    /// <summary>An image sent with a user message (base64, as the Messages API expects).</summary>
    internal sealed class ImageContent
    {
        public ImageContent(string mediaType, string base64)
        {
            MediaType = mediaType;
            Base64 = base64;
        }

        public string MediaType { get; }

        public string Base64 { get; }
    }

    internal sealed class ClaudeExitedEventArgs : EventArgs
    {
        public ClaudeExitedEventArgs(int? exitCode, string standardErrorTail)
        {
            ExitCode = exitCode;
            StandardErrorTail = standardErrorTail;
        }

        public int? ExitCode { get; }

        public string StandardErrorTail { get; }
    }

    internal sealed class ClaudeProcessException : Exception
    {
        public ClaudeProcessException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Runs the Claude Code CLI in stream-json mode and speaks the same stdin/stdout protocol as the
    /// official Agent SDKs: user messages and control requests go in, NDJSON messages come out.
    /// </summary>
    internal sealed class ClaudeProcess : IDisposable
    {
        private const int StandardErrorTailLimit = 4000;

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private static readonly JsonSerializerSettings ReadSettings = new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            MaxDepth = 512,
        };

        // Escaping everything outside ASCII keeps stdin independent of any console code page.
        private static readonly JsonSerializerSettings WriteSettings = new JsonSerializerSettings
        {
            StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
            Formatting = Formatting.None,
        };

        private readonly Process _process;
        private readonly JobObject _job;
        private readonly bool _inJob;
        private readonly Stream _stdin;
        private readonly object _writeLock = new object();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<JObject>>();
        private readonly StringBuilder _stderr = new StringBuilder();
        private Task _stderrTask = Task.CompletedTask;
        private int _requestCounter;
        private int _disposed;
        private volatile bool _exited;

        private ClaudeProcess(Process process, JobObject job, bool inJob)
        {
            _process = process;
            _job = job;
            _inJob = inJob;
            _stdin = process.StandardInput.BaseStream;
        }

        public event Action<JObject> MessageReceived;

        public event Action<ClaudeExitedEventArgs> Exited;

        public bool HasExited => _exited || _disposed != 0;

        public static ClaudeProcess Start(ClaudeLaunchOptions options)
        {
            var startInfo = new ProcessStartInfo(options.ExecutablePath, BuildArguments(options))
            {
                WorkingDirectory = options.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
            };

            // Same environment hygiene as the official SDKs. The IDE connection variables are meant for terminals;
            // the panel talks to Visual Studio directly.
            startInfo.EnvironmentVariables.Remove("CLAUDECODE");
            startInfo.EnvironmentVariables.Remove("CLAUDE_CODE_SSE_PORT");
            startInfo.EnvironmentVariables.Remove("ENABLE_IDE_INTEGRATION");
            startInfo.EnvironmentVariables["CLAUDE_CODE_ENTRYPOINT"] = "sdk-vs";
            foreach (var pair in options.Environment ?? new Dictionary<string, string>())
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }

            var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                process.Dispose();
                throw new ClaudeProcessException(L.F("claude.exe başlatılamadı ({0}): {1}", options.ExecutablePath, ex.Message));
            }

            var job = new JobObject();
            return new ClaudeProcess(process, job, job.TryAssign(process));
        }

        internal static string BuildArguments(ClaudeLaunchOptions options)
        {
            var args = new List<string> { "--output-format", "stream-json", "--verbose", "--input-format", "stream-json" };

            if (options.ControlOnly)
            {
                args.Add("--strict-mcp-config");
                args.Add("--settings");
                args.Add("{\"disableAllHooks\":true}");
            }
            else
            {
                args.Add("--include-partial-messages");
                args.Add("--permission-prompt-tool");
                args.Add("stdio");
                AddOption(args, "--permission-mode", options.PermissionMode);
                AddOption(args, "--model", options.Model);
                AddOption(args, "--resume", options.ResumeSessionId);
                AddOption(args, "--append-system-prompt", options.AppendSystemPrompt);
                AddOption(args, "--settings", options.Settings?.ToString(Formatting.None));
                if (options.StrictMcpConfig)
                {
                    args.Add("--strict-mcp-config");
                }
            }

            var commandLine = string.Join(" ", args.Select(QuoteArgument));
            if (!options.ControlOnly && !string.IsNullOrWhiteSpace(options.AdditionalArguments))
            {
                commandLine += " " + options.AdditionalArguments.Trim();
            }

            return commandLine;
        }

        private static void AddOption(List<string> args, string name, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                args.Add(name);
                args.Add(value);
            }
        }

        /// <summary>Quotes one argument following the CommandLineToArgvW rules.</summary>
        internal static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return argument;
            }

            var builder = new StringBuilder("\"");
            for (int i = 0; i < argument.Length; i++)
            {
                int backslashes = 0;
                while (i < argument.Length && argument[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == argument.Length)
                {
                    builder.Append('\\', backslashes * 2);
                    break;
                }

                if (argument[i] == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1).Append('"');
                }
                else
                {
                    builder.Append('\\', backslashes).Append(argument[i]);
                }
            }

            return builder.Append('"').ToString();
        }

        /// <summary>Starts the output readers. Call after subscribing to the events.</summary>
        public void BeginReading()
        {
            _stderrTask = Task.Run(ReadStandardErrorAsync);
            _ = Task.Run(ReadStandardOutputAsync);
        }

        public void SendUserMessage(string text, IReadOnlyList<ImageContent> images = null)
        {
            JToken content = text;
            if (images != null && images.Count > 0)
            {
                var blocks = new JArray();
                foreach (var image in images)
                {
                    blocks.Add(new JObject
                    {
                        ["type"] = "image",
                        ["source"] = new JObject { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = image.Base64 },
                    });
                }

                blocks.Add(new JObject { ["type"] = "text", ["text"] = text });
                content = blocks;
            }

            Write(new JObject
            {
                ["type"] = "user",
                ["session_id"] = string.Empty,
                ["parent_tool_use_id"] = JValue.CreateNull(),
                ["message"] = new JObject { ["role"] = "user", ["content"] = content },
            });
        }

        public void AllowTool(string requestId, JToken input, JArray updatedPermissions)
        {
            // updatedInput is mandatory on allow for older CLI versions, so always echo the input back.
            var response = new JObject
            {
                ["behavior"] = "allow",
                ["updatedInput"] = input?.DeepClone() ?? new JObject(),
            };
            if (updatedPermissions != null && updatedPermissions.Count > 0)
            {
                response["updatedPermissions"] = updatedPermissions.DeepClone();
            }

            RespondToControlRequest(requestId, response);
        }

        public void DenyTool(string requestId, string message, bool interrupt)
        {
            var response = new JObject { ["behavior"] = "deny", ["message"] = message };
            if (interrupt)
            {
                response["interrupt"] = true;
            }

            RespondToControlRequest(requestId, response);
        }

        public void RespondToControlRequest(string requestId, JObject response)
        {
            Write(new JObject
            {
                ["type"] = "control_response",
                ["response"] = new JObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response },
            });
        }

        public void RespondWithError(string requestId, string error)
        {
            Write(new JObject
            {
                ["type"] = "control_response",
                ["response"] = new JObject { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = error },
            });
        }

        public async Task<JObject> SendControlRequestAsync(JObject request, TimeSpan timeout)
        {
            var id = "req_" + Interlocked.Increment(ref _requestCounter) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;

            try
            {
                Write(new JObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }

            using (var timer = new CancellationTokenSource(timeout))
            using (timer.Token.Register(() =>
            {
                if (_pending.TryRemove(id, out var expired))
                {
                    expired.TrySetException(new TimeoutException(L.F("Claude Code '{0}' isteğine zamanında yanıt vermedi.", request.Str("subtype"))));
                }
            }))
            {
                return await completion.Task.ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_writeLock)
            {
                try
                {
                    _stdin.Dispose();
                }
                catch (IOException)
                {
                }
            }

            try
            {
                if (!_process.HasExited)
                {
                    if (_inJob)
                    {
                        _job.Terminate();
                    }
                    else
                    {
                        KillProcessTree(_process.Id);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }

            _job.Dispose();
            FailPending(new ClaudeProcessException(L.T("Claude Code oturumu kapatıldı.")));
            _process.Dispose();
        }

        private void Write(JObject message)
        {
            var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(message, WriteSettings) + "\n");
            lock (_writeLock)
            {
                if (_disposed != 0 || _exited)
                {
                    throw new ClaudeProcessException(L.T("Claude Code süreci çalışmıyor."));
                }

                try
                {
                    _stdin.Write(bytes, 0, bytes.Length);
                    _stdin.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                    throw new ClaudeProcessException(L.T("Claude Code sürecine yazılamadı: ") + ex.Message);
                }
            }
        }

        private async Task ReadStandardOutputAsync()
        {
            try
            {
                var reader = _process.StandardOutput;
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Length == 0 || line[0] != '{')
                    {
                        continue;
                    }

                    JObject message;
                    try
                    {
                        message = JsonConvert.DeserializeObject<JObject>(line, ReadSettings);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (message != null)
                    {
                        Dispatch(message);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
            }
            finally
            {
                await OnOutputClosedAsync().ConfigureAwait(false);
            }
        }

        private async Task ReadStandardErrorAsync()
        {
            try
            {
                var buffer = new char[1024];
                int read;
                while ((read = await _process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    lock (_stderr)
                    {
                        _stderr.Append(buffer, 0, read);
                        if (_stderr.Length > StandardErrorTailLimit)
                        {
                            _stderr.Remove(0, _stderr.Length - StandardErrorTailLimit);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
            }
        }

        private void Dispatch(JObject message)
        {
            if (message.Str("type") == "control_response")
            {
                var response = message.Obj("response");
                var id = response?.Str("request_id");
                if (id != null && _pending.TryRemove(id, out var completion))
                {
                    if (response.Str("subtype") == "error")
                    {
                        completion.TrySetException(new ClaudeProcessException(response.Str("error") ?? L.T("Claude Code isteği reddetti.")));
                    }
                    else
                    {
                        completion.TrySetResult(response.Obj("response") ?? new JObject());
                    }
                }

                return;
            }

            MessageReceived?.Invoke(message);
        }

        private async Task OnOutputClosedAsync()
        {
            int? exitCode = null;
            try
            {
                if (_process.WaitForExit(5000))
                {
                    exitCode = _process.ExitCode;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
            }

            await Task.WhenAny(_stderrTask, Task.Delay(1000)).ConfigureAwait(false);
            _exited = true;

            string stderr;
            lock (_stderr)
            {
                stderr = _stderr.ToString().Trim();
            }

            var message = L.F("Claude Code süreci kapandı (çıkış kodu {0}).", exitCode?.ToString() ?? "?");
            FailPending(new ClaudeProcessException(stderr.Length > 0 ? message + "\n" + stderr : message));

            if (_disposed == 0)
            {
                Exited?.Invoke(new ClaudeExitedEventArgs(exitCode, stderr));
            }
        }

        private void FailPending(Exception error)
        {
            foreach (var id in _pending.Keys.ToList())
            {
                if (_pending.TryRemove(id, out var completion))
                {
                    completion.TrySetException(error);
                }
            }
        }

        private static void KillProcessTree(int processId)
        {
            try
            {
                var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), $"/PID {processId} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var taskkill = Process.Start(startInfo))
                {
                    taskkill?.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
            }
        }
    }
}
