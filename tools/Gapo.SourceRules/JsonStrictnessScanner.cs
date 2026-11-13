using System.Text.Json;

namespace Gapo.SourceRules;

public sealed class JsonStrictnessScanner : ISourceScanner
{
    public const string Rule = "json-not-strict";

    private static readonly JsonDocumentOptions StrictJson = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public bool Handles(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<Finding> Scan(string path, string text)
    {
        var violation = FindViolation(path, text);
        if (violation is not null)
        {
            yield return violation.Value;
        }
    }

    private static Finding? FindViolation(string path, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, StrictJson);
            return null;
        }
        catch (JsonException exception)
        {
            var line = (int)(exception.LineNumber ?? 0) + 1;
            return Finding.At(path, line, Rule, exception.Message);
        }
    }
}
