using System.Collections.Generic;

namespace Enqueuer.Repositories
{
    // Every listed repository, in one list or the other, each in the order Azure DevOps gave
    public sealed record RepositorySelection(IReadOnlyList<RepositoryToScan> ToScan, IReadOnlyList<SkippedRepository> Skipped);
}
