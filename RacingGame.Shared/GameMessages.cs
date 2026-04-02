using System.Text.Json;
using System.Text.Json.Serialization;

namespace RacingGame.Shared;

/// <summary>
/// All message types exchanged between server and clients over TCP.
/// </summary>
public enum MessageType
{
    // Client → Server
    Join,           // player sends name + car choice when connecting
    Move,           // player clicked the "Move" button to advance their car
    Ready,          // player clicked "I'm Ready!" – signals they want to start

    // Server → Client(s)
    PlayerJoined,   // broadcast: a new player connected to the lobby
    PlayerLeft,     // broadcast: a player disconnected
    GameStart,      // broadcast: race begins – cars can now move
    PositionUpdate, // broadcast: updated positions for all cars
    GameOver,       // broadcast: winner announced, race is over
    WaitingRoom,    // unicast:  sent to a newly joined player with lobby snapshot
    Countdown,      // broadcast: countdown tick before race starts (Message = "3","2","1","Go!")
    Error           // unicast:  error message from the server
}

/// <summary>
/// Envelope used for all TCP messages.  Serialised as UTF-8 JSON + newline.
/// </summary>
public class GameMessage
{
    [JsonPropertyName("type")]
    public MessageType Type { get; set; }

    /// <summary>Sender's display name.</summary>
    [JsonPropertyName("playerName")]
    public string PlayerName { get; set; } = string.Empty;

    /// <summary>Car choice 1, 2 or 3.</summary>
    [JsonPropertyName("carChoice")]
    public int CarChoice { get; set; }

    /// <summary>Map of playerName → track position (0–100).</summary>
    [JsonPropertyName("positions")]
    public Dictionary<string, int>? Positions { get; set; }

    /// <summary>List of players currently in the waiting room.</summary>
    [JsonPropertyName("players")]
    public List<PlayerInfo>? Players { get; set; }

    /// <summary>Name of the winner (GameOver only).</summary>
    [JsonPropertyName("winnerName")]
    public string? WinnerName { get; set; }

    /// <summary>Human-readable error or info text.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    // ── Serialisation helpers ───────────────────────────────────────────────

    private static readonly JsonSerializerOptions _opts =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Serialize() => JsonSerializer.Serialize(this, _opts);

    public static GameMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<GameMessage>(json, _opts);
}

/// <summary>Lightweight player descriptor used in waiting-room snapshots.</summary>
public class PlayerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("carChoice")]
    public int CarChoice { get; set; }
}
