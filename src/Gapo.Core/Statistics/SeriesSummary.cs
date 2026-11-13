namespace Gapo.Core.Statistics;

public sealed class SeriesSummary
{
    public const int DefaultCapacity = 24_000;

    private readonly double[] values;
    private double sum;

    public SeriesSummary()
        : this(DefaultCapacity)
    {
    }

    public SeriesSummary(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        values = new double[capacity];
    }

    public int Count { get; private set; }

    public long Overflowed { get; private set; }

    public double Mean => Count == 0 ? 0 : sum / Count;

    public void Clear()
    {
        Count = 0;
        Overflowed = 0;
        sum = 0;
    }

    public void Add(double value)
    {
        if (Count == values.Length)
        {
            Overflowed++;
            return;
        }

        values[Count++] = value;
        sum += value;
    }

    public double Median() => Percentile(0.5);

    public double Percentile(double quantile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantile, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quantile, 1);
        if (Count == 0)
        {
            return 0;
        }

        var sorted = values.AsSpan(0, Count).ToArray();
        Array.Sort(sorted);
        var position = quantile * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(sorted.Length - 1, lower + 1);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }
}
