using System;

namespace Enqueuer.AzureDevOps
{
    // Azure DevOps refused the PAT: it is wrong, expired or revoked, or lacks Code (Read). Nothing can be listed, so
    // Program exits 1 with one line (Issue 2 design, decision 7).
    public sealed class AzureDevOpsAuthenticationException : Exception
    {
        public AzureDevOpsAuthenticationException(string message)
            : base(message)
        {
        }
    }
}
