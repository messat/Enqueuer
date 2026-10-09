using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Enqueuer.AzureDevOps;
using Enqueuer.Configuration;
using Enqueuer.Queue;
using Enqueuer.Tests.AzureDevOps;
using Enqueuer.Tests.Queue;
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

        // The default sample listing skips all three of its repositories. Three to scan, in two projects, and a disabled
        // one among them (Issue 3 design, Testing).
        private const string ThreeToScanAndOneDisabled = """
            {
              "count": 4,
              "value": [
                { "id": "0f8fad5b-d9cb-469f-a165-70867728950e", "name": "victim-repo", "project": { "name": "Kudos Wall" }, "defaultBranch": "refs/heads/main" },
                { "id": "278d5cd2-584d-4b63-824a-2ba458937249", "name": "old-repo", "project": { "name": "Kudos Wall" }, "defaultBranch": "refs/heads/main", "isDisabled": true },
                { "id": "66efb083-777a-4cac-a350-a24b046be6be", "name": "ProblemApp", "project": { "name": "Kudos Wall" }, "defaultBranch": "refs/heads/develop" },
                { "id": "5febef5a-833d-4e14-b9c0-14cb638f91e6", "name": "WebShop", "project": { "name": "Retail" }, "defaultBranch": "refs/heads/main" }
              ]
            }
            """;

        private FakeAzureDevOps _azureDevOps = null!;
        private ServiceProvider _services = null!;
        private FakeLogger<EnqueueOrchestrator> _logger = null!;
        private FakeQueueClient _queue = null!;
        private EnqueueOrchestrator _orchestrator = null!;

        [SetUp]
        public void SetUp()
        {
            _azureDevOps = new FakeAzureDevOps();
            _services = _azureDevOps.BuildServices(OrganizationUrl, "your-test-pat-0001");
            _logger = new FakeLogger<EnqueueOrchestrator>();
            _queue = new FakeQueueClient("scan-test");
            HostingEnvironment environment = new HostingEnvironment { EnvironmentName = "Test" };
            IOptions<QueueOptions> queueOptions = Options.Create(new QueueOptions { Name = "scan-test", ConnectionString = "UseDevelopmentStorage=true" });

            _orchestrator = new EnqueueOrchestrator(
                _logger,
                environment,
                queueOptions,
                _services.GetRequiredService<IOptions<AzureDevOpsOptions>>(),
                _services.GetRequiredService<AzureDevOpsRepositoryClient>(),
                new ScanRequestSender(_queue));
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

        // Issue 3, criterion 1: one request per repository to scan, in the order listed, and none for a skipped one
        [Test]
        public async Task RunAsync_SendsOneRequestPerRepositoryToScanInListedOrder()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);

            await _orchestrator.RunAsync(CancellationToken.None);

            Assert.That(_queue.Sent.Select(sent => FieldIn(sent.Text, "repositoryName")), Is.EqualTo(new[] { "victim-repo", "ProblemApp", "WebShop" }));
        }

        // Issue 3, criterion 2: every message the enqueuer builds keeps the contract
        [Test]
        public async Task RunAsync_EveryRequestSent_KeepsTheContract()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);

            await _orchestrator.RunAsync(CancellationToken.None);

            Assert.That(_queue.Sent, Has.Count.EqualTo(3));
            foreach ((string Text, TimeSpan? VisibilityTimeout, TimeSpan? TimeToLive) sent in _queue.Sent)
            {
                Assert.That(ScanRequestContract.ProblemsWith(sent.Text), Is.Empty, sent.Text);
            }
        }

        // Decision 4: a new correlation ID for each message
        [Test]
        public async Task RunAsync_EachRequestSent_HasItsOwnCorrelationId()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);

            await _orchestrator.RunAsync(CancellationToken.None);

            List<Guid> correlationIds = _queue.Sent.Select(sent => new Guid(FieldIn(sent.Text, "correlationId"))).ToList();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(correlationIds, Has.Count.EqualTo(3));
                Assert.That(correlationIds, Is.Unique);
                Assert.That(correlationIds, Has.None.EqualTo(Guid.Empty));
            }
        }

        // Decision 15: a Queued line per message, carrying the correlation ID its message carries
        [Test]
        public async Task RunAsync_LogsAQueuedLinePerRequestWithItsCorrelationId()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);

            await _orchestrator.RunAsync(CancellationToken.None);

            List<FakeLogRecord> queued = _logger.Collector.GetSnapshot().Where(record => record.GetStructuredStateValue("CorrelationId") != null).ToList();
            List<string> sentIds = _queue.Sent.Select(sent => FieldIn(sent.Text, "correlationId")).ToList();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(queued.Select(record => record.GetStructuredStateValue("CorrelationId")), Is.EqualTo(sentIds));
                Assert.That(queued[0].Message, Is.EqualTo("Queued Kudos Wall/victim-repo with correlation " + sentIds[0]));
            }
        }

        [Test]
        public async Task RunAsync_EndsWithHowManyWereSent()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);

            await _orchestrator.RunAsync(CancellationToken.None);

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(records[records.Count - 1].Message, Is.EqualTo("Sent 3 scan requests to scan-test"));
                Assert.That(records[records.Count - 1].GetStructuredStateValue("SentCount"), Is.EqualTo("3"));
            }
        }

        // Decision 12: the first send that still fails stops the run; the requests already sent stay on the queue
        [Test]
        public void RunAsync_WhenTheSecondSendFails_StopsBeforeTheThird()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);
            _queue.FailSend(2, new RequestFailedException(503, "busy", "ServerBusy", null));

            Assert.ThrowsAsync<ScanQueueUnavailableException>(() => _orchestrator.RunAsync(CancellationToken.None));

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Attempts, Is.EqualTo(2));
                Assert.That(_queue.Sent.Select(sent => FieldIn(sent.Text, "repositoryName")), Is.EqualTo(new[] { "victim-repo" }));
                Assert.That(records.Count(record => record.GetStructuredStateValue("CorrelationId") != null), Is.EqualTo(1));
                Assert.That(records.Any(record => record.GetStructuredStateValue("SentCount") != null), Is.False);
            }
        }

        // With nothing to scan, the queue is not touched; replaces Issue 2's "Nothing sent" line
        [Test]
        public async Task RunAsync_NothingToScan_SendsNothingAndSaysSentZero()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Attempts, Is.Zero);
                Assert.That(records[records.Count - 1].Message, Is.EqualTo("Sent 0 scan requests to scan-test"));
            }
        }

        // One field of a sent body, read as the scanner reads it
        private static string FieldIn(string body, string name)
        {
            using (JsonDocument document = JsonDocument.Parse(body))
            {
                return document.RootElement.GetProperty(name).ToString();
            }
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
