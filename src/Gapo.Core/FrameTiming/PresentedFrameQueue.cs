namespace Gapo.Core.FrameTiming;

public sealed class PresentedFrameQueue
{
    public const int DefaultCapacity = 8192;

    private readonly PresentedFrame[] ring;
    private readonly Lock gate = new();
    private int head;
    private int count;

    public PresentedFrameQueue()
        : this(DefaultCapacity)
    {
    }

    public PresentedFrameQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ring = new PresentedFrame[capacity];
    }

    public long Dropped { get; private set; }

    public void Enqueue(PresentedFrame frame)
    {
        lock (gate)
        {
            if (count == ring.Length)
            {
                head = (head + 1) % ring.Length;
                count--;
                Dropped++;
            }

            ring[(head + count) % ring.Length] = frame;
            count++;
        }
    }

    public int Drain(Span<PresentedFrame> destination)
    {
        lock (gate)
        {
            var taken = Math.Min(count, destination.Length);
            for (var index = 0; index < taken; index++)
            {
                destination[index] = ring[(head + index) % ring.Length];
            }

            head = (head + taken) % ring.Length;
            count -= taken;
            return taken;
        }
    }
}
