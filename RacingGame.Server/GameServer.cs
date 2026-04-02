using System.Net;
using System.Net.Sockets;
using RacingGame.Shared;

namespace RacingGame.Server;

/// <summary>
/// Manages all TCP connections, tracks game state, and broadcasts messages.
/// Supports up to <see cref="MaxPlayers"/> simultaneous players.
/// Race begins only after ALL connected players (minimum 2) click "Ready".
/// </summary>
public class GameServer
{
    // ── Constants ────────────────────────────────────────────────────────────
    public const int MaxPlayers = 5;      // maximum players allowed in one game
    public const int FinishLine = 100;    // position units a car needs to reach to win
    public const int MoveAmount = 5;      // units a car advances per "Move" click

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly List<PlayerConnection> _connections = [];  // all connected players
    private readonly HashSet<string> _readyPlayers = [];        // names of players who clicked Ready
    private readonly Lock _lock = new();                        // thread-safety lock
    private GamePhase _phase = GamePhase.Waiting;               // current game phase
    private bool _running;                                      // true while server is accepting clients

    // ── Logger callback ───────────────────────────────────────────────────────
    // Called whenever the server wants to print status text.
    // Defaults to Console.WriteLine so the server still works headless.
    private readonly Action<string> _log;

    public GameServer(int port, Action<string>? logger = null)
    {
        _port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _log = logger ?? Console.WriteLine;  // use provided logger or fall back to console
    }

    // ── Start / Stop ─────────────────────────────────────────────────────────

    /// <summary>Starts listening for incoming TCP connections.</summary>
    public async Task StartAsync()
    {
        _listener.Start();
        _running = true;

        // Print all local IPv4 addresses so users know what IP to connect to
        _log($"Server listening on port {_port}");
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName())
                               .Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            _log($"  -> {ip}:{_port}");
        _log("Waiting for players (2-5 needed, all must click Ready) ...");

        // Keep accepting new clients until Stop() is called
        while (_running)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { break; }   // listener was stopped

