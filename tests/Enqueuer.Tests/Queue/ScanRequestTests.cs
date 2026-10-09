using System;
using Enqueuer.Queue;
using Enqueuer.Repositories;
using NUnit.Framework;

namespace Enqueuer.Tests.Queue
{
    // Building the request for one repository Issue 2 chose (Issue 3 design, decision 4)
    [TestFixture]
    public class ScanRequestTests
    {
        // Two different GUIDs, so swapping them fails the test (Review Focus 5)
        private static readonly Guid RepositoryId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        private static readonly Guid CorrelationId = new Guid("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c");

        [Test]
        public void For_ARepositoryToScan_CopiesItsFieldsAndTheCorrelationId()
        {
            ScanRequest request = ScanRequest.For(new RepositoryToScan(RepositoryId, "Kudos Wall", "victim-repo", "main"), CorrelationId);

            Assert.That(request, Is.EqualTo(new ScanRequest(CorrelationId, RepositoryId, "Kudos Wall", "victim-repo", "main")));
        }
    }
}
