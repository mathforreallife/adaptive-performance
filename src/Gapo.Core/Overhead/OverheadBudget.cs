namespace Gapo.Core.Overhead;

public enum OptimiserState
{
    AgentIdle,
    GameRunning,
    DashboardOpen,
    Optimising,
    MeasurementWindow,
}

public sealed record OverheadBudget(double MaximumCpuPercent, double MaximumPrivateMegabytes)
{
    public static OverheadBudget For(OptimiserState state) => state switch
    {
        OptimiserState.AgentIdle => new OverheadBudget(0.1, 30),
        OptimiserState.GameRunning => new OverheadBudget(0.3, 40),
        OptimiserState.DashboardOpen => new OverheadBudget(0.5, 80),
        OptimiserState.Optimising => new OverheadBudget(1.0, 100),
        OptimiserState.MeasurementWindow => new OverheadBudget(0.3, 100),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown optimiser state."),
    };
}
