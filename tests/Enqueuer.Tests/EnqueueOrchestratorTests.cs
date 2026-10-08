using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enqueuer.AzureDevOps;
using Enqueuer.Configuration;
using Enqueuer.Tests.AzureDevOps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Enqueuer.Tests
{
    [TestFixture]
    public class EnqueueOrchestratorTests
    {
        private const string OrganizationUrl = "https://dev.azure.com/BlacklightInternalsMo";

        private FakeAzureDevOps _azureDevOps = null!;
        private ServiceProvider _services = null!;
        private FakeLogger<EnqueueOrchestrator> _logger = null!;
        private EnqueueOrchestrator _orchestrator = null!;

        [SetUp]
        public void SetUp()
        {
            _azureDevOps = new FakeAzureDevOps();
            _services = _azureDevOps.BuildServices(OrganizationUrl, "your-test-pat-0001");
            _logger = new FakeLogger<EnqueueOrchestrator>();
            HostingEnvironment environment = new HostingEnvironment { EnvironmentName = "Test" };
            IOptions<QueueOptions> queueOptions = Options.Create(new QueueOptions { Name = "scan-test", ConnectionString = "UseDevelopmentStorage=true" });

            _orchestrator = new EnqueueOrchestrator(
                _logger,
                environment,
                queueOptions,
                _services.GetRequiredService<IOptions<AzureDevOpsOptions>>(),
                _services.GetRequiredService<AzureDevOpsRepositoryClient>());
        }

        [TearDown]
        public void TearDown()
        {
            _services.Dispose();
            _azureDevOps.Dispose();
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

        // Issue 2, criterion 1: the repositories are listed with one call
        [Test]
        public async Task RunAsync_ListsTheRepositoriesOnce()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            Assert.That(_azureDevOps.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task RunAsync_LogsHowManyRepositoriesWereListedAndWhere()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            FakeLogRecord? listed = FindByProperty("ListedCount");
            Assert.That(listed, Is.Not.Null, "no line with ListedCount");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(listed!.GetStructuredStateValue("ListedCount"), Is.EqualTo("3"));
                Assert.That(listed.GetStructuredStateValue("OrganizationUrl"), Is.EqualTo(OrganizationUrl));
            }
        }

        // Sending arrives in Issue 3, which removes this line
        [Test]
        public async Task RunAsync_UntilIssue3_SaysNothingWasSent()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            Assert.That(records, Is.Not.Empty);
            Assert.That(records[records.Count - 1].Message, Is.EqualTo("Nothing sent: sending scan requests arrives in Issue 3"));
        }

        private FakeLogRecord? FindByProperty(string name)
        {
            foreach (FakeLogRecord record in _logger.Collector.GetSnapshot())
            {
                if (record.GetStructuredStateValue(name) != null)
                {
                    return record;
                }
            }

            return null;
        }
    }
}
