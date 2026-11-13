namespace Gapo.Windows.Processes;

public sealed class ProcessLiveness : IDisposable
{
    private nint handle;

    private ProcessLiveness(int processId, nint handle)
    {
        ProcessId = processId;
        this.handle = handle;
    }

    public int ProcessId { get; }

    public static ProcessLiveness? TryWatch(int processId)
    {
        var handle = ProcessNative.OpenProcess(
            ProcessNative.QueryLimitedInformation | ProcessNative.Synchronize,
            inheritHandle: false,
            processId);
        return handle == 0 ? null : new ProcessLiveness(processId, handle);
    }

    public bool IsRunning => handle != 0 && ProcessNative.WaitForSingleObject(handle, 0) == ProcessNative.WaitTimeout;

    public void Dispose()
    {
        if (handle != 0)
        {
            ProcessNative.CloseHandle(handle);
            handle = 0;
        }
    }
}
