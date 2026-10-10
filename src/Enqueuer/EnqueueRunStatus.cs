namespace Enqueuer
{
    // How one enqueue run ended, as the summary line says it (Issue 4 design, The summary line). Logged by name, so a
    // search can filter on it.
    public enum EnqueueRunStatus
    {
        // The run got to the end: every repository chosen to queue was sent, which may be none (Issue 4 design, decision 8)
        Completed,

        // The run stopped on an error: the listing was refused or failed, the queue was missing or a send failed, or a bug
        Failed,

        // The job was asked to stop, Ctrl+C locally or SIGTERM in Azure, and the run stopped at its next call (Issue 4 design, decision 12)
        Stopped
    }
}
