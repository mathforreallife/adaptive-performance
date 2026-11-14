using System.Globalization;
using System.Text.Json;

namespace Gapo.App.Reports;

public sealed record ComparisonRow(string Label, string Before, string After, string Change, bool Improved)
{
    public bool Worsened => !Improved;
}

public sealed record ExperimentCard(
    string Title,
    string Hypothesis,
    string Measured,
    string Reason,
    string VisualCost,
    bool Kept,
    bool RolledBack,
    bool Failed)
{
    public bool HasMeasurement => Measured.Length > 0;
}

public sealed record FactLine(string Label, string Value);

public sealed record TipCard(string Title, string Reason, string KindLabel, string SettingsPage, string ButtonText)
{
    public bool HasButton => SettingsPage.Length > 0;
}

public sealed class ReportView
{
    private const string Arrow = " → ";

    public bool HasResult { get; private init; }

    public string Headline { get; private init; } = string.Empty;

    public string HeadlineWord { get; private init; } = string.Empty;

    public string WorkLine { get; private init; } = string.Empty;

    public string Sentence { get; private init; } = string.Empty;

    public string MeasurementQuality { get; private init; } = string.Empty;

    public string OverheadText { get; private init; } = string.Empty;

    public bool OverBudget { get; private init; }

    public string DiagnosisTitle { get; private init; } = string.Empty;

    public string DiagnosisSummary { get; private init; } = string.Empty;

    public string BottleneckAfter { get; private init; } = string.Empty;

    public string EngineLimitation { get; private init; } = string.Empty;

    public string PcClass { get; private init; } = string.Empty;

    public IReadOnlyList<ComparisonRow> Rows { get; private init; } = [];

    public IReadOnlyList<ExperimentCard> Experiments { get; private init; } = [];

    public IReadOnlyList<string> NotChanged { get; private init; } = [];

    public IReadOnlyList<string> Evidence { get; private init; } = [];

    public IReadOnlyList<string> OtherProblems { get; private init; } = [];

    public IReadOnlyList<FactLine> Hardware { get; private init; } = [];

    public IReadOnlyList<string> CouldNotBeRead { get; private init; } = [];

    public IReadOnlyList<FactLine> Live { get; private init; } = [];

    public IReadOnlyList<TipCard> Tips { get; private init; } = [];

    public bool HasNoTips => Tips.Count == 0;

    public static ReportView Empty { get; } = new();

