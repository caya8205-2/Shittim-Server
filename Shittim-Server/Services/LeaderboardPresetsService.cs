using System.Text.Json;

namespace Shittim_Server.Services;

// Reads the squad list used by the placeholder leaderboards from `leaderboard-presets.json`. Keeping it in
// a file instead of in-game presets matters because the game only holds five presets per group, while a
// season needs a team per difficulty and raid type. The file is re-read when it changes, so entries can be
// added without a rebuild.
public class LeaderboardPresetsService
{
    private const string FileName = "leaderboard-presets.json";

    private readonly object _gate = new();
    private readonly Dictionary<string, List<MockSquad>> _boards = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _stamp = DateTime.MinValue;
    private string? _path;

    public string? LoadedFrom => _path;

    // Squads for one board, in file order. Falls back to the `default` board when the board is empty.
    public IReadOnlyList<MockSquad> Squads(string board)
    {
        Refresh();

        lock (_gate)
        {
            if (_boards.TryGetValue(board, out var squads) && squads.Count > 0)
                return squads.ToList();

            // A per-boss board can be empty while the shared board still has entries.
            if (_boards.TryGetValue("totalAssault", out var shared) && shared.Count > 0)
                return shared.ToList();

            if (_boards.TryGetValue("default", out var fallback))
                return fallback.ToList();

            return [];
        }
    }

    private void Refresh()
    {
        var path = Locate();
        if (path == null)
            return;

        var stamp = File.GetLastWriteTimeUtc(path);

        lock (_gate)
        {
            if (path == _path && stamp == _stamp)
                return;

            try
            {
                var parsed = Parse(path);
                _boards.Clear();
                foreach (var (name, list) in parsed)
                    _boards[name] = list;

                _path = path;
                _stamp = stamp;
            }
            catch
            {
                // A malformed file keeps the previous contents rather than emptying every board.
                _path ??= path;
            }
        }
    }

    // The file can live next to the binary, in Config, or at the repository root: walk upwards and take
    // the first match, so both a published build and a source run find the same file.
    private static string? Locate()
    {
        var directories = new List<string>();

        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            directories.Add(baseDir);
            directories.Add(Path.Combine(baseDir, "Config"));

            var parent = new DirectoryInfo(baseDir);
            for (var depth = 0; depth < 6 && parent != null; depth++)
            {
                directories.Add(parent.FullName);
                parent = parent.Parent;
            }
        }

        directories.Add(Directory.GetCurrentDirectory());

        foreach (var directory in directories.Distinct())
        {
            var candidate = Path.Combine(directory, FileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static Dictionary<string, List<MockSquad>> Parse(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var result = new Dictionary<string, List<MockSquad>>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("boards", out var boards) || boards.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var board in boards.EnumerateObject())
        {
            var squads = new List<MockSquad>();

            switch (board.Value.ValueKind)
            {
                case JsonValueKind.Array:
                    squads.AddRange(ReadSquads(board.Value));
                    break;

                case JsonValueKind.Object:
                    // A board can be split by difficulty; for the leaderboard the union is what matters.
                    foreach (var difficulty in board.Value.EnumerateObject())
                    {
                        if (difficulty.Value.ValueKind == JsonValueKind.Array)
                            squads.AddRange(ReadSquads(difficulty.Value));
                    }
                    break;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    break;
            }

            result[board.Name] = squads;
        }

        return result;
    }

    private static IEnumerable<MockSquad> ReadSquads(JsonElement array)
    {
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var strikers = ReadIds(entry, "strikers");
            var specials = ReadIds(entry, "specials");

            // A squad needs at least one striker to be renderable; supports are optional for PvP.
            if (strikers.Count == 0)
                continue;

            yield return new MockSquad(strikers, specials);
        }
    }

    private static List<long> ReadIds(JsonElement entry, string property)
    {
        var result = new List<long>();
        if (!entry.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var id) && id > 0)
                result.Add(id);
        }

        return result;
    }
}