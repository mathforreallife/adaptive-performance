using System.Collections.Immutable;
using System.Globalization;

namespace Gapo.Core.Optimisation;

public sealed class GameSettings
{
    private readonly ImmutableSortedDictionary<string, string> values;

    public GameSettings(IEnumerable<KeyValuePair<string, string>> values, int revision)
    {
        this.values = values.ToImmutableSortedDictionary(StringComparer.OrdinalIgnoreCase);
        Revision = revision;
    }

    private GameSettings(ImmutableSortedDictionary<string, string> values, int revision)
    {
        this.values = values;
        Revision = revision;
    }

    public int Revision { get; }

    public IReadOnlyDictionary<string, string> Values => values;

    public string this[string key] => values.TryGetValue(key, out var value) ? value : string.Empty;

    public int Number(string key) => int.TryParse(this[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    public bool Is(string key, string value) => string.Equals(this[key], value, StringComparison.OrdinalIgnoreCase);

    public GameSettings With(IReadOnlyDictionary<string, string> changes) => new(values.SetItems(changes), Revision);

    public GameSettings WithRevision(int revision) => new(values, revision);

    public bool HasSameValuesAs(GameSettings other) =>
        values.Count == other.values.Count
        && values.All(pair => other.values.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal));

    public IReadOnlyDictionary<string, string> ValuesOf(IEnumerable<string> keys) =>
        keys.ToDictionary(key => key, key => this[key], StringComparer.OrdinalIgnoreCase);
}
