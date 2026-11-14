using Gapo.Core.Optimisation;

namespace Gapo.Core.Protocol;

public enum AgentState
{
    NoGame,
    Watching,
    Optimising,
    Finished,
    Failed,
}

public abstract record AgentMessage;

public sealed record StatusMessage(AgentState State, string Title, string Detail) : AgentMessage;

public sealed record LiveSampleMessage(
    bool GameConnected,
    string GameName,
    double AverageFps,
    double MedianFrameMs,
    double MedianBusyMs,
    double StuttersPerMinute,
    double GameProcessorsUsed,
    double MachineLoadPercent,
    double OwnCpuPercent,
    double OwnMegabytes,
    bool OwnUseIsOverBudget,
    IReadOnlyList<double> RecentFrameMs,
    bool CanOptimise = true,
    int GameProcessId = 0,
    double BeforeFps = 0,
    double AfterFps = 0) : AgentMessage;

public sealed record ReportReadyMessage(string Path) : AgentMessage;

public sealed record GameSettingsMessage(string GameName, bool CanChange, bool AppliesAtRestart, string Note, IReadOnlyList<SettingState> Settings) : AgentMessage;

public sealed record ProposalMessage(string Title, string Detail) : AgentMessage;

public sealed record PlanMessage(string GameName, bool CanStart, string Summary, IReadOnlyList<string> Steps, int SecondsLow, int SecondsHigh, int Restarts) : AgentMessage;

public sealed record ProgressMessage(string Phase, int StepsDone, int StepsExpected, int ElapsedSeconds, int RemainingSecondsLow, int RemainingSecondsHigh) : AgentMessage;

public sealed record DiagnosisMessage(string GameName, string Bottleneck, string Title, string Summary, IReadOnlyList<string> AlsoNoticed, string Basis) : AgentMessage;

public abstract record UiCommand;

public sealed record OptimiseCommand(Goal Goal) : UiCommand;

public sealed record RevertAllCommand : UiCommand;

public sealed record StopCommand : UiCommand;

public sealed record LockSettingCommand(string Key, bool Locked) : UiCommand;

public sealed record SetSettingCommand(string Key, string Value) : UiCommand;

public sealed record RevertSettingCommand(string Key) : UiCommand;

public sealed record AnswerProposalCommand(bool Approved) : UiCommand;

public sealed record AgentPreferencesCommand(bool AskBeforeEachChange, bool AllowFrameCap, int TargetFps, int MostRestarts, bool OptimiserEnabled = true, bool DiagnoseAutomatically = true) : UiCommand;

public sealed record PlanCommand(Goal Goal) : UiCommand;

public sealed record DiagnoseCommand : UiCommand;