    public static ReportView Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("optimisation", out var optimisation) || !optimisation.GetProperty("completed").GetBoolean())
        {
            return new ReportView { OverheadText = Overhead(root), Hardware = HardwareFacts(root), Tips = TipCards(root) };
        }

        var baseline = optimisation.GetProperty("baseline");
        var final = optimisation.GetProperty("final");
        var gain = optimisation.GetProperty("fpsGainPercent").GetDouble();
        var kept = optimisation.GetProperty("kept").GetInt32();
        var rolledBack = optimisation.GetProperty("rolledBack").GetInt32();
        var diagnosis = root.TryGetProperty("diagnosis", out var found) ? found : default;
        return new ReportView
        {
            HasResult = true,
            Headline = Text($"{gain:+0;-0;0} %"),
            HeadlineWord = gain >= 0 ? "faster" : "slower",
            WorkLine = Text($"{optimisation.GetProperty("workReductionFactor").GetDouble():0.0}× less work per frame"),
            Sentence = Summarise(kept, rolledBack, optimisation.GetProperty("pacingNote").GetString() ?? string.Empty),
            MeasurementQuality = Text($"Measured on the same scene in {optimisation.GetProperty("measurementWindows").GetInt32()} windows over {optimisation.GetProperty("durationSeconds").GetDouble():0} s. Noise floor ±{optimisation.GetProperty("noiseFloorPercent").GetDouble():0.0} %."),
            OverheadText = Overhead(root),
            OverBudget = !root.GetProperty("withinBudget").GetBoolean(),
            Rows =
            [
                Row("Average FPS", baseline, final, "averageFps", "0", higherIsBetter: true),
                Row("Frame work (ms)", baseline, final, "medianBusyMs", "0.0", higherIsBetter: false),
                Row("Slowest frames, 95th percentile (ms)", baseline, final, "percentile95FrameMs", "0.0", higherIsBetter: false),
                Row("Stutters per minute", baseline, final, "stuttersPerMinute", "0.0", higherIsBetter: false),
            ],
            Experiments = optimisation.GetProperty("experiments").EnumerateArray().Select(Card).ToList(),
            NotChanged = Strings(optimisation.GetProperty("notTouched")),
            DiagnosisTitle = String(diagnosis, "title"),
            DiagnosisSummary = String(diagnosis, "summary"),
            EngineLimitation = String(diagnosis, "engineLimitation"),
            BottleneckAfter = optimisation.GetProperty("bottleneckAfter").GetString() ?? string.Empty,
            Evidence = diagnosis.ValueKind == JsonValueKind.Object ? Strings(diagnosis.GetProperty("evidence")) : [],
            OtherProblems = diagnosis.ValueKind == JsonValueKind.Object ? Strings(diagnosis.GetProperty("secondaryProblems")) : [],
            Hardware = HardwareFacts(root),
            PcClass = PcClassLine(root),
            CouldNotBeRead = root.TryGetProperty("hardware", out var hardware) ? Strings(hardware.GetProperty("couldNotBeRead")) : [],
            Live = LiveFacts(root),
            Tips = TipCards(root),
        };
    }

    private static string Summarise(int kept, int rolledBack, string pacingNote)
    {
        var changes = kept == 1 ? "1 change" : Text($"{kept} changes");
        var rollbacks = rolledBack == 0 ? "none had to be rolled back" : Text($"rolled back {rolledBack} that did not prove itself");
        return Text($"Kept {changes}, {rollbacks}. {pacingNote}");
    }

    private static ComparisonRow Row(string label, JsonElement baseline, JsonElement final, string property, string format, bool higherIsBetter)
    {
        var before = baseline.GetProperty(property).GetDouble();
        var after = final.GetProperty(property).GetDouble();
        var improved = higherIsBetter ? after >= before : after <= before;
        var change = before == 0 ? (after == 0 ? "no change" : "new") : Text($"{(after - before) / before * 100:+0;-0;0} %");
        return new ComparisonRow(label, before.ToString(format, CultureInfo.InvariantCulture), after.ToString(format, CultureInfo.InvariantCulture), change, improved);
    }

    private static ExperimentCard Card(JsonElement experiment)
    {
        var decision = experiment.GetProperty("decision").GetString();
        var before = experiment.GetProperty("frameWorkBeforeMs").GetDouble();
        var after = experiment.GetProperty("frameWorkAfterMs").GetDouble();
        var isThroughput = experiment.GetProperty("kind").GetString() is "Throughput" or "QualityRestore";
        var measured = isThroughput && after > 0
            ? Text($"Frame work {before:0.0}{Arrow}{after:0.0} ms ({experiment.GetProperty("gainPercent").GetDouble():+0;-0;0} %). Predicted saving {experiment.GetProperty("predictedSavingMs").GetDouble():0.0} ms, measured {experiment.GetProperty("measuredSavingMs").GetDouble():0.0} ms.")
            : string.Empty;
        var visualLoss = Math.Max(0, experiment.GetProperty("visualLoss").GetDouble());
        return new ExperimentCard(
            experiment.GetProperty("title").GetString() ?? string.Empty,
            experiment.GetProperty("hypothesis").GetString() ?? string.Empty,
            measured,
            experiment.GetProperty("reason").GetString() ?? string.Empty,
            Text($"Visual cost {visualLoss:0} of 10"),
            decision == "Kept",
            decision == "RolledBack",
            decision == "Failed");
    }

    private static string Overhead(JsonElement root) =>
        Text($"GAPO itself: {root.GetProperty("runAverageCpuPercent").GetDouble():0.00} % CPU, {root.GetProperty("peakPrivateMegabytes").GetDouble():0} MB");

    private static IReadOnlyList<FactLine> HardwareFacts(JsonElement root)
    {
        if (!root.TryGetProperty("hardware", out var hardware))
        {
            return [];
        }

        var processor = hardware.GetProperty("processor");
        var memory = hardware.GetProperty("memory");
        var display = hardware.GetProperty("display");
        var windows = hardware.GetProperty("windows");
        var power = hardware.GetProperty("power");
        var facts = new List<FactLine>
        {
            new("Processor", Text($"{processor.GetProperty("name").GetString()} · {processor.GetProperty("cores").GetInt32()} cores, {processor.GetProperty("logicalProcessors").GetInt32()} threads")),
            new("Graphics", hardware.GetProperty("primaryGraphics").GetString() ?? string.Empty),
            new("Memory", Text($"{memory.GetProperty("totalMegabytes").GetInt64() / 1024.0:0} GB · {ChannelText(memory.GetProperty("channelMode").GetString())}")),
            new("Display", Text($"{display.GetProperty("width").GetInt32()} × {display.GetProperty("height").GetInt32()} at {display.GetProperty("effectiveRefreshHz").GetInt32()} Hz{(display.GetProperty("refreshIsKnown").GetBoolean() ? string.Empty : " (assumed)")}")),
        };
        facts.AddRange(hardware.GetProperty("storage").EnumerateArray().Select(drive => new FactLine(
            Text($"Storage {drive.GetProperty("drive").GetString()}"),
            DriveText(drive))));
        facts.Add(new FactLine("Power", power.GetProperty("hasBattery").GetBoolean()
            ? (power.GetProperty("onMainsPower").GetBoolean() ? "Laptop on mains power" : "Laptop on battery") + Text($" · mode {power.GetProperty("mode").GetString()}")
            : Text($"Desktop · mode {power.GetProperty("mode").GetString()}")));
        facts.Add(new FactLine("Windows", Text($"{windows.GetProperty("name").GetString()} {windows.GetProperty("version").GetString()} (build {windows.GetProperty("build").GetInt32()})")));
        return facts;
    }

    private static string PcClassLine(JsonElement root) =>
        root.TryGetProperty("hardware", out var hardware)
            ? Text($"{hardware.GetProperty("class").GetString()} PC: {hardware.GetProperty("classReason").GetString()}")
            : string.Empty;

    private static IReadOnlyList<FactLine> LiveFacts(JsonElement root)
    {
        if (!root.TryGetProperty("monitor", out var monitor))
        {
            return [];
        }

        return
        [
            new("Game CPU use", Text($"{monitor.GetProperty("meanGameProcessorsUsed").GetDouble():0.00} processors")),
            new("Busiest game thread", Text($"{monitor.GetProperty("medianBusiestGameThreadPercent").GetDouble():0} % of one processor")),
            new("Whole PC", Text($"{monitor.GetProperty("meanMachineLoadPercent").GetDouble():0} % CPU, busiest processor {monitor.GetProperty("meanBusiestProcessorPercent").GetDouble():0} %")),
            new("Background apps", Text($"{monitor.GetProperty("meanBackgroundPercent").GetDouble():0.0} % CPU")),
            new("Game memory", Text($"{monitor.GetProperty("gamePrivateMegabytes").GetDouble():0} MB")),
            new("Memory in use", Text($"{monitor.GetProperty("memoryLoadPercent").GetInt32()} %")),
            new("Peak game disk writes", Text($"{monitor.GetProperty("peakGameWriteMegabytesPerSecond").GetDouble():0.0} MB/s")),
            new("GAPO CPU", Text($"{root.GetProperty("runAverageCpuPercent").GetDouble():0.00} % average, {root.GetProperty("measurementWindowCpuPercent").GetDouble():0.00} % while measuring (budget {root.GetProperty("budgetCpuPercent").GetDouble():0.0} %)")),
            new("GAPO memory", Text($"{root.GetProperty("peakPrivateMegabytes").GetDouble():0} MB (budget {root.GetProperty("budgetPrivateMegabytes").GetDouble():0} MB)")),
        ];
    }

    private static string DriveText(JsonElement drive)
    {
        var description = string.Join(' ', new[] { MediaText(drive.GetProperty("media").GetString()), drive.GetProperty("bus").GetString(), drive.GetProperty("model").GetString() }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return Text($"{description} · {drive.GetProperty("freeMegabytes").GetInt64() / 1024.0:0} GB free");
    }

    private static string ChannelText(string? mode) => mode switch
    {
        "Single" => "single-channel",
        "Multiple" => "dual-channel or better",
        "Soldered" => "soldered",
        "Inconsistent" => "module data inconsistent",
        _ => "modules not reported",
    };

    private static string MediaText(string? media) => media switch
    {
        "SolidState" => "SSD",
        "HardDisk" => "Hard disk",
        _ => "Drive",
    };

    public static IReadOnlyList<TipCard> TipCards(JsonElement root)
    {
        if (!root.TryGetProperty("tips", out var tips) || tips.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return tips.EnumerateArray().Select(tip => new TipCard(
            String(tip, "title"),
            String(tip, "reason"),
            String(tip, "kind") switch
            {
                "HardwareUpgrade" => "Hardware upgrade",
                "WindowsSetting" => "Windows setting",
                _ => "What you can do",
            },
            String(tip, "settingsPage"),
            String(tip, "buttonText"))).ToList();
    }

    private static IReadOnlyList<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(text => text.Length > 0).ToList();

    private static string String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string Text(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
