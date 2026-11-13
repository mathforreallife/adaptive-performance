namespace Gapo.Core.FrameTiming;

public sealed class GpuBusyTracker
{
    private const int QueueGrowth = 16;

    private readonly Dictionary<ulong, ulong> adapterOfDevice = [];
    private readonly Dictionary<(ulong Adapter, uint Node), Node> nodes = [];
    private readonly Dictionary<ulong, Context> contexts = [];
    private readonly Dictionary<int, ProcessWork> work = [];

    public long BusyTicks(int processId) => work.TryGetValue(processId, out var process) ? process.AccumulatedTicks : 0;

    public int KnownContexts => contexts.Count;

    public void DeviceStarted(ulong device, ulong adapter) => adapterOfDevice[device] = adapter;

    public void DeviceStopped(ulong device) => adapterOfDevice.Remove(device);

    public void ContextStarted(ulong context, ulong device, uint nodeOrdinal, int processId)
    {
        if (!adapterOfDevice.TryGetValue(device, out var adapter))
        {
            return;
        }

        var key = (adapter, nodeOrdinal);
        if (!nodes.TryGetValue(key, out var node))
        {
            node = new Node();
            nodes[key] = node;
        }

        if (!work.TryGetValue(processId, out var process))
        {
            process = new ProcessWork();
            work[processId] = process;
        }

        contexts[context] = new Context(node, process);
    }

    public void ContextStopped(ulong context) => contexts.Remove(context);

    public void PacketQueued(ulong context, uint sequence, long timestamp)
    {
        if (sequence == 0 || !contexts.TryGetValue(context, out var found))
        {
            return;
        }

        var node = found.Node;
        if (node.Count == node.Entries.Length)
        {
            node.Grow();
        }

        node.Entries[(node.Head + node.Count) % node.Entries.Length] = new Entry(found.Process, sequence);
        node.Count++;
        if (node.Count == 1)
        {
            Start(found.Process, timestamp);
        }
    }

    public void PacketCompleted(ulong context, uint sequence, long timestamp)
    {
        if (sequence == 0 || !contexts.TryGetValue(context, out var found))
        {
            return;
        }

        var node = found.Node;
        if (node.Count == 0 || sequence < node.Entries[node.Head].Sequence)
        {
            return;
        }

        var skipped = 0;
        if (sequence > node.Entries[node.Head].Sequence)
        {
            skipped = 1;
            while (skipped < node.Count && node.Entries[(node.Head + skipped) % node.Entries.Length].Sequence != sequence)
            {
                skipped++;
            }

            if (skipped == node.Count)
            {
                return;
            }
        }

        var running = node.Entries[node.Head].Process;
        node.Head = (node.Head + skipped + 1) % node.Entries.Length;
        node.Count -= skipped + 1;
        Finish(running, timestamp);

        if (node.Count > 0)
        {
            Start(node.Entries[node.Head].Process, timestamp);
        }
    }

    private static void Start(ProcessWork process, long timestamp)
    {
        process.Running++;
        if (process.Running == 1)
        {
            process.RunningSince = timestamp;
        }
    }

    private static void Finish(ProcessWork process, long timestamp)
    {
        if (process.Running == 0)
        {
            return;
        }

        process.Running--;
        if (process.Running == 0)
        {
            process.AccumulatedTicks += Math.Max(0, timestamp - process.RunningSince);
        }
    }

    private readonly record struct Entry(ProcessWork Process, uint Sequence);

    private sealed record Context(Node Node, ProcessWork Process);

    private sealed class ProcessWork
    {
        public int Running;
        public long RunningSince;
        public long AccumulatedTicks;
    }

    private sealed class Node
    {
        public Entry[] Entries = new Entry[QueueGrowth];
        public int Head;
        public int Count;

        public void Grow()
        {
            var larger = new Entry[Entries.Length + QueueGrowth];
            for (var index = 0; index < Count; index++)
            {
                larger[index] = Entries[(Head + index) % Entries.Length];
            }

            Entries = larger;
            Head = 0;
        }
    }
}
