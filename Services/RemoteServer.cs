using libvlc_media.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace libvlc_media.Services
{
    public sealed class RemoteServer : IDisposable
    {
        public const int DefaultPort = 8787;
        private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private readonly Func<RemoteSnapshot> _captureState;
        private readonly Func<int, int, int, byte[]?> _getThumbnail;
        private readonly Action<RemoteCommand> _applyCommand;
        private readonly List<WsConnection> _clients = new();
        private readonly object _clientsLock = new();
        private readonly byte[] _indexHtml;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _debounce;
        private bool _disposed;

        public RemoteServer(
            Func<RemoteSnapshot> captureState,
            Func<int, int, int, byte[]?> getThumbnail,
            Action<RemoteCommand> applyCommand)
        {
            _captureState = captureState;
            _getThumbnail = getThumbnail;
            _applyCommand = applyCommand;
            _indexHtml = LoadIndexHtml();
        }

        public bool IsRunning { get; private set; }

        public int Port { get; private set; } = DefaultPort;

        public string? Error { get; private set; }

        public IReadOnlyList<string> Urls { get; private set; } = [];

        public int ClientCount
        {
            get
            {
                lock (_clientsLock)
                    return _clients.Count;
            }
        }

        public event Action<int>? ClientCountChanged;

        public void Start()
        {
            if (IsRunning)
                return;

            Exception? last = null;
            for (var port = DefaultPort; port < DefaultPort + 10; port++)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Any, port);
                    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            if (_listener is null)
            {
                Error = last?.Message ?? "Could not open a network port for the tablet remote.";
                IsRunning = false;
                Urls = [];
                return;
            }

            Error = null;
            IsRunning = true;
            Urls = BuildUrls(Port);
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_cts.Token);
        }

        public void NotifyStateChanged()
        {
            if (!IsRunning)
                return;

            _debounce?.Cancel();
            _debounce = new CancellationTokenSource();
            var token = _debounce.Token;
            _ = DebouncedBroadcastAsync(token);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            IsRunning = false;
            _debounce?.Cancel();
            _debounce?.Dispose();
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            lock (_clientsLock)
            {
                foreach (var client in _clients)
                    client.Dispose();
                _clients.Clear();
            }
            _cts?.Dispose();
        }

        private async Task DebouncedBroadcastAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(80, token);
                await BroadcastStateAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener is not null)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token);
                    client.NoDelay = true;
                    var captured = client;
                    _ = Task.Run(() => HandleClientAsync(captured, token), token);
                }
                catch (OperationCanceledException)
                {
                    client?.Close();
                    break;
                }
                catch
                {
                    client?.Close();
                    if (token.IsCancellationRequested)
                        break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient tcp, CancellationToken token)
        {
            try
            {
                using var _ = tcp;
                using var stream = tcp.GetStream();
                var request = await ReadHttpRequestAsync(stream, token);
                if (request is null)
                    return;

                if (IsWebSocketUpgrade(request))
                {
                    tcp.ReceiveTimeout = 0;
                    tcp.SendTimeout = 0;
                    await AcceptWebSocketAsync(tcp, stream, request, token);
                    return;
                }

                await HandleHttpAsync(stream, request, token);
            }
            catch
            {
            }
        }

        private async Task HandleHttpAsync(NetworkStream stream, HttpRequest request, CancellationToken token)
        {
            var path = request.Path;
            if (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", _indexHtml, token);
                return;
            }

            if (path.Equals("/api/state", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(_captureState(), JsonOptions);
                await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), token);
                return;
            }

            if (path.Equals("/api/command", StringComparison.OrdinalIgnoreCase) && request.Method == "POST")
            {
                ApplyJsonCommand(request.Body);
                await WriteResponseAsync(stream, 204, "text/plain", [], token);
                return;
            }

            if (TryParseSlideImage(path, out var deck, out var slide, out var width))
            {
                var png = _getThumbnail(deck, slide, width);
                if (png is { Length: > 0 })
                {
                    await WriteResponseAsync(stream, 200, "image/png", png, token, cacheSeconds: 15);
                    return;
                }

                await WriteResponseAsync(stream, 404, "text/plain", "not found"u8.ToArray(), token);
                return;
            }

            await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8", "not found"u8.ToArray(), token);
        }

        private async Task AcceptWebSocketAsync(
            TcpClient tcp,
            NetworkStream stream,
            HttpRequest request,
            CancellationToken token)
        {
            if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out var key) || string.IsNullOrWhiteSpace(key))
            {
                await WriteResponseAsync(stream, 400, "text/plain", "missing key"u8.ToArray(), token);
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WsGuid)));
            var handshake =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n" +
                "\r\n";
            var bytes = Encoding.ASCII.GetBytes(handshake);
            await stream.WriteAsync(bytes.AsMemory(), token);

            var connection = new WsConnection(tcp, stream);
            lock (_clientsLock)
                _clients.Add(connection);
            RaiseClientCount();

            try
            {
                await connection.SendTextAsync(JsonSerializer.Serialize(_captureState(), JsonOptions), token);
                while (!token.IsCancellationRequested && tcp.Connected)
                {
                    var message = await connection.ReadTextAsync(token);
                    if (message is null)
                        break;
                    ApplyJsonCommand(message);
                }
            }
            catch
            {
            }
            finally
            {
                lock (_clientsLock)
                    _clients.Remove(connection);
                connection.Dispose();
                RaiseClientCount();
            }
        }

        private async Task BroadcastStateAsync()
        {
            string json;
            try
            {
                json = JsonSerializer.Serialize(_captureState(), JsonOptions);
            }
            catch
            {
                return;
            }

            List<WsConnection> clients;
            lock (_clientsLock)
                clients = _clients.ToList();

            foreach (var client in clients)
            {
                try
                {
                    await client.SendTextAsync(json, CancellationToken.None);
                }
                catch
                {
                    lock (_clientsLock)
                        _clients.Remove(client);
                    client.Dispose();
                    RaiseClientCount();
                }
            }
        }

        private void ApplyJsonCommand(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            RemoteCommand? command;
            try
            {
                command = JsonSerializer.Deserialize<RemoteCommand>(json, JsonOptions);
            }
            catch
            {
                return;
            }

            if (command is null || string.IsNullOrWhiteSpace(command.Type))
                return;

            try
            {
                _applyCommand(command);
            }
            catch
            {
            }
        }

        private void RaiseClientCount() => ClientCountChanged?.Invoke(ClientCount);

        private static bool IsWebSocketUpgrade(HttpRequest request)
        {
            return request.Path.Equals("/ws", StringComparison.OrdinalIgnoreCase)
                   && request.Headers.TryGetValue("Upgrade", out var upgrade)
                   && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseSlideImage(string path, out int deck, out int slide, out int width)
        {
            deck = 0;
            slide = 0;
            width = 640;
            if (!path.StartsWith("/thumb/", StringComparison.OrdinalIgnoreCase))
                return false;

            var rest = path["/thumb/".Length..];

            var q = rest.IndexOf('?');
            if (q >= 0)
                rest = rest[..q];
            var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2
                   && int.TryParse(parts[0], out deck)
                   && int.TryParse(parts[1], out slide);
        }

        private static async Task<HttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[1024];
            var headerEnd = -1;
            while (buffer.Length < 65536)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), token);
                if (read <= 0)
                    return null;
                buffer.Write(chunk, 0, read);
                headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
                if (headerEnd >= 0)
                    break;
            }

            if (headerEnd < 0)
                return null;

            var raw = buffer.GetBuffer();
            var headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
            var lines = headerText.Split(["\r\n"], StringSplitOptions.None);
            if (lines.Length == 0)
                return null;

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2)
                return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            var path = requestLine[1];
            var q = path.IndexOf('?');
            if (q >= 0)
                path = path[..q];

            var body = string.Empty;
            var extra = (int)buffer.Length - (headerEnd + 4);
            headers.TryGetValue("Content-Length", out var lengthText);
            var contentLength = 0;
            if (!string.IsNullOrEmpty(lengthText))
                int.TryParse(lengthText, out contentLength);

            if (contentLength > 0)
            {
                var bodyBytes = new byte[contentLength];
                var copied = Math.Max(0, extra);
                if (copied > 0)
                    Array.Copy(raw, headerEnd + 4, bodyBytes, 0, Math.Min(copied, contentLength));
                var offset = Math.Min(copied, contentLength);
                while (offset < contentLength)
                {
                    var read = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), token);
                    if (read <= 0)
                        break;
                    offset += read;
                }
                body = Encoding.UTF8.GetString(bodyBytes, 0, offset);
            }

            return new HttpRequest(requestLine[0].ToUpperInvariant(), path, headers, body);
        }

        private static int IndexOfHeaderEnd(byte[] data, int length)
        {
            for (var i = 0; i <= length - 4; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            }
            return -1;
        }

        private static async Task WriteResponseAsync(
            Stream stream,
            int status,
            string contentType,
            byte[] body,
            CancellationToken token,
            int cacheSeconds = 0)
        {
            var reason = status switch
            {
                200 => "OK",
                204 => "No Content",
                400 => "Bad Request",
                404 => "Not Found",
                101 => "Switching Protocols",
                _ => "OK"
            };
            var cache = cacheSeconds > 0 ? $"max-age={cacheSeconds}" : "no-store";
            var header =
                $"HTTP/1.1 {status} {reason}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                $"Cache-Control: {cache}\r\n" +
                "Connection: close\r\n" +
                "\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes.AsMemory(), token);
            if (body.Length > 0)
                await stream.WriteAsync(body.AsMemory(), token);
        }

        private static IReadOnlyList<string> BuildUrls(int port)
        {
            var urls = new List<string>();
            foreach (var ip in GetLanIPv4Addresses())
                urls.Add($"http://{ip}:{port}/");
            urls.Add($"http://127.0.0.1:{port}/");
            return urls;
        }

        public static IReadOnlyList<string> GetLanIPv4Addresses()
        {
            var found = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                    continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(addr.Address))
                        continue;
                    var text = addr.Address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal))
                        continue;
                    if (!found.Contains(text, StringComparer.Ordinal))
                        found.Add(text);
                }
            }

            return found
                .OrderBy(ScoreIp)
                .ToList();
        }

        private static int ScoreIp(string ip)
        {
            if (ip.StartsWith("192.168.", StringComparison.Ordinal))
                return 0;
            if (ip.StartsWith("10.", StringComparison.Ordinal))
                return 1;
            if (ip.StartsWith("172.", StringComparison.Ordinal))
                return 2;
            return 3;
        }

        private static byte[] LoadIndexHtml()
        {
            var assembly = typeof(RemoteServer).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("RemoteWeb.index.html", StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "RemoteWeb", "index.html");
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
                return Encoding.UTF8.GetBytes("<h1>Remote page missing</h1>");
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
                return Encoding.UTF8.GetBytes("<h1>Remote page missing</h1>");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private sealed record HttpRequest(
            string Method,
            string Path,
            Dictionary<string, string> Headers,
            string Body);

        private sealed class WsConnection : IDisposable
        {
            private readonly Stream _stream;
            private readonly SemaphoreSlim _write = new(1, 1);
            private bool _disposed;

            public WsConnection(TcpClient tcp, Stream stream)
            {
                Tcp = tcp;
                _stream = stream;
            }

            public TcpClient Tcp { get; }

            public async Task SendTextAsync(string text, CancellationToken token)
            {
                var payload = Encoding.UTF8.GetBytes(text);
                var header = BuildServerFrameHeader(payload.Length, opcode: 1);
                await _write.WaitAsync(token);
                try
                {
                    await _stream.WriteAsync(header.AsMemory(), token);
                    await _stream.WriteAsync(payload.AsMemory(), token);
                }
                finally
                {
                    _write.Release();
                }
            }

            public async Task<string?> ReadTextAsync(CancellationToken token)
            {
                while (true)
                {
                    var frame = await ReadFrameAsync(token);
                    if (frame is null)
                        return null;

                    switch (frame.Opcode)
                    {
                        case 1:
                            return Encoding.UTF8.GetString(frame.Payload);
                        case 8:
                            return null;
                        case 9:
                            await SendControlAsync(10, frame.Payload, token);
                            break;
                        case 10:
                            break;
                        default:
                            break;
                    }
                }
            }

            private async Task SendControlAsync(byte opcode, byte[] payload, CancellationToken token)
            {
                var header = BuildServerFrameHeader(payload.Length, opcode);
                await _write.WaitAsync(token);
                try
                {
                    await _stream.WriteAsync(header.AsMemory(), token);
                    if (payload.Length > 0)
                        await _stream.WriteAsync(payload.AsMemory(), token);
                }
                finally
                {
                    _write.Release();
                }
            }

            private async Task<WsFrame?> ReadFrameAsync(CancellationToken token)
            {
                var header = await ReadExactAsync(2, token);
                if (header is null)
                    return null;

                var opcode = (byte)(header[0] & 0x0F);
                var masked = (header[1] & 0x80) != 0;
                var length = header[1] & 0x7F;
                if (length == 126)
                {
                    var ext = await ReadExactAsync(2, token);
                    if (ext is null)
                        return null;
                    length = (ext[0] << 8) | ext[1];
                }
                else if (length == 127)
                {
                    var ext = await ReadExactAsync(8, token);
                    if (ext is null)
                        return null;
                    length = 0;
                    for (var i = 0; i < 8; i++)
                        length = (length << 8) | ext[i];
                }

                if (length < 0 || length > 1024 * 1024)
                    return null;

                byte[]? mask = null;
                if (masked)
                {
                    mask = await ReadExactAsync(4, token);
                    if (mask is null)
                        return null;
                }

                var payload = length == 0 ? [] : await ReadExactAsync(length, token);
                if (payload is null)
                    return null;

                if (mask is not null)
                {
                    for (var i = 0; i < payload.Length; i++)
                        payload[i] ^= mask[i % 4];
                }

                return new WsFrame(opcode, payload);
            }

            private async Task<byte[]?> ReadExactAsync(int count, CancellationToken token)
            {
                var buffer = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), token);
                    if (read <= 0)
                        return null;
                    offset += read;
                }
                return buffer;
            }

            private static byte[] BuildServerFrameHeader(int length, byte opcode)
            {
                if (length < 126)
                    return [(byte)(0x80 | opcode), (byte)length];
                if (length <= ushort.MaxValue)
                    return [(byte)(0x80 | opcode), 126, (byte)(length >> 8), (byte)length];

                var header = new byte[10];
                header[0] = (byte)(0x80 | opcode);
                header[1] = 127;
                var value = (long)length;
                for (var i = 9; i >= 2; i--)
                {
                    header[i] = (byte)(value & 0xFF);
                    value >>= 8;
                }
                return header;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                try { Tcp.Close(); } catch { }
                _write.Dispose();
            }

            private sealed record WsFrame(byte Opcode, byte[] Payload);
        }
    }
}
