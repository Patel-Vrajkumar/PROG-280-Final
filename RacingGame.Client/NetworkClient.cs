using System.Net.Sockets;
using System.Text;
using RacingGame.Shared;

namespace RacingGame.Client;

/// <summary>
/// Manages the TCP connection to the game server.
/// Fires events for each received <see cref="GameMessage"/>.
/// </summary>
public class NetworkClient : IDisposable
{
    private TcpClient? _client;
    private StreamReader? _reader;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public event Action<GameMessage>? MessageReceived;
    public event Action? Disconnected;

    public bool IsConnected => _client?.Connected == true;

    // ── Connect / Disconnect ─────────────────────────────────────────────────

    public async Task ConnectAsync(string host, int port)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(host, port);
        _stream = _client.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8);
        _ = Task.Run(ReadLoopAsync);
    }

    public void Disconnect()
    {
        try { _client?.Close(); } catch { }
    }

    // ── Send ─────────────────────────────────────────────────────────────────

    public async Task SendAsync(GameMessage msg)
    {
        if (_stream is null) return;
        string line = msg.Serialize() + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        await _writeLock.WaitAsync();
        try { await _stream.WriteAsync(bytes); }
        finally { _writeLock.Release(); }
    }

    // ── Background reader ────────────────────────────────────────────────────

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                string? line = await _reader!.ReadLineAsync();
                if (line is null) break;
                var msg = GameMessage.Deserialize(line);
                if (msg is not null)
                    MessageReceived?.Invoke(msg);
            }
        }
        catch { /* connection closed */ }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _writeLock.Dispose();
    }
}
