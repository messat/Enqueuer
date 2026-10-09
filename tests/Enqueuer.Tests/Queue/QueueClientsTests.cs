using System;
using Azure.Storage.Queues;
using Enqueuer.Configuration;
using Enqueuer.Queue;
using NUnit.Framework;

namespace Enqueuer.Tests.Queue
{
    // The client for Enqueuer:Queue (Issue 3 design, decision 8). Building one makes no network call, so neither test
    // needs Azurite or Azure.
    [TestFixture]
    public class QueueClientsTests
    {
        [Test]
        public void Create_WithAConnectionString_PointsAtTheNamedQueueInAzurite()
        {
            QueueClient client = QueueClients.Create(new QueueOptions { Name = "scan-test", ConnectionString = "UseDevelopmentStorage=true" });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(client.Uri, Is.EqualTo(new Uri("http://127.0.0.1:10001/devstoreaccount1/scan-test")));
                Assert.That(client.Name, Is.EqualTo("scan-test"));
            }
        }

        // A trailing / must not leave the queue name empty
        [TestCase("https://example.queue.core.windows.net")]
        [TestCase("https://example.queue.core.windows.net/")]
        public void Create_WithAServiceUri_PointsAtTheNamedQueueInTheAccount(string serviceUri)
        {
            QueueClient client = QueueClients.Create(new QueueOptions { Name = "scan-nightly", ServiceUri = serviceUri });

            Assert.That(client.Uri, Is.EqualTo(new Uri("https://example.queue.core.windows.net/scan-nightly")));
        }
    }
}
