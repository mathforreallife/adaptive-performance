using System.Globalization;

namespace Gapo.Core.Optimisation;

public sealed record SettingChoice(string Value, string Label);

public sealed record SettingDefinition(string Key, string Label, IReadOnlyList<SettingChoice> Choices)
{
    public bool CanBeChosen => Choices.Count > 0;

    public SettingChoice? Find(string value) =>
        Choices.FirstOrDefault(choice => SameValue(choice.Value, value));

    public string Describe(string value) => Find(value)?.Label ?? value;

    public static bool SameValue(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
        || (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(second, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            && Math.Abs(a - b) < 0.0001);
}

public sealed record SettingState(string Key, string Label, string Value, string OriginalValue, bool Locked, IReadOnlyList<SettingChoice> Choices)
{
    public bool ChangedFromOriginal => !SettingDefinition.SameValue(Value, OriginalValue);

    public static SettingState From(SettingDefinition definition, string value, string originalValue, bool locked) =>
        new(definition.Key, definition.Label, value, originalValue, locked, definition.Choices);
}

public sealed record OptimisationControls
{
    public IReadOnlySet<string> LockedSettings { get; init; } = new HashSet<string>();

    public bool AllowFrameCap { get; init; } = true;

    public double TargetFps { get; init; }

    public Func<SettingCandidate, bool>? Approve { get; init; }
}

public static class SettingLocks
{
    public static bool Touches(SettingCandidate candidate, IReadOnlySet<string> locked) =>
        locked.Count > 0 && candidate.Changes.Keys.Any(locked.Contains);

    public static string Note(IEnumerable<string> lockedLabels)
    {
        var labels = lockedLabels.ToList();
        return labels.Count == 0 ? string.Empty : "Not changed because you locked them: " + string.Join(", ", labels) + ".";
    }
}
