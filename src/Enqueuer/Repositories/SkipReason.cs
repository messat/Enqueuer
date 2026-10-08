namespace Enqueuer.Repositories
{
    // Why a listed repository gets no scan request (Issue 2 design, decision 10). Logged by name, so Issue 4 can
    // count skips by reason.
    public enum SkipReason
    {
        // Switched off in Azure DevOps: its code cannot be read
        Disabled,

        // Azure DevOps is working on it, and it may not clone
        InMaintenance,

        // No default branch: the scanner would fail its clone and poison the message every night
        Empty
    }
}