            // Handle each client on its own task so we don't block new connections
            _ = Task.Run(() => HandleClientAsync(client));
        }

        _log("Server stopped.");
    }

    /// <summary>Stops the server and closes all connections.</summary>
    public void Stop()
    {
        _running = false;
        _listener.Stop();
        lock (_lock)
            foreach (var c in _connections)
                c.Close();
    }

    // ── Client lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs for one connected TCP client from first message until disconnect.
    /// </summary>
    private async Task HandleClientAsync(TcpClient tcpClient)
    {
        var conn = new PlayerConnection(tcpClient);
        string remoteEp = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _log($"[+] New connection from {remoteEp}");

        try
        {
            // ── Step 1: wait for the Join message ─────────────────────────────
            string? raw = await conn.ReadLineAsync();
            if (raw is null) return;   // client disconnected immediately

            var msg = GameMessage.Deserialize(raw);
            if (msg is null || msg.Type != MessageType.Join)
            {
                // If the first message is not a Join, reject the connection
                await conn.SendAsync(new GameMessage
                {
                    Type    = MessageType.Error,
                    Message = "First message must be Join."
                });
                return;
            }

            // ── Step 2: validate and register the player ──────────────────────
            string name = msg.PlayerName.Trim();
            if (string.IsNullOrEmpty(name)) name = "Player";  // fallback name

            lock (_lock)
            {
                // Reject if a game is already running
                if (_phase == GamePhase.InProgress || _phase == GamePhase.Countdown)
                {
                    conn.SendAsync(new GameMessage
                    {
                        Type    = MessageType.Error,
                        Message = "A game is already in progress. Try again later."
                    }).Wait();
                    conn.Close();
                    return;
                }

                // Reject if the lobby is full
                if (_connections.Count >= MaxPlayers)
                {
                    conn.SendAsync(new GameMessage
                    {
                        Type    = MessageType.Error,
                        Message = "Server is full (max 5 players)."
                    }).Wait();
                    conn.Close();
                    return;
                }

                // Make the name unique if another player has the same name
                int suffix = 2;
                string baseName = name;
                while (_connections.Any(c => c.Name == name))
                    name = $"{baseName}{suffix++}";

                conn.Name      = name;
                conn.CarChoice = Math.Clamp(msg.CarChoice, 1, 3);
                _connections.Add(conn);
            }

            _log($"  Player joined: {conn.Name} (Car {conn.CarChoice})");

            // Send the new player a snapshot of who is already in the lobby
            await conn.SendAsync(BuildWaitingRoom());

            // Tell everyone else that this player joined
            await BroadcastExceptAsync(conn, new GameMessage
            {
                Type       = MessageType.PlayerJoined,
                PlayerName = conn.Name,
                CarChoice  = conn.CarChoice
            });

            PrintLobby();

            // ── Step 3: main message loop ─────────────────────────────────────
            while (true)
            {
                raw = await conn.ReadLineAsync();
                if (raw is null) break;   // player disconnected

                var incoming = GameMessage.Deserialize(raw);
                if (incoming is null) continue;

                await ProcessMessageAsync(conn, incoming);
            }
        }
        catch (Exception ex)
        {
            _log($"[!] Error for {conn.Name}: {ex.Message}");
        }
        finally
        {
            // Always clean up when a player leaves or disconnects
            await RemovePlayerAsync(conn);
        }
    }

    /// <summary>Routes an incoming message from a player to the right handler.</summary>
    private async Task ProcessMessageAsync(PlayerConnection sender, GameMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.Move:
                await HandleMoveAsync(sender);
                break;

            case MessageType.Ready:
                await HandleReadyAsync(sender);
                break;

            case MessageType.Ping:
                // Echo the Ping straight back as a Pong with the same Timestamp.
                // The client uses the round-trip time to calculate latency.
                await sender.SendAsync(new GameMessage
                {
                    Type      = MessageType.Pong,
                    Timestamp = msg.Timestamp   // echo the client's timestamp unchanged
                });
                break;

            case MessageType.Resign:
                await HandleResignAsync(sender);
                break;
        }
    }

    // ── Move logic ────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances a player's car by <see cref="MoveAmount"/> and checks for a winner.
    /// </summary>
    private async Task HandleMoveAsync(PlayerConnection sender)
    {
        bool gameOver = false;
        string winner = string.Empty;
        Dictionary<string, int> snapshot;

        lock (_lock)
        {
            // Only process moves while the race is in progress
            if (_phase != GamePhase.InProgress) return;

            // Move the car forward (clamp so it doesn't go past the finish line)
            sender.Position = Math.Min(sender.Position + MoveAmount, FinishLine);

            // Take a snapshot of all positions to broadcast
            snapshot = _connections.ToDictionary(c => c.Name, c => c.Position);

            // Check if this player crossed the finish line
            if (sender.Position >= FinishLine)
            {
                gameOver = true;
                winner   = sender.Name;
                _phase   = GamePhase.Finished;
            }
        }

        if (gameOver)
        {
            _log($"\nWinner: {winner}!\n");
            // Tell all clients the race is over and who won
            await BroadcastAsync(new GameMessage
            {
                Type       = MessageType.GameOver,
                WinnerName = winner,
                Positions  = snapshot
            });

            // Reset ready state so a new race can start
            lock (_lock)
            {
                _readyPlayers.Clear();
                _phase = GamePhase.Waiting;
                foreach (var c in _connections) c.Position = 0;
            }

            // Send updated lobby so clients return to waiting state
            await BroadcastAsync(BuildWaitingRoom());
        }
        else
        {
            // Send the updated positions to all players
            await BroadcastAsync(new GameMessage
            {
                Type      = MessageType.PositionUpdate,
                Positions = snapshot
            });
        }
    }

    // ── Ready logic ───────────────────────────────────────────────────────────

    // ── Resign logic ──────────────────────────────────────────────────────────

    /// <summary>
    /// Handles a player voluntarily resigning from the current race.
    /// Broadcasts a PlayerLeft message and ends the game if only one player remains.
    /// </summary>
    private async Task HandleResignAsync(PlayerConnection sender)
    {
        bool wasInGame;
        string? autoWinner = null;

        lock (_lock)
        {
            // Can only resign during an active race
            if (_phase != GamePhase.InProgress) return;

            wasInGame = _connections.Remove(sender);
            _readyPlayers.Remove(sender.Name);

            // If only one player remains after the resign, they win by default
            if (_connections.Count == 1)
            {
                autoWinner = _connections[0].Name;
                _phase     = GamePhase.Waiting;
                _readyPlayers.Clear();
                foreach (var c in _connections) c.Position = 0;
            }
        }

        if (!wasInGame) return;

        sender.Close();   // disconnect the resigning player

        _log($"  {sender.Name} resigned.");

        // Tell everyone the resigner left
        await BroadcastAsync(new GameMessage
        {
            Type       = MessageType.PlayerLeft,
            PlayerName = sender.Name
        });

        // If someone wins by default, announce it and reset the lobby
        if (autoWinner is not null)
        {
            await BroadcastAsync(new GameMessage
            {
                Type       = MessageType.GameOver,
                WinnerName = autoWinner,
                Message    = $"{sender.Name} resigned – {autoWinner} wins!"
            });
            await BroadcastAsync(BuildWaitingRoom());
        }
    }

    /// <summary>
    /// Marks a player as ready.  When ALL connected players are ready (and >= 2
    /// are connected), the server starts the 3-2-1-Go! countdown.
    /// </summary>
    private async Task HandleReadyAsync(PlayerConnection sender)
    {
        bool allReady = false;

        lock (_lock)
        {
            // Only accept ready signals while in the waiting phase
            if (_phase != GamePhase.Waiting) return;

            _readyPlayers.Add(sender.Name);
            _log($"  {sender.Name} is ready ({_readyPlayers.Count}/{_connections.Count})");

            // All players must be ready AND there must be at least 2
            if (_connections.Count >= 2 && _readyPlayers.Count == _connections.Count)
            {
                allReady = true;
                _phase = GamePhase.Countdown;  // block new ready calls during countdown
            }
        }

        if (allReady)
        {
            await RunCountdownAndStartAsync();
        }
        else
        {
            // Let everyone know how many players are ready
            await BroadcastAsync(BuildWaitingRoom());
        }
    }

    /// <summary>
    /// Broadcasts a 3-2-1-Go! countdown then starts the race.
    /// </summary>
    private async Task RunCountdownAndStartAsync()
    {
        _log("All players ready - countdown starting ...");

        // Send countdown ticks: 3, 2, 1, then "Go!"
        foreach (string tick in new[] { "3", "2", "1", "Go!" })
        {
            await BroadcastAsync(new GameMessage
            {
                Type    = MessageType.Countdown,
                Message = tick
            });
            await Task.Delay(1000);  // wait one second between each tick
        }

        // Snapshot the player list and reset positions for the new race
        List<PlayerConnection> snapshot;
        lock (_lock)
        {
            _phase = GamePhase.InProgress;
            foreach (var c in _connections) c.Position = 0;
            snapshot = [.._connections];
        }

        _log("Race started!");

        // Tell all clients the race has begun
        await BroadcastAsync(new GameMessage
        {
            Type    = MessageType.GameStart,
            Players = snapshot.Select(c => new PlayerInfo
            {
                Name      = c.Name,
                CarChoice = c.CarChoice
            }).ToList()
        });
    }

    // ── Player removal ────────────────────────────────────────────────────────

    /// <summary>
    /// Removes a player when they disconnect and updates game state accordingly.
    /// </summary>
    private async Task RemovePlayerAsync(PlayerConnection conn)
    {
        bool wasInList;
        lock (_lock)
        {
            wasInList = _connections.Remove(conn);
            _readyPlayers.Remove(conn.Name);  // un-ready them so counts stay correct
        }
        conn.Close();

        if (!wasInList) return;

        _log($"[-] Player left: {conn.Name}");
        PrintLobby();

        // Let remaining players know someone left
        await BroadcastAsync(new GameMessage
        {
            Type       = MessageType.PlayerLeft,
            PlayerName = conn.Name
        });

        // If the race was running and fewer than 2 players remain, end the game
        lock (_lock)
        {
            if ((_phase == GamePhase.InProgress || _phase == GamePhase.Countdown)
                && _connections.Count < 2)
            {
                _phase = GamePhase.Waiting;
                _readyPlayers.Clear();
                var remaining = _connections.FirstOrDefault();
                if (remaining is not null)
                {
                    _ = BroadcastAsync(new GameMessage
                    {
                        Type       = MessageType.GameOver,
                        WinnerName = remaining.Name,
                        Message    = "Other players disconnected - you win by default!"
                    });
                }
            }
        }

        // Send updated waiting-room snapshot so clients reflect who is still here
        await BroadcastAsync(BuildWaitingRoom());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds a WaitingRoom message with the current lobby snapshot.</summary>
    private GameMessage BuildWaitingRoom()
    {
        lock (_lock)
        {
            return new GameMessage
            {
                Type    = MessageType.WaitingRoom,
                Players = _connections.Select(c => new PlayerInfo
                {
                    Name      = c.Name,
                    CarChoice = c.CarChoice
                }).ToList(),
                // Include ready count so the client can display "X / Y ready"
                Message = $"{_readyPlayers.Count}/{_connections.Count}"
            };
        }
    }

    /// <summary>Sends a message to every connected player.</summary>
    private async Task BroadcastAsync(GameMessage msg)
    {
        List<PlayerConnection> targets;
        lock (_lock) targets = [.._connections];
        await Task.WhenAll(targets.Select(c => c.SendAsync(msg)));
    }

    /// <summary>Sends a message to every connected player except <paramref name="except"/>.</summary>
    private async Task BroadcastExceptAsync(PlayerConnection except, GameMessage msg)
    {
        List<PlayerConnection> targets;
        lock (_lock) targets = _connections.Where(c => c != except).ToList();
        await Task.WhenAll(targets.Select(c => c.SendAsync(msg)));
    }

    /// <summary>Prints the current lobby state to the log.</summary>
    private void PrintLobby()
    {
        lock (_lock)
        {
            _log($"Lobby ({_connections.Count}/{MaxPlayers}):");
            foreach (var c in _connections)
                _log($"  * {c.Name}  (Car {c.CarChoice})");
            if (_connections.Count < 2)
                _log("  Waiting for at least 2 players ...");
            else
                _log("  Waiting for all players to click Ready ...");
        }
    }
}

/// <summary>The phases of a racing game session.</summary>
public enum GamePhase { Waiting, Countdown, InProgress, Finished }
