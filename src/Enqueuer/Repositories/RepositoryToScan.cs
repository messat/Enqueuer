using System;

namespace Enqueuer.Repositories
{
    // A repository a scan request will go to: the four fields the request needs from it (Issue 3). DefaultBranch has
    // no refs/heads/ prefix, which the scanner refuses (decision 13).
    public sealed record RepositoryToScan(Guid Id, string ProjectName, string Name, string DefaultBranch);
}
