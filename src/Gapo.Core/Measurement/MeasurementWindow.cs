namespace Gapo.Core.Measurement;

public sealed record MeasurementWindowOptions
{
    public double SettlingDiscardMs { get; init; } = 650;

    public double MinimumSpanMs { get; init; } = 2600;

    public int MinimumFrames { get; init; } = 45;

    public double GiveUpAfterMs { get; init; } = 7000;

    public int FewestUsableFrames { get; init; } = 8;
}

public enum MeasurementWindowState
{
    Collecting,
    Complete,
    Failed,
}

public sealed class MeasurementWindow
{
    private readonly MeasurementWindowOptions options;
    private readonly int expectedRevision;
    private readonly double measureFromMs;
    private readonly double giveUpAtMs;
    private readonly List<MeasuredFrame> frames = new(256);

    public MeasurementWindow(MeasurementWindowOptions options, int expectedRevision, double startedAtGameTimeMs)
    {
        this.options = options;
        this.expectedRevision = expectedRevision;
        measureFromMs = startedAtGameTimeMs + options.SettlingDiscardMs;
        giveUpAtMs = measureFromMs + options.GiveUpAfterMs;
    }

    public MeasurementWindowState State { get; private set; } = MeasurementWindowState.Collecting;

    public string FailureReason { get; private set; } = string.Empty;

    public bool WasCutShort { get; private set; }

    public int FramesFromOtherRevisions { get; private set; }

    public int WarmupFramesSkipped { get; private set; }

    public IReadOnlyList<MeasuredFrame> Frames => frames;

    public double SpanMs => frames.Count < 2 ? 0 : frames[^1].EndTimeMs - frames[0].EndTimeMs;

    public void Offer(in MeasuredFrame frame)
    {
        if (State != MeasurementWindowState.Collecting || frame.EndTimeMs < measureFromMs)
        {
            return;
        }

        if (frame.SettingsRevision != expectedRevision)
        {
            FramesFromOtherRevisions++;
        }
        else if (frame.IsWarmup)
        {
            WarmupFramesSkipped++;
        }
        else
        {
            frames.Add(frame);
        }

        if (SpanMs >= options.MinimumSpanMs && frames.Count >= options.MinimumFrames)
        {
            State = MeasurementWindowState.Complete;
        }
        else if (frame.EndTimeMs >= giveUpAtMs)
        {
            GiveUp();
        }
    }

    public void Advance(double gameTimeNowMs)
    {
        if (State == MeasurementWindowState.Collecting && gameTimeNowMs >= giveUpAtMs)
        {
            GiveUp();
        }
    }

    public FrameStatistics Summarise() => FrameStatistics.From(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(frames));

    private void GiveUp()
    {
        if (frames.Count >= options.FewestUsableFrames)
        {
            WasCutShort = true;
            State = MeasurementWindowState.Complete;
            return;
        }

        State = MeasurementWindowState.Failed;
        FailureReason = $"Too few frames were measured ({frames.Count}).";
    }
}
