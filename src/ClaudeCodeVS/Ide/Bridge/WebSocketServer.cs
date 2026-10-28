using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Ide.Bridge
{
    /// <summary>
    /// Minimal RFC 6455 server on 127.0.0.1. Only connections that present the auth token in the
    /// X-Claude-Code-Ide-Authorization header are accepted (the same header the Claude Code CLI sends).
    /// </summary>
    internal sealed class WebSocketServer : IDisposable
    {
        public const string AuthHeader = "x-claude-code-ide-authorization";
        private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxHeaderBytes = 16 * 1024;

        private readonly TcpListener _listener;
        private readonly string _token;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        public WebSocketServer(string token)
        {
            _token = token;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public event Action<WebSocketConnection> Connected;

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }

                _ = HandshakeAsync(client);
            }
        }

        private async Task HandshakeAsync(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var headers = await ReadHeadersAsync(stream).ConfigureAwait(false);
                if (headers == null
                    || !headers.TryGetValue("sec-websocket-key", out var key)
                    || !headers.TryGetValue("upgrade", out var upgrade)
                    || upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    await RejectAsync(stream, "400 Bad Request").ConfigureAwait(false);
                    client.Dispose();
                    return;
                }

                if (!headers.TryGetValue(AuthHeader, out var token) || !FixedTimeEquals(token, _token))
                {
                    await RejectAsync(stream, "401 Unauthorized").ConfigureAwait(false);
                    client.Dispose();
                    return;
                }

                string accept;
                using (var sha1 = SHA1.Create())
                {
                    accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + AcceptGuid)));
                }

                var response = new StringBuilder()
                    .Append("HTTP/1.1 101 Switching Protocols\r\n")
                    .Append("Upgrade: websocket\r\n")
                    .Append("Connection: Upgrade\r\n")
                    .Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n");
                if (headers.TryGetValue("sec-websocket-protocol", out var protocols)
                    && protocols.IndexOf("mcp", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    response.Append("Sec-WebSocket-Protocol: mcp\r\n");
                }

                var bytes = Encoding.ASCII.GetBytes(response.Append("\r\n").ToString());
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);

                var connection = new WebSocketConnection(client, stream);
                Connected?.Invoke(connection);
                connection.Start();
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                client.Dispose();
            }
        }

        private static async Task<Dictionary<string, string>> ReadHeadersAsync(Stream stream)
        {
            var buffer = new List<byte>(1024);
            var one = new byte[1];
            while (buffer.Count < MaxHeaderBytes)
            {
                if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0)
                {
                    return null;
                }

                buffer.Add(one[0]);
                int n = buffer.Count;
                if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
                {
                    break;
                }
            }

            var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal))
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
            }

            return headers;
        }

        private static async Task RejectAsync(Stream stream, string status)
        {
            var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }
    }

    /// <summary>One accepted WebSocket: text messages in and out, ping/pong and close handled here.</summary>
    internal sealed class WebSocketConnection : IDisposable
    {
        private const int MaxMessageBytes = 64 * 1024 * 1024;

        private readonly TcpClient _client;
        private readonly Stream _stream;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private int _closed;

        public WebSocketConnection(TcpClient client, Stream stream)
        {
            _client = client;
            _stream = stream;
        }

        public event Action<string> MessageReceived;

        public event Action Closed;

        public bool IsOpen => _closed == 0;

        internal void Start() => _ = ReceiveLoopAsync();

        public async Task SendAsync(string text)
        {
            if (!IsOpen)
            {
                return;
            }

            await SendFrameAsync(0x1, Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
        }

        public void Dispose() => Close();

        private async Task ReceiveLoopAsync()
        {
            var message = new MemoryStream();
            try
            {
                while (IsOpen)
                {
                    var header = await ReadExactAsync(2).ConfigureAwait(false);
                    bool fin = (header[0] & 0x80) != 0;
                    int opcode = header[0] & 0x0F;
                    bool masked = (header[1] & 0x80) != 0;
                    long length = header[1] & 0x7F;
                    if (length == 126)
                    {
                        var ext = await ReadExactAsync(2).ConfigureAwait(false);
                        length = (ext[0] << 8) | ext[1];
                    }
                    else if (length == 127)
                    {
                        var ext = await ReadExactAsync(8).ConfigureAwait(false);
                        length = 0;
                        for (int i = 0; i < 8; i++)
                        {
                            length = (length << 8) | ext[i];
                        }
                    }

                    if (length < 0 || length + message.Length > MaxMessageBytes)
                    {
                        break;
                    }

                    var mask = masked ? await ReadExactAsync(4).ConfigureAwait(false) : null;
                    var payload = length == 0 ? new byte[0] : await ReadExactAsync((int)length).ConfigureAwait(false);
                    if (mask != null)
                    {
                        for (int i = 0; i < payload.Length; i++)
                        {
                            payload[i] ^= mask[i % 4];
                        }
                    }

                    switch (opcode)
                    {
                        case 0x0: // continuation
                        case 0x1: // text
                        case 0x2: // binary (not used by MCP, treated as UTF-8 text)
                            await message.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                            if (fin)
                            {
                                var text = Encoding.UTF8.GetString(message.ToArray());
                                message.SetLength(0);
                                MessageReceived?.Invoke(text);
                            }

                            break;
                        case 0x8: // close
                            await SendFrameAsync(0x8, payload.Length >= 2 ? new[] { payload[0], payload[1] } : new byte[0]).ConfigureAwait(false);
                            Close();
                            return;
                        case 0x9: // ping
                            await SendFrameAsync(0xA, payload).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is EndOfStreamException)
            {
            }

            Close();
        }

        private async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await _stream.ReadAsync(buffer, read, count - read).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new EndOfStreamException();
                }

                read += n;
            }

            return buffer;
        }

        private async Task SendFrameAsync(int opcode, byte[] payload)
        {
            var header = new List<byte> { (byte)(0x80 | opcode) };
            if (payload.Length < 126)
            {
                header.Add((byte)payload.Length);
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                header.Add(126);
                header.Add((byte)(payload.Length >> 8));
                header.Add((byte)payload.Length);
            }
            else
            {
                header.Add(127);
                for (int shift = 56; shift >= 0; shift -= 8)
                {
                    header.Add((byte)((long)payload.Length >> shift));
                }
            }

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = header.ToArray();
                await _stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await _stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                Close();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try
            {
                _client.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            Closed?.Invoke();
        }
    }
}
