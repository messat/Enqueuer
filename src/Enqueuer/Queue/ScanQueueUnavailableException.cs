using System;

namespace Enqueuer.Queue
{
    // A send still failed after the SDK's retries: the queue could not be reached, or refused the identity. The run
    // stops there and Program exits 1 (Issue 3 design, decisions 12 and 13).
    public sealed class ScanQueueUnavailableException : Exception
    {
        public ScanQueueUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
