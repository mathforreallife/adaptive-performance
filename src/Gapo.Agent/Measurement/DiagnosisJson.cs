using System.Text.Json;
using Gapo.Core.Diagnosis;

namespace Gapo.Agent.Measurement;

public static class DiagnosisJson
{
    private const int DecimalPlaces = 3;

    public static void Write(Utf8JsonWriter json, BottleneckDiagnosis diagnosis)
    {
        json.WriteStartObject("diagnosis");
        json.WriteString("primary", diagnosis.Primary.ToString());
        json.WriteString("title", diagnosis.Title);
        json.WriteString("summary", diagnosis.Summary);
        json.WriteString("category", diagnosis.Category.ToString());
        json.WriteString("engineLimitation", diagnosis.EngineLimitation ?? string.Empty);
        json.WriteNumber("targetFps", Round(diagnosis.TargetFps));
        json.WriteNumber("budgetMs", Round(diagnosis.BudgetMs));
        json.WriteNumber("frameWorkMs", Round(diagnosis.FrameWorkMs));
        json.WriteNumber("requiredSavingMs", Round(diagnosis.RequiredSavingMs));
        json.WriteStartArray("stages");
        foreach (var stage in diagnosis.Stages)
        {
            json.WriteStartObject();
            json.WriteString("name", stage.Name);
            json.WriteNumber("milliseconds", Round(stage.Milliseconds));
            json.WriteNumber("share", Round(stage.ShareOfFrameWork));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        WriteSentences(json, "evidence", diagnosis.Evidence);
        WriteSentences(json, "secondaryProblems", diagnosis.SecondaryProblems);
        json.WriteEndObject();
    }

    private static void WriteSentences(Utf8JsonWriter json, string name, IReadOnlyList<string> sentences)
    {
        json.WriteStartArray(name);
        foreach (var sentence in sentences)
        {
            json.WriteStringValue(sentence);
        }

        json.WriteEndArray();
    }

    private static double Round(double value) => Math.Round(value, DecimalPlaces);
}
