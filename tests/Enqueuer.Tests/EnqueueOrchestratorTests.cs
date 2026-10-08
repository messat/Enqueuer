using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Enqueuer.AzureDevOps;
using Enqueuer.Configuration;
using Enqueuer.Tests.AzureDevOps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
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

        // Issue 2, criterion 2: each skipped repository with its reason. The sample listing has one of each.
        [Test]
        public async Task RunAsync_LogsEachSkippedRepositoryWithItsReason()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            List<string> skipped = new List<string>();
            foreach (FakeLogRecord record in _logger.Collector.GetSnapshot())
            {
                if (record.GetStructuredStateValue("SkipReason") != null)
                {
                    skipped.Add(record.Message);
                }
            }

            Assert.That(skipped, Is.EqualTo(new[]
            {
                "Skipped Fabrikam-Fiber-Git/AnotherRepository: Empty",
                "Skipped Fabrikam-Fiber-Git/Fabrikam-Fiber-Git: Disabled",
                "Skipped TestGit/TestGit: InMaintenance"
            }));
        }

        // The reason is the enum's name, so Issue 4 can count skips by reason from the log
        [Test]
        public async Task RunAsync_LogsTheSkipReasonAsAStructuredProperty()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            FakeLogRecord? skipped = FindByProperty("SkipReason");
            Assert.That(skipped, Is.Not.Null, "no line with SkipReason");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(skipped!.GetStructuredStateValue("SkipReason"), Is.EqualTo("Empty"));
                Assert.That(skipped.GetStructuredStateValue("ProjectName"), Is.EqualTo("Fabrikam-Fiber-Git"));
                Assert.That(skipped.GetStructuredStateValue("RepositoryName"), Is.EqualTo("AnotherRepository"));
            }
        }

        [Test]
        public async Task RunAsync_LogsTheTotals()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, """
                {
                  "count": 2,
                  "value": [
                    { "id": "0f8fad5b-d9cb-469f-a165-70867728950e", "name": "victim-repo", "project": { "name": "Kudos Wall" }, "defaultBranch": "refs/heads/main" },
                    { "id": "5febef5a-833d-4e14-b9c0-14cb638f91e6", "name": "new-repo", "project": { "name": "Kudos Wall" } }
                  ]
                }
                """);

            await _orchestrator.RunAsync(CancellationToken.None);

            FakeLogRecord? totals = FindByProperty("ToScanCount");
            Assert.That(totals, Is.Not.Null, "no line with ToScanCount");
            Assert.That(totals!.Message, Is.EqualTo("Listed 2 repositories in " + OrganizationUrl + ": 1 to queue, 1 skipped"));
        }

        // Decision 14: a PAT whose owner can see no project lists nothing rather than being refused
        [Test]
        public async Task RunAsync_NothingListed_WarnsToCheckWhatThePatsOwnerCanSee()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, """{ "count": 0, "value": [] }""");

            await _orchestrator.RunAsync(CancellationToken.None);

            FakeLogRecord? warning = null;
            foreach (FakeLogRecord record in _logger.Collector.GetSnapshot())
            {
                if (record.Level == LogLevel.Warning)
                {
                    warning = record;
                }
            }

            Assert.That(warning, Is.Not.Null, "no warning");
            Assert.That(warning!.Message, Is.EqualTo("Azure DevOps listed no repositories in " + OrganizationUrl + ". Check that the PAT's owner can see its projects"));
        }

        [Test]
        public async Task RunAsync_SomethingListed_DoesNotWarn()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            foreach (FakeLogRecord record in _logger.Collector.GetSnapshot())
            {
                Assert.That(record.Level, Is.LessThan(LogLevel.Warning), record.Message);
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
