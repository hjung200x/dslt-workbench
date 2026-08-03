using Dslt.Managed.Core.Models;

namespace Dslt.App.ViewModels;

public sealed record OperationOption(string Name, ProcessingOperation Operation)
{
    public override string ToString() => Name;
}

