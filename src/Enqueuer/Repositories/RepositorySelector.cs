using System;
using System.Collections.Generic;
using Enqueuer.AzureDevOps;

namespace Enqueuer.Repositories
{
    // Which listed repositories to scan, and why the others are skipped (Issue 2 design, decisions 10 to 13). Logs
    // nothing: the orchestrator logs what it returns.
    public static class RepositorySelector
    {
        // How Azure DevOps names a branch; the scanner wants the branch name alone
        private const string BranchPrefix = "refs/heads/";

        public static RepositorySelection Select(IReadOnlyList<AzureDevOpsRepository> repositories)
        {
            List<RepositoryToScan> toScan = new List<RepositoryToScan>();
            List<SkippedRepository> skipped = new List<SkippedRepository>();

            foreach (AzureDevOpsRepository repository in repositories)
            {
                string branch = BranchName(repository.DefaultBranch);
                SkipReason? reason = ReasonToSkip(repository, branch);

                if (reason.HasValue)
                {
                    skipped.Add(new SkippedRepository(repository.ProjectName, repository.Name, reason.Value));
                }
                else
                {
                    // Forks are scanned like any other repository (decision 12)
                    toScan.Add(new RepositoryToScan(repository.Id, repository.ProjectName, repository.Name, branch));
                }
            }

            return new RepositorySelection(toScan, skipped);
        }

        // In the design's order: the first that applies is the one reason (decision 10)
        private static SkipReason? ReasonToSkip(AzureDevOpsRepository repository, string branch)
        {
            if (repository.IsDisabled)
            {
                return SkipReason.Disabled;
            }

            if (repository.IsInMaintenance)
            {
                return SkipReason.InMaintenance;
            }

            // Azure DevOps gives an empty repository no default branch. A size of 0 does not mean empty (decision 11).
            if (string.IsNullOrWhiteSpace(branch))
            {
                return SkipReason.Empty;
            }

            return null;
        }

        // refs/heads/main becomes main. Only a leading prefix, matching case exactly as git does (decision 13).
        private static string BranchName(string? defaultBranch)
        {
            if (defaultBranch == null)
            {
                return string.Empty;
            }

            return defaultBranch.StartsWith(BranchPrefix, StringComparison.Ordinal)
                ? defaultBranch.Substring(BranchPrefix.Length)
                : defaultBranch;
        }
    }
}
