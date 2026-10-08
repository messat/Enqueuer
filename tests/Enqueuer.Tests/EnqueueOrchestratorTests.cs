using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enqueuer.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Enqueuer.Tests
{
    [TestFixture]
    public class EnqueueOrchestratorTests
    {
        private FakeLogger<EnqueueOrchestrator> _logger = null!;
        private EnqueueOrchestrator _orchestrator = null!;

        [SetUp]
        public void SetUp()
        {
            _logger = new FakeLogger<EnqueueOrchestrator>();
            HostingEnvironment environment = new HostingEnvironment { EnvironmentName = "Test" };
            IOptions<QueueOptions> queueOptions = Options.Create(new QueueOptions { Name = "scan-test", ConnectionString = "UseDevelopmentStorage=true" });
            _orchestrator = new EnqueueOrchestrator(_logger, environment, queueOptions);
        }

        [Test]
        public async Task RunAsync_WhenStarting_LogsEnvironmentAsStructuredProperty()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            Assert.That(records, Is.Not.Empty);
            Assert.That(records[0].GetStructuredStateValue("EnvironmentName"), Is.EqualTo("Test"));
        }

        [Test]
        public async Task RunAsync_WhenStarting_LogsQueueNameAsStructuredProperty()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            Assert.That(records, Is.Not.Empty);
            Assert.That(records[0].GetStructuredStateValue("QueueName"), Is.EqualTo("scan-test"));
        }

        // Until the repository listing arrives (Issue 2), there is never anything to queue
        [Test]
        public async Task RunAsync_WithNothingToQueue_LogsThatItIsExiting()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            Assert.That(records, Is.Not.Empty);
            Assert.That(records[records.Count - 1].Message, Is.EqualTo("No repositories to queue, exiting"));
        }
    }
}
