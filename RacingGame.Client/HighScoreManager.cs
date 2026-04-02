using System.Text.Json;

namespace RacingGame.Client;

/// <summary>
/// Persists and retrieves each player's lifetime win count.
/// Data is stored in a JSON file in the user's AppData folder so scores
/// survive between game sessions without needing a database.
/// </summary>
public static class HighScoreManager
{
    // Full path to the JSON score file (created automatically on first save)
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RacingGame", "scores.json");

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Increments the win count for <paramref name="playerName"/> by 1 and saves.
    /// Creates the file if it does not exist yet.
    /// </summary>
    public static void RecordWin(string playerName)
    {
        var scores = LoadAll();
        scores[playerName] = scores.TryGetValue(playerName, out int wins) ? wins + 1 : 1;
        SaveAll(scores);
    }

    /// <summary>
    /// Returns the top <paramref name="count"/> players sorted by win count (descending).
    /// Returns an empty list if the score file does not exist yet.
    /// </summary>
    public static List<(string Name, int Wins)> GetTopScores(int count = 10)
    {
        return LoadAll()
            .OrderByDescending(kv => kv.Value)
            .Take(count)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the JSON score file and returns a dictionary of name → win count.
    /// Returns an empty dictionary if the file is missing or unreadable.
    /// </summary>
    private static Dictionary<string, int> LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [];
        }
        catch
        {
            // If the file is corrupted, start fresh rather than crashing
            return [];
        }
    }

    /// <summary>Writes the score dictionary back to the JSON file.</summary>
    private static void SaveAll(Dictionary<string, int> scores)
    {
        try
        {
            // Ensure the AppData/RacingGame folder exists before writing
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(scores,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* silently ignore disk errors so the game can still run */ }
    }
}
