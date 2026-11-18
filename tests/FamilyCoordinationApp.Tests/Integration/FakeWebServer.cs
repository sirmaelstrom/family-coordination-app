using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FamilyCoordinationApp.Services.AgentAccess;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// A loopback "public website" for the agents' outbound fetch. A <see cref="SafeRecipeFetcher"/> built from
/// <see cref="CreateFetcher"/> sends every connection here through its connect seam, so URLs like
/// <c>http://images.test/a.png</c> keep passing the fetcher's own URL checks while no real network is touched.
/// Each path answers with the response registered for it (404 otherwise); every request's path is recorded.
/// </summary>
public sealed class FakeWebServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Func<NetworkStream, CancellationToken, Task>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly Task _loop;

    /// <summary>A real 1×1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    /// <summary>JPEG signature plus filler: enough for the signature check, which is all the server inspects.</summary>
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0xFF, 0xD9];

    public FakeWebServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Paths requested so far, in order (e.g. <c>/a.png</c>).</summary>
    public IReadOnlyList<string> Requests => _requests.ToList();

    public SafeRecipeFetcher CreateFetcher() => new(ConnectAsync);

    /// <summary>Answer <paramref name="path"/> with a 200 carrying <paramref name="body"/>.</summary>
    public void Serve(string path, byte[] body, string? contentType)
    {
        var head = "HTTP/1.1 200 OK\r\n" + (contentType is null ? "" : $"Content-Type: {contentType}\r\n") +
                   $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        Serve(path, async (stream, ct) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(body, ct);
        });
    }

    /// <summary>Answer <paramref name="path"/> with a 200 and <paramref name="length"/> bytes, sent with NO Content-Length (read until close).</summary>
    public void ServeStreamed(string path, byte[] prefix, int length, string contentType) =>
        Serve(path, async (stream, ct) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nConnection: close\r\n\r\n"), ct);
            await stream.WriteAsync(prefix, ct);
            var chunk = new byte[64 * 1024];
            for (var sent = prefix.Length; sent < length; sent += chunk.Length)
            {
                await stream.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, length - sent)), ct);
            }
        });

    public void Serve(string path, Func<NetworkStream, CancellationToken, Task> respond) => _routes[path] = respond;

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var path = await ReadRequestPathAsync(stream);
                    _requests.Enqueue(path);
                    try
                    {
                        if (_routes.TryGetValue(path, out var respond))
                        {
                            await respond(stream, _stop.Token);
                        }
                        else
                        {
                            await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                        }
                    }
                    catch (IOException) { /* the client stopped reading (a cap or a deadline) */ }
                    catch (OperationCanceledException) { }
                }
            });
        }
    }

    /// <summary>Read the request head of a GET and return its path.</summary>
    private static async Task<string> ReadRequestPathAsync(NetworkStream stream)
    {
        var seen = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one.AsMemory(0, 1)) == 1)
        {
            seen.Add(one[0]);
            if (seen.Count >= 4 && seen[^4] == '\r' && seen[^3] == '\n' && seen[^2] == '\r' && seen[^1] == '\n') break;
        }
        var requestLine = Encoding.ASCII.GetString(seen.ToArray()).Split("\r\n")[0];
        var parts = requestLine.Split(' ');
        return parts.Length >= 2 ? parts[1] : "";
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port), ct);
        return new NetworkStream(socket, ownsSocket: true);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch (OperationCanceledException) { }
    }
}
