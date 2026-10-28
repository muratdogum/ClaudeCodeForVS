using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using ClaudeCodeVS.Cli;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Ide.Bridge
{
    internal sealed class McpException : Exception
    {
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        public McpException(int code, string message) : base(message)
        {
            Code = code;
        }

        public int Code { get; }
    }

    /// <summary>
    /// Lets Claude Code running in a terminal connect to this Visual Studio the way it connects to VS Code (/ide):
    /// an MCP server over WebSocket on 127.0.0.1, advertised by a lock file in ~/.claude/ide. Claude Code then sees the
    /// editor selection and the Error List, and shows proposed edits in the Visual Studio diff viewer.
    /// </summary>
    internal sealed class IdeBridge : IDisposable
    {
        public const string IdeName = "Visual Studio";
        public const string PortVariable = "CLAUDE_CODE_SSE_PORT";
        public const string IntegrationVariable = "ENABLE_IDE_INTEGRATION";

        private readonly AsyncPackage _package;
        private readonly IdeTools _tools;
        private readonly DiffSessions _diffs;
        private readonly WebSocketServer _server;
        private readonly string _token = Guid.NewGuid().ToString();
        private readonly string _lockPath;
        private readonly List<WebSocketConnection> _clients = new List<WebSocketConnection>();
        private readonly DispatcherTimer _selectionTimer;
        private SelectionSnapshot _sentSelection;
        private bool _disposed;

        private IdeBridge(AsyncPackage package, IdeTools tools)
        {
            _package = package;
            _tools = tools;
            _diffs = new DiffSessions(package);
            _server = new WebSocketServer(_token);
            _server.Connected += OnConnected;
            _lockPath = Path.Combine(LockFolder, _server.Port + ".lock");
            _selectionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            _selectionTimer.Tick += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                PublishSelection();
            };

            // Terminals started from this Visual Studio connect without /ide.
            Environment.SetEnvironmentVariable(PortVariable, Port.ToString());
            Environment.SetEnvironmentVariable(IntegrationVariable, "true");
        }

        public int Port => _server.Port;

        public int ClientCount
        {
            get
            {
                lock (_clients)
                {
                    return _clients.Count;
                }
            }
        }

        /// <summary>Raised on the UI thread with the new number of connected Claude Code sessions.</summary>
        public event Action<int> ClientsChanged;

        public static string LockFolder
        {
            get
            {
                var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                var root = string.IsNullOrEmpty(config) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : config;
                return Path.Combine(root, "ide");
            }
        }

        public static async Task<IdeBridge> StartAsync(AsyncPackage package, string workspace, Func<string, bool> isProtected)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
            var textManager = await package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
            var bridge = new IdeBridge(package, new IdeTools(package, dte, textManager, isProtected));
            bridge.SetWorkspace(workspace);
            return bridge;
        }

        /// <summary>Claude Code offers this Visual Studio to sessions started in (or under) the workspace folder.</summary>
        public void SetWorkspace(string folder)
        {
            if (_disposed)
            {
                return;
            }

            _tools.Workspace = folder;
            var content = new JObject
            {
                ["pid"] = Process.GetCurrentProcess().Id,
                ["workspaceFolders"] = string.IsNullOrEmpty(folder) ? new JArray() : new JArray(folder),
                ["ideName"] = IdeName,
                ["transport"] = "ws",
                ["runningInWindows"] = true,
                ["authToken"] = _token,
            };

            try
            {
                Directory.CreateDirectory(LockFolder);
                File.WriteAllText(_lockPath, content.ToString(Formatting.None));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.WriteLine("IDE lock file: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _selectionTimer.Stop();
            _server.Dispose();
            WebSocketConnection[] clients;
            lock (_clients)
            {
                clients = _clients.ToArray();
                _clients.Clear();
            }

            foreach (var client in clients)
            {
                client.Dispose();
            }

            try
            {
                File.Delete(_lockPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }

            if (Environment.GetEnvironmentVariable(PortVariable) == Port.ToString())
            {
                Environment.SetEnvironmentVariable(PortVariable, null);
                Environment.SetEnvironmentVariable(IntegrationVariable, null);
            }
        }

        private void OnConnected(WebSocketConnection connection)
        {
            lock (_clients)
            {
                _clients.Add(connection);
            }

            connection.MessageReceived += text => _ = HandleMessageAsync(connection, text);
            connection.Closed += () => OnClosed(connection);
            OnClientsChanged();
        }

        private void OnClosed(WebSocketConnection connection)
        {
            lock (_clients)
            {
                _clients.Remove(connection);
            }

            _ = _diffs.CloseAllAsync(connection);
            OnClientsChanged();
        }

        private void OnClientsChanged()
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                int count = ClientCount;
                if (count > 0 && !_disposed)
                {
                    _sentSelection = null;
                    _selectionTimer.Start();
                }
                else
                {
                    _selectionTimer.Stop();
                }

                ClientsChanged?.Invoke(count);
            }).FileAndForget("ClaudeCodeVS/bridge/clients");
        }

        private void PublishSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SelectionSnapshot selection;
            try
            {
                selection = _tools.ReadSelection();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException)
            {
                return;
            }

            if (selection == null || selection.SameAs(_sentSelection))
            {
                return;
            }

            _sentSelection = selection;
            Broadcast("selection_changed", selection.ToJson());
        }

        private void Broadcast(string method, JObject parameters)
        {
            var text = new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }.ToString(Formatting.None);
            WebSocketConnection[] clients;
            lock (_clients)
            {
                clients = _clients.ToArray();
            }

            foreach (var client in clients)
            {
                _ = client.SendAsync(text);
            }
        }

        private async Task HandleMessageAsync(WebSocketConnection connection, string text)
        {
            JObject message;
            try
            {
                message = JObject.Parse(text);
            }
            catch (JsonException)
            {
                return;
            }

            var method = message.Str("method");
            var id = message["id"];
            if (method == null || id == null)
            {
                return; // notifications (initialized, ide_connected) and responses need no answer
            }

            JObject reply;
            try
            {
                var result = await DispatchAsync(connection, method, message["params"] as JObject ?? new JObject());
                reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (McpException ex)
            {
                reply = Error(id, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IDE bridge {method}: {ex}");
                reply = Error(id, McpException.InternalError, ex.Message);
            }

            await connection.SendAsync(reply.ToString(Formatting.None));
        }

        private static JObject Error(JToken id, int code, string message) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = code, ["message"] = message } };

        private async Task<JObject> DispatchAsync(WebSocketConnection connection, string method, JObject parameters)
        {
            switch (method)
            {
                case "initialize":
                    return new JObject
                    {
                        ["protocolVersion"] = parameters.Str("protocolVersion") ?? "2025-06-18",
                        ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JObject { ["name"] = "Claude Code Visual Studio MCP", ["version"] = typeof(IdeBridge).Assembly.GetName().Version.ToString(3) },
                    };
                case "ping":
                    return new JObject();
                case "tools/list":
                    return new JObject { ["tools"] = IdeTools.Definitions() };
                case "tools/call":
                    return await CallToolAsync(connection, parameters.Str("name"), parameters["arguments"] as JObject ?? new JObject());
                case "resources/list":
                    return new JObject { ["resources"] = new JArray() };
                case "resources/templates/list":
                    return new JObject { ["resourceTemplates"] = new JArray() };
                case "prompts/list":
                    return new JObject { ["prompts"] = new JArray() };
                default:
                    throw new McpException(McpException.MethodNotFound, "Method not found: " + method);
            }
        }

        private async Task<JObject> CallToolAsync(WebSocketConnection connection, string name, JObject args)
        {
            switch (name)
            {
                case "openDiff":
                    return await _diffs.OpenAsync(args, connection);
                case "close_tab":
                    await _diffs.CloseAsync(args.Str("tab_name"));
                    return IdeTools.Text(DiffSessions.TabClosed);
                case "closeAllDiffTabs":
                    return IdeTools.Text($"CLOSED_{await _diffs.CloseAllAsync()}_DIFF_TABS");
            }

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            switch (name)
            {
                case "getDiagnostics":
                    return _tools.GetDiagnostics(args.Str("uri"));
                case "openFile":
                    return _tools.OpenFile(args);
                case "getOpenEditors":
                    return _tools.GetOpenEditors();
                case "getWorkspaceFolders":
                    return _tools.GetWorkspaceFolders();
                case "getCurrentSelection":
                    return _tools.GetSelection(latest: false);
                case "getLatestSelection":
                    return _tools.GetSelection(latest: true);
                case "checkDocumentDirty":
                    return _tools.CheckDocumentDirty(args.Str("filePath"));
                case "saveDocument":
                    return _tools.SaveDocument(args.Str("filePath"));
                default:
                    throw new McpException(McpException.InvalidParams, "Unknown tool: " + name);
            }
        }
    }
}
