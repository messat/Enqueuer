using System;

namespace Enqueuer.AzureDevOps
{
    // One repository as Azure DevOps lists it: only the fields the enqueuer reads. DefaultBranch is as Azure DevOps
    // gives it, such as refs/heads/main, and null for an empty repository (Issue 2 design, decision 10).
    public sealed record AzureDevOpsRepository(Guid Id, string Name, string ProjectName, string? DefaultBranch, bool IsDisabled, bool IsInMaintenance);
}
