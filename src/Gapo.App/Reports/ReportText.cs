using System.Globalization;
using System.Text;

namespace Gapo.App.Reports;

public static class ReportText
{
    public const string FolderName = "GAPO";

    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FolderName);

    public static string Save(ReportView report, string? sourceJsonPath, string folder, DateTimeOffset at)
    {
        Directory.CreateDirectory(folder);
        var baseName = "GAPO report " + at.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        var textPath = Path.Combine(folder, baseName + ".txt");
        File.WriteAllText(textPath, Compose(report, at), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (sourceJsonPath is not null && File.Exists(sourceJsonPath))
        {
            File.Copy(sourceJsonPath, Path.Combine(folder, baseName + ".json"), overwrite: true);
        }

        return textPath;
    }

    public static string Compose(ReportView report, DateTimeOffset at)
    {
        var text = new StringBuilder();
        Line(text, "GAPO report, " + at.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture));
        Line(text, report.OverheadText);
        if (report.HasResult)
        {
            Section(text, "Result");
            Line(text, report.Headline + " " + report.HeadlineWord + " average FPS");
            Line(text, report.WorkLine);
            foreach (var row in report.Rows)
            {
                Line(text, string.Create(CultureInfo.InvariantCulture, $"{row.Label}: {row.Before} -> {row.After} ({row.Change})"));
            }

            Line(text, report.Sentence);
            Line(text, report.MeasurementQuality);

            Section(text, "What GAPO changed");
            foreach (var card in report.Experiments)
            {
                var decision = card.Kept ? "kept" : card.RolledBack ? "rolled back" : card.Failed ? "could not be measured" : "not decided";
                Line(text, card.Title + " (" + decision + ")");
                Line(text, "  " + card.Hypothesis);
                if (card.HasMeasurement)
                {
                    Line(text, "  " + card.Measured);
                }

                Line(text, "  " + card.Reason);
                Line(text, "  " + card.VisualCost);
            }

            List(text, "Considered but not changed", report.NotChanged);

            Section(text, "Diagnosis");
            Line(text, report.DiagnosisTitle);
            Line(text, report.DiagnosisSummary);
            Line(text, report.EngineLimitation);
            foreach (var evidence in report.Evidence.Concat(report.OtherProblems))
            {
                Line(text, "- " + evidence);
            }

            Line(text, "After the changes: " + report.BottleneckAfter);
        }

        Section(text, "Tips");
        if (report.Tips.Count == 0)
        {
            Line(text, "GAPO found nothing outside the game worth changing on this PC.");
        }

        foreach (var tip in report.Tips)
        {
            Line(text, tip.Title + " (" + tip.KindLabel + ")");
            Line(text, "  " + tip.Reason);
        }

        Section(text, "Your PC");
        Line(text, report.PcClass);
        foreach (var fact in report.Hardware)
        {
            Line(text, fact.Label + ": " + fact.Value);
        }

        List(text, "What could not be read", report.CouldNotBeRead);
        if (report.Live.Count > 0)
        {
            Section(text, "Measured while the game ran");
            foreach (var fact in report.Live)
            {
                Line(text, fact.Label + ": " + fact.Value);
            }
        }

        return text.ToString();
    }

    private static void Section(StringBuilder text, string title)
    {
        text.AppendLine();
        text.AppendLine(title);
        text.AppendLine(new string('-', title.Length));
    }

    private static void List(StringBuilder text, string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        Section(text, title);
        foreach (var line in lines)
        {
            Line(text, "- " + line);
        }
    }

    private static void Line(StringBuilder text, string line)
    {
        if (line.Length > 0)
        {
            text.AppendLine(line);
        }
    }
}
