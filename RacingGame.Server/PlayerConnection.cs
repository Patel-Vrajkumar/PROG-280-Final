using System.Net.Sockets;
using System.Text;
using RacingGame.Shared;

namespace RacingGame.Server;

/// <summary>
/// Wraps a <see cref="TcpClient"/> and provides line-based async send/receive.
/// </summary>
public class PlayerConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string Name { get; set; } = "Unknown";
    public int CarChoice { get; set; } = 1;
    public int Position { get; set; } = 0;

    public PlayerConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8);
    }

    /// <summary>Reads one newline-terminated JSON line. Returns null on disconnect.</summary>
    public async Task<string?> ReadLineAsync()
    {
        try { return await _reader.ReadLineAsync(); }
        catch { return null; }
    }

    /// <summary>Serialises <paramref name="msg"/> and sends it as one JSON line.</summary>
    public async Task SendAsync(GameMessage msg)
    {
        string line = msg.Serialize() + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        await _writeLock.WaitAsync();
        try { await _stream.WriteAsync(bytes); }
        catch { /* ignore send errors – disconnect is handled in the read loop */ }
        finally { _writeLock.Release(); }
    }

    public void Close()
    {
        try { _client.Close(); } catch { }
    }
}
