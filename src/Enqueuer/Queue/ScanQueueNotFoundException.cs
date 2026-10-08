using System;

namespace Enqueuer.Queue
{
    // The queue does not exist: fixed by creating it or correcting Enqueuer:Queue:Name, so Program exits 2 (Issue 3
    // design, decision 10). The enqueuer never creates queues: in Azure its identity may only add messages.
    public sealed class ScanQueueNotFoundException : Exception
    {
        public ScanQueueNotFoundException(string queueName, Exception innerException)
            : base("The queue " + queueName + " does not exist. Create it, or correct Enqueuer:Queue:Name.", innerException)
        {
        }
    }
}
