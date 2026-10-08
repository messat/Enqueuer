namespace Enqueuer
{
    // Process exit codes. The Container Apps Job treats 0 as success and anything else as a failed execution.
    public static class ExitCodes
    {
        // Every listed repository was queued, or there was nothing to queue
        public const int Success = 0;

        // Azure DevOps or the queue could not be reached, a send failed, or something else went wrong
        public const int Failure = 1;

        // Settings are missing or invalid, or the queue is missing, so the enqueuer did not start
        public const int InvalidConfiguration = 2;
    }
}
