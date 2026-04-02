using System.Net;
using System.Net.Sockets;
using RacingGame.Shared;

namespace RacingGame.Server;

/// <summary>
/// Manages TCP connections, game state, and message broadcasting.
/// Supports up to <see cref="MaxPlayers"/> simultaneous players.
/// </summary>
public class GameServer
{
    public const int MaxPlayers = 5;
    public const int FinishLine = 100;   // position units needed to win
    public const int MoveAmount = 5;     // units advanced per click

    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly List<PlayerConnection> _connections = [];
    private readonly Lock _lock = new();
    private GamePhase _phase = GamePhase.Waiting;
    private bool _running;

    public GameServer(int port)
    {
        _port = port;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public async Task StartAsync()
    {
        _listener.Start();
        _running = true;

        // Print the local IP addresses so clients know where to connect
        Console.WriteLine($"Server listening on port {_port}");
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName())
                              .Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            Console.WriteLine($"  → {ip}:{_port}");
        Console.WriteLine("Waiting for players (2–5 to start) …\n");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        while (_running)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { break; }

            _ = Task.Run(() => HandleClientAsync(client));
        }

        Console.WriteLine("Server stopped.");
    }

    public void Stop()
    {
        _running = false;
        _listener.Stop();
        lock (_lock)
            foreach (var c in _connections)
                c.Close();
    }

    // ── Client lifecycle ─────────────────────────────────────────────────────

    private async Task HandleClientAsync(TcpClient tcpClient)
    {
        var conn = new PlayerConnection(tcpClient);
        string remoteEp = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Console.WriteLine($"[+] New connection from {remoteEp}");

        try
        {
            // First message must be Join
            string? raw = await conn.ReadLineAsync();
            if (raw is null) return;

            var msg = GameMessage.Deserialize(raw);
            if (msg is null || msg.Type != MessageType.Join)
            {
                await conn.SendAsync(new GameMessage
                {
                    Type = MessageType.Error,
                    Message = "First message must be Join."
                });
                return;
            }

            string name = msg.PlayerName.Trim();
            if (string.IsNullOrEmpty(name)) name = "Player";

            lock (_lock)
            {
                if (_phase != GamePhase.Waiting)
                {
                    conn.SendAsync(new GameMessage
                    {
                        Type = MessageType.Error,
                        Message = "A game is already in progress. Try again later."
                    }).Wait();
                    conn.Close();
                    return;
                }
                if (_connections.Count >= MaxPlayers)
                {
                    conn.SendAsync(new GameMessage
                    {
                        Type = MessageType.Error,
                        Message = "Server is full (max 5 players)."
                    }).Wait();
                    conn.Close();
                    return;
                }

                // Ensure unique name
                int suffix = 2;
                string baseName = name;
                while (_connections.Any(c => c.Name == name))
                    name = $"{baseName}{suffix++}";

                conn.Name = name;
                conn.CarChoice = Math.Clamp(msg.CarChoice, 1, 3);
                _connections.Add(conn);
            }

            Console.WriteLine($"  Player joined: {conn.Name} (Car {conn.CarChoice})");

            // Tell the new player the current waiting-room state
            await conn.SendAsync(BuildWaitingRoom());

            // Broadcast PlayerJoined to everyone else
            await BroadcastExceptAsync(conn, new GameMessage
            {
                Type = MessageType.PlayerJoined,
                PlayerName = conn.Name,
                CarChoice = conn.CarChoice
            });

            PrintLobby();

            // Auto-start the race once enough players have joined
            await TryStartGameAsync();

            // Main read loop
            while (true)
            {
                raw = await conn.ReadLineAsync();
                if (raw is null) break;

                var incoming = GameMessage.Deserialize(raw);
                if (incoming is null) continue;

                await ProcessMessageAsync(conn, incoming);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Error for {conn.Name}: {ex.Message}");
        }
        finally
        {
            await RemovePlayerAsync(conn);
        }
    }

    private async Task ProcessMessageAsync(PlayerConnection sender, GameMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.Move:
                await HandleMoveAsync(sender);
                break;
        }
    }

    private async Task HandleMoveAsync(PlayerConnection sender)
    {
        bool gameOver = false;
        string winner = string.Empty;
        Dictionary<string, int> snapshot;

        lock (_lock)
        {
            if (_phase != GamePhase.InProgress) return;

            sender.Position = Math.Min(sender.Position + MoveAmount, FinishLine);

            snapshot = _connections.ToDictionary(c => c.Name, c => c.Position);

            if (sender.Position >= FinishLine)
            {
                gameOver = true;
                winner = sender.Name;
                _phase = GamePhase.Finished;
            }
        }

        if (gameOver)
        {
            Console.WriteLine($"\n🏆 Winner: {winner}!\n");
            await BroadcastAsync(new GameMessage
            {
                Type = MessageType.GameOver,
                WinnerName = winner,
                Positions = snapshot
            });
        }
        else
        {
            await BroadcastAsync(new GameMessage
            {
                Type = MessageType.PositionUpdate,
                Positions = snapshot
            });
        }
    }

    private async Task RemovePlayerAsync(PlayerConnection conn)
    {
        bool wasInGame;
        lock (_lock)
        {
            wasInGame = _connections.Remove(conn);
        }
        conn.Close();

        if (!wasInGame) return;

        Console.WriteLine($"[-] Player left: {conn.Name}");
        PrintLobby();

        await BroadcastAsync(new GameMessage
        {
            Type = MessageType.PlayerLeft,
            PlayerName = conn.Name
        });

        // If the game was in-progress and only one player remains, end the game
        lock (_lock)
        {
            if (_phase == GamePhase.InProgress && _connections.Count < 2)
            {
                _phase = GamePhase.Waiting;
                var remaining = _connections.FirstOrDefault();
                if (remaining is not null)
                {
                    _ = BroadcastAsync(new GameMessage
                    {
                        Type = MessageType.GameOver,
                        WinnerName = remaining.Name,
                        Message = "Other players disconnected – you win by default!"
                    });
                }
            }
        }

        // Auto-start if 2+ players are present and not yet in-game
        await TryStartGameAsync();
    }

    // ── Game start ───────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the race as soon as ≥2 players are connected and game is waiting.
    /// </summary>
    private async Task TryStartGameAsync()
    {
        List<PlayerConnection> snapshot;
        lock (_lock)
        {
            if (_phase != GamePhase.Waiting || _connections.Count < 2)
                return;
            _phase = GamePhase.InProgress;
            foreach (var c in _connections) c.Position = 0;
            snapshot = [.._connections];
        }

        Console.WriteLine("\n🚦 Race starting!\n");
        await BroadcastAsync(new GameMessage
        {
            Type = MessageType.GameStart,
            Players = snapshot.Select(c => new PlayerInfo
            {
                Name = c.Name,
                CarChoice = c.CarChoice
            }).ToList()
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private GameMessage BuildWaitingRoom()
    {
        lock (_lock)
        {
            return new GameMessage
            {
                Type = MessageType.WaitingRoom,
                Players = _connections.Select(c => new PlayerInfo
                {
                    Name = c.Name,
                    CarChoice = c.CarChoice
                }).ToList()
            };
        }
    }

    private async Task BroadcastAsync(GameMessage msg)
    {
        List<PlayerConnection> targets;
        lock (_lock) targets = [.._connections];
        await Task.WhenAll(targets.Select(c => c.SendAsync(msg)));
    }

    private async Task BroadcastExceptAsync(PlayerConnection except, GameMessage msg)
    {
        List<PlayerConnection> targets;
        lock (_lock) targets = _connections.Where(c => c != except).ToList();
        await Task.WhenAll(targets.Select(c => c.SendAsync(msg)));
    }

    private void PrintLobby()
    {
        lock (_lock)
        {
            Console.WriteLine($"Lobby ({_connections.Count}/{MaxPlayers}):");
            foreach (var c in _connections)
                Console.WriteLine($"  • {c.Name}  (Car {c.CarChoice})");
            if (_connections.Count >= 2 && _phase == GamePhase.Waiting)
                Console.WriteLine("  … starting race …");
            else if (_connections.Count < 2)
                Console.WriteLine("  Waiting for at least 2 players …");
        }
    }
}

public enum GamePhase { Waiting, InProgress, Finished }
