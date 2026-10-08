using System;

namespace Enqueuer.AzureDevOps
{
    // Azure DevOps could not list the repositories for any reason other than a refused PAT: the network, a timeout,
    // another status, or an answer that is not a repository list. Program exits 1 with one line (decision 8).
    public sealed class AzureDevOpsUnavailableException : Exception
    {
        public AzureDevOpsUnavailableException(string message, Exception? innerException)
            : base(message, innerException)
        {
        }
    }
}
