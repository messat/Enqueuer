using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Enqueuer.Tests.Queue
{
    // A QueueClient that keeps what it is sent in memory, so no test reaches Azurite (Issue 3 design, decision 18). Cut
    // down from RepoScanner's to the one call the enqueuer makes. Azure's clients have a protected constructor and
    // virtual methods for this.
    internal sealed class FakeQueueClient : QueueClient
    {
        private readonly string _name;
        private readonly Dictionary<int, Exception> _failures = new Dictionary<int, Exception>();

        public FakeQueueClient(string name)
        {
            _name = name;
        }

        public override string Name
        {
            get { return _name; }
        }

        // The sends that succeeded, in order
        public List<(string Text, TimeSpan? VisibilityTimeout, TimeSpan? TimeToLive)> Sent { get; } = new List<(string Text, TimeSpan? VisibilityTimeout, TimeSpan? TimeToLive)>();

        // Every send tried, including those that failed
        public int Attempts { get; private set; }

        // Makes that attempt, counted from 1, throw the exception instead of sending
        public void FailSend(int attempt, Exception exception)
        {
            _failures[attempt] = exception;
        }

        // Registered after Program's own QueueClient, so it wins
        public void ReplaceQueue(IServiceCollection services)
        {
            services.AddSingleton<QueueClient>(this);
        }

        // The overload ScanRequestSender calls. Any other would reach the real client, which this fake never set up.
        public override Task<Response<SendReceipt>> SendMessageAsync(
            string messageText, TimeSpan? visibilityTimeout = null, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (_failures.TryGetValue(Attempts, out Exception? failure))
            {
                throw failure;
            }

            Sent.Add((messageText, visibilityTimeout, timeToLive));
            SendReceipt receipt = QueuesModelFactory.SendReceipt(
                "sent-" + Sent.Count, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "receipt", DateTimeOffset.UtcNow);
            return Task.FromResult(Response.FromValue(receipt, new FakeResponse(201)));
        }
    }
}
