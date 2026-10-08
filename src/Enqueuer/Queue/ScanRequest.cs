using System;
using Enqueuer.Repositories;

namespace Enqueuer.Queue
{
    // One scan request, in the format the scanner reads (RepoScanner Issue 7 design, Message format). The enqueuer's
    // own copy (ENQ-ADR-001), kept in step with the scanner's by the contract test on
    // docs/contracts/scan-request.example.json.
    public sealed record ScanRequest(Guid CorrelationId, Guid RepositoryId, string ProjectName, string RepositoryName, string DefaultBranch)
    {
        // The only version the scanner reads. Raised only when an older scanner could not read the format safely.
        public const int SchemaVersion = 1;

        // The request for one repository Issue 2 chose, with the correlation ID the caller made: the builder makes
        // none, so the contract test can rebuild the example exactly (Issue 3 design, decision 4)
        public static ScanRequest For(RepositoryToScan repository, Guid correlationId)
        {
            return new ScanRequest(correlationId, repository.Id, repository.ProjectName, repository.Name, repository.DefaultBranch);
        }
    }
}
