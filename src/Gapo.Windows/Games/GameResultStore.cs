using System.Text.Json;

namespace Gapo.Windows.Games;

public sealed record GameResult(double BeforeFps, double AfterFps, DateTimeOffset When);

public sealed class GameResultStore
{
    private readonly string path;
    private readonly Dictionary<string, GameResult> results = new(StringComparer.Ordinal);

    public GameResultStore(string path)
    {
        this.path = path;
        Load();
    }

    public GameResult? For(string gameKey) => results.GetValueOrDefault(gameKey);

    public void Record(string gameKey, double beforeFps, double afterFps)
    {
        results[gameKey] = new GameResult(beforeFps, afterFps, DateTimeOffset.Now);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var game in document.RootElement.EnumerateObject())
            {
                results[game.Name] = new GameResult(
                    game.Value.GetProperty("beforeFps").GetDouble(),
                    game.Value.GetProperty("afterFps").GetDouble(),
                    game.Value.GetProperty("when").GetDateTimeOffset());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            results.Clear();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            foreach (var (game, result) in results)
            {
                json.WriteStartObject(game);
                json.WriteNumber("beforeFps", Math.Round(result.BeforeFps, 1));
                json.WriteNumber("afterFps", Math.Round(result.AfterFps, 1));
                json.WriteString("when", result.When);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        File.Move(temporary, path, overwrite: true);
    }
}
