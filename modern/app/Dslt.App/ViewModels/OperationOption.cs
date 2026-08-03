using Dslt.Managed.Core.Models;

namespace Dslt.App.ViewModels;

public enum WorkflowStage
{
    Inspect,
    Process,
    Segment,
    Edit,
    Export,
}

public sealed record OperationOption(string Name, ProcessingOperation Operation, WorkflowStage Stage)
{
    public override string ToString() => Name;
}
