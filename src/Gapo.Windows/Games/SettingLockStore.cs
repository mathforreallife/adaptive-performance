using System.Text.Json;

namespace Gapo.Windows.Games;

public sealed class SettingLockStore
{
    private readonly string path;
    private readonly Dictionary<string, SortedSet<string>> locks = new(StringComparer.Ordinal);

    public SettingLockStore(string path)
    {
        this.path = path;
        Load();
    }

    public IReadOnlySet<string> LockedFor(string gameKey) =>
        locks.TryGetValue(gameKey, out var keys) ? new HashSet<string>(keys, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);

    public void SetLocked(string gameKey, string settingKey, bool locked)
    {
        if (!locks.TryGetValue(gameKey, out var keys))
        {
            keys = new SortedSet<string>(StringComparer.Ordinal);
            locks[gameKey] = keys;
        }

        var changed = locked ? keys.Add(settingKey) : keys.Remove(settingKey);
        if (changed)
        {
            Save();
        }
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
                locks[game.Name] = new SortedSet<string>(game.Value.EnumerateArray().Select(key => key.GetString() ?? string.Empty).Where(key => key.Length > 0), StringComparer.Ordinal);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            locks.Clear();
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
            foreach (var (game, keys) in locks.Where(pair => pair.Value.Count > 0))
            {
                json.WriteStartArray(game);
                foreach (var key in keys)
                {
                    json.WriteStringValue(key);
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        File.Move(temporary, path, overwrite: true);
    }
}
