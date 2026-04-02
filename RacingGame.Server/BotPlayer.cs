using System.Net.Sockets;
using System.Text;
using RacingGame.Shared;

namespace RacingGame.Server;

/// <summary>
/// An AI-controlled bot that connects to the server just like a real player.
/// Because it uses a real TCP connection, the server treats it identically to
/// a human player – no special server-side code is needed.
///
/// The bot:
///   1. Connects to 127.0.0.1 on the given port.
///   2. Sends a Join message with its name and a random car choice.
///   3. Waits in the lobby and clicks "Ready" automatically.
///   4. Once the race starts, clicks "Move" at a random interval.
///   5. After a game ends, clicks "Ready" again for the next race.
/// </summary>
public class BotPlayer
{
    private readonly string _name;          // bot display name (e.g. "Bot_1")
    private readonly int    _port;          // server port to connect to
    private readonly Random _rng = new();   // random number generator for move timing

    private TcpClient?    _tcp;
    private StreamReader? _reader;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);  // prevents overlapping writes

    private bool _moving   = false;  // true while the bot should keep clicking Move
    private bool _running  = true;   // false when the bot should stop completely

    public BotPlayer(string name, int port)
    {
        _name = name;
        _port = port;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Connects to the server and begins playing.  This method runs until the
    /// server disconnects the bot (e.g. when the server is stopped).
    /// Call via <c>Task.Run(() => bot.RunAsync())</c> to run in the background.
    /// </summary>
    public async Task RunAsync()
    {
        // Retry connecting briefly – the server might not be ready yet
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                _tcp    = new TcpClient();
                await _tcp.ConnectAsync("127.0.0.1", _port);
                _stream = _tcp.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8);
                break;   // connection succeeded
            }
            catch
            {
                // Wait a bit before retrying
                await Task.Delay(500);
            }
        }

        if (_tcp is null || !_tcp.Connected)
            return;   // give up if we couldn't connect

        // Join the game with a random car choice
        await SendAsync(new GameMessage
        {
            Type       = MessageType.Join,
            PlayerName = _name,
            CarChoice  = _rng.Next(1, 4)   // random car: 1, 2, or 3
        });

        // Start reading messages from the server
        await ReadLoopAsync();
    }

    /// <summary>Tells the bot to stop moving and disconnect.</summary>
    public void Stop()
    {
        _running = false;
        _moving  = false;
        try { _tcp?.Close(); } catch { }
    }

    // ── Message reading ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads messages from the server one line at a time and reacts to them.
    /// Returns when the connection is closed.
    /// </summary>
    private async Task ReadLoopAsync()
    {
        try
        {
            while (_running)
            {
                string? line = await _reader!.ReadLineAsync();
                if (line is null) break;   // server closed the connection

                var msg = GameMessage.Deserialize(line);
                if (msg is not null)
                    await HandleMessageAsync(msg);
            }
        }
        catch { /* connection closed – stop silently */ }
    }

    // ── Message handling ──────────────────────────────────────────────────────

    /// <summary>Reacts to a message received from the server.</summary>
    private async Task HandleMessageAsync(GameMessage msg)
    {
        switch (msg.Type)
        {
            // ── Lobby snapshot received – send Ready after a short pause ──────
            case MessageType.WaitingRoom:
                // Wait a random 0.5–2 seconds so bots don't all ready simultaneously
                await Task.Delay(_rng.Next(500, 2000));
                await SendAsync(new GameMessage { Type = MessageType.Ready });
                break;

            // ── Race started – begin clicking Move automatically ───────────────
            case MessageType.GameStart:
                _moving = true;
                _ = Task.Run(AutoMoveLoopAsync);   // run moves on a background task
                break;

            // ── Race ended – stop moving (WaitingRoom will trigger Ready again) ─
            case MessageType.GameOver:
                _moving = false;
                break;

            // ── Ping – bots don't need to handle ping ─────────────────────────
            default:
                break;
        }
    }

    // ── Auto-move loop ────────────────────────────────────────────────────────

    /// <summary>
    /// Sends Move messages at random intervals (300–900 ms) while the race is
    /// in progress.  This simulates a human player clicking as fast as they can.
    /// </summary>
    private async Task AutoMoveLoopAsync()
    {
        while (_moving && _running)
        {
            // Wait a random amount of time between moves
            int delay = _rng.Next(300, 900);
            await Task.Delay(delay);

            if (!_moving || !_running) break;

            try
            {
                await SendAsync(new GameMessage { Type = MessageType.Move });
            }
            catch
            {
                _moving = false;   // stop if send fails (disconnected)
            }
        }
    }

    // ── Send helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Serialises and sends one message to the server, using a lock so that
    /// concurrent sends (e.g. a move arriving just as we send Ready) don't
    /// corrupt the stream.
    /// </summary>
    private async Task SendAsync(GameMessage msg)
    {
        if (_stream is null) return;
        string line = msg.Serialize() + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        await _writeLock.WaitAsync();
        try { await _stream.WriteAsync(bytes); }
        catch { _running = false; }
        finally { _writeLock.Release(); }
    }
}
