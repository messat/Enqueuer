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
using Microsoft.Extensions.Time.Testing;
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

        // Time that moves only when read: each GetUtcNow() moves it 1.5 seconds on, so a run that reads it at its start and
        // at its summary lasts exactly 1500 ms (Issue 4 design, decision 11)
        private FakeTimeProvider _time = null!;
        private EnqueueOrchestrator _orchestrator = null!;

        [SetUp]
        public void SetUp()
        {
            _azureDevOps = new FakeAzureDevOps();
            _services = _azureDevOps.BuildServices(OrganizationUrl, "your-test-pat-0001");
            _logger = new FakeLogger<EnqueueOrchestrator>();
            _queue = new FakeQueueClient("scan-test");
            _time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1500) };
            HostingEnvironment environment = new HostingEnvironment { EnvironmentName = "Test" };
            IOptions<QueueOptions> queueOptions = Options.Create(new QueueOptions { Name = "scan-test", ConnectionString = "UseDevelopmentStorage=true" });

            _orchestrator = new EnqueueOrchestrator(
                _logger,
                environment,
                queueOptions,
                _services.GetRequiredService<IOptions<AzureDevOpsOptions>>(),
                _services.GetRequiredService<AzureDevOpsRepositoryClient>(),
                new ScanRequestSender(_queue),
                _time);
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

                // Only the summary counts what was sent (decision 7), and it counts the one request that went
                Assert.That(
                    records.Where(record => record.GetStructuredStateValue("SentCount") != null).Select(record => record.GetStructuredStateValue("SentCount")),
                    Is.EqualTo(new[] { "1" }));
            }
        }

        // With nothing to scan, the queue is not touched; the summary says 0 were sent
        [Test]
        public async Task RunAsync_NothingToScan_SendsNothingAndSaysSentZero()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_queue.Attempts, Is.Zero);
                Assert.That(TheSummary().GetStructuredStateValue("SentCount"), Is.EqualTo("0"));
            }
        }

        // Issue 4, the summary: one line ends every run that gets past start-up, whatever the outcome (decision 2)
        [Test]
        public async Task RunAsync_WhenEverythingIsSent_EndsWithACompletedSummary()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);

            await _orchestrator.RunAsync(CancellationToken.None);

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Information));
                Assert.That(summary.GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Completed"));
                Assert.That(summary.GetStructuredStateValue("EnqueueDurationMs"), Is.EqualTo("1500"));
                Assert.That(summary.GetStructuredStateValue("ListedCount"), Is.EqualTo("4"));
                Assert.That(summary.GetStructuredStateValue("OrganizationUrl"), Is.EqualTo(OrganizationUrl));
                Assert.That(summary.GetStructuredStateValue("ToScanCount"), Is.EqualTo("3"));
                Assert.That(summary.GetStructuredStateValue("SkippedRepositoryCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("SkippedByReason"), Is.EqualTo("[\"[Disabled, 1]\",\"[InMaintenance, 0]\",\"[Empty, 0]\"]"));
                Assert.That(summary.GetStructuredStateValue("SentCount"), Is.EqualTo("3"));
                Assert.That(summary.GetStructuredStateValue("QueueName"), Is.EqualTo("scan-test"));
                Assert.That(summary.GetStructuredStateValue("NotSentCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("ErrorMessage"), Is.Null);
                Assert.That(summary.Message, Does.StartWith("Enqueue run Completed in 1500 ms: 4 listed in " + OrganizationUrl + ", 3 to queue, 1 skipped "));
                Assert.That(summary.Message, Does.EndWith(", 3 sent to scan-test, 0 not sent"));
            }
        }

        // Decision 14: every reason is counted, zeros included. The default sample listing has one of each.
        [Test]
        public async Task RunAsync_WithEverySkipReason_CountsEachReason()
        {
            await _orchestrator.RunAsync(CancellationToken.None);

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Completed"));
                Assert.That(summary.GetStructuredStateValue("SkippedRepositoryCount"), Is.EqualTo("3"));
                Assert.That(summary.GetStructuredStateValue("SkippedByReason"), Is.EqualTo("[\"[Disabled, 1]\",\"[InMaintenance, 1]\",\"[Empty, 1]\"]"));
                Assert.That(summary.GetStructuredStateValue("ToScanCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("SentCount"), Is.EqualTo("0"));
                Assert.That(_queue.Attempts, Is.Zero);
            }
        }

        // Decision 8: a run that sends nothing still completed. The warning about the empty listing comes just before.
        [Test]
        public async Task RunAsync_NothingListed_EndsWithACompletedSummaryAfterTheWarning()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, """{ "count": 0, "value": [] }""");

            await _orchestrator.RunAsync(CancellationToken.None);

            FakeLogRecord summary = TheSummary();
            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            FakeLogRecord beforeIt = records[records.Count - 2];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Information));
                Assert.That(summary.GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Completed"));
                Assert.That(summary.GetStructuredStateValue("ListedCount"), Is.EqualTo("0"));
                Assert.That(beforeIt.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(beforeIt.Message, Does.StartWith("Azure DevOps listed no repositories in "));
            }
        }

        // Decision 2: a listing that fails ends in a summary too, and the exception still goes up for Program (decision 13)
        [Test]
        public void RunAsync_WhenAzureDevOpsRefusesThePat_SummaryIsFailedAndTheExceptionGoesUp()
        {
            _azureDevOps.Respond(HttpStatusCode.Unauthorized, "{}");

            AzureDevOpsAuthenticationException? exception = Assert.ThrowsAsync<AzureDevOpsAuthenticationException>(() => _orchestrator.RunAsync(CancellationToken.None));

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(summary.GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Failed"));
                Assert.That(summary.GetStructuredStateValue("ListedCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("ErrorMessage"), Is.EqualTo(exception!.Message));
                Assert.That(summary.Message, Does.EndWith(". Error: " + exception.Message));
            }
        }

        [Test]
        public void RunAsync_WhenAzureDevOpsIsUnavailable_SummaryIsFailed()
        {
            _azureDevOps.Respond(HttpStatusCode.NotFound, "<html>404</html>");

            Assert.ThrowsAsync<AzureDevOpsUnavailableException>(() => _orchestrator.RunAsync(CancellationToken.None));

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(summary.GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Failed"));
            }
        }

        // Decision 3: the first send fails, so all three chosen to queue are not sent
        [Test]
        public void RunAsync_WhenTheQueueIsMissing_SummaryIsFailedWithNothingSent()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);
            _queue.FailSend(1, new RequestFailedException(404, "The specified queue does not exist.", "QueueNotFound", null));

            Assert.ThrowsAsync<ScanQueueNotFoundException>(() => _orchestrator.RunAsync(CancellationToken.None));

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(summary.GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Failed"));
                Assert.That(summary.GetStructuredStateValue("SentCount"), Is.EqualTo("0"));
                Assert.That(summary.GetStructuredStateValue("NotSentCount"), Is.EqualTo("3"));
                Assert.That(summary.GetStructuredStateValue("ErrorMessage"), Is.EqualTo("The queue scan-test does not exist. Create it, or correct Enqueuer:Queue:Name."));
            }
        }

        // Decision 3: ToScanCount is always SentCount plus NotSentCount, so one sent and two not sent
        [Test]
        public void RunAsync_WhenTheSecondSendFails_SummaryCountsOneSentAndTwoNotSent()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);
            _queue.FailSend(2, new RequestFailedException(503, "busy", "ServerBusy", null));

            Assert.ThrowsAsync<ScanQueueUnavailableException>(() => _orchestrator.RunAsync(CancellationToken.None));

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Failed"));
                Assert.That(summary.GetStructuredStateValue("SentCount"), Is.EqualTo("1"));
                Assert.That(summary.GetStructuredStateValue("NotSentCount"), Is.EqualTo("2"));
            }
        }

        // Decision 12: a timeout the job did not ask for is a failure, not a request to stop. The sender turns it into
        // ScanQueueUnavailableException, and the token was never cancelled.
        [Test]
        public void RunAsync_WhenASendTimesOutWithoutBeingStopped_SummaryIsFailed()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);
            _queue.FailSend(1, new OperationCanceledException("The operation was cancelled because it exceeded the configured timeout."));

            Assert.ThrowsAsync<ScanQueueUnavailableException>(() => _orchestrator.RunAsync(CancellationToken.None));

            Assert.That(TheSummary().GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Failed"));
        }

        // Decision 12, the other half: a cancellation the job's token did not cause, here from the HTTP client's own timeout,
        // reaches the orchestrator as it was thrown and is still Failed. The send's timeout above cannot show this, since
        // the sender turns it into ScanQueueUnavailableException first.
        [Test]
        public void RunAsync_WhenACancellationIsNotTheJobsOwn_SummaryIsFailed()
        {
            _azureDevOps.FailWith(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));

            Assert.CatchAsync<OperationCanceledException>(() => _orchestrator.RunAsync(CancellationToken.None));

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(summary.GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Failed"));
            }
        }

        // Decision 12: the job asked to stop (Ctrl+C, SIGTERM) is Stopped. The HTTP client throws a subclass of
        // OperationCanceledException for a token cancelled before the call.
        [Test]
        public void RunAsync_WhenTheJobIsStopped_SummaryIsStopped()
        {
            using (CancellationTokenSource stopped = new CancellationTokenSource())
            {
                stopped.Cancel();

                Assert.CatchAsync<OperationCanceledException>(() => _orchestrator.RunAsync(stopped.Token));
            }

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(summary.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(summary.GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Stopped"));
                Assert.That(summary.GetStructuredStateValue("ListedCount"), Is.EqualTo("0"));
            }
        }

        // Decision 13: a bug is Failed, and goes up as it was thrown, with its stack trace, for Program's Fatal line
        [Test]
        public void RunAsync_WhenABugIsThrown_SummaryIsFailedAndTheBugGoesUpUnchanged()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);
            InvalidOperationException bug = new InvalidOperationException("Simulated bug");
            _queue.FailSend(1, bug);

            InvalidOperationException? caught = Assert.ThrowsAsync<InvalidOperationException>(() => _orchestrator.RunAsync(CancellationToken.None));

            FakeLogRecord summary = TheSummary();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(caught, Is.SameAs(bug));
                Assert.That(summary.GetStructuredStateValue("EnqueueRunStatus"), Is.EqualTo("Failed"));
                Assert.That(summary.GetStructuredStateValue("ErrorMessage"), Is.EqualTo("Simulated bug"));
            }
        }

        // Decision 7: the summary replaces the Listed and Sent lines
        [Test]
        public async Task RunAsync_NoLongerLogsTheListedOrSentLines()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, ThreeToScanAndOneDisabled);

            await _orchestrator.RunAsync(CancellationToken.None);

            bool oldLine = _logger.Collector.GetSnapshot().Any(
                record => record.Message.StartsWith("Listed ", StringComparison.Ordinal) || record.Message.StartsWith("Sent ", StringComparison.Ordinal));
            Assert.That(oldLine, Is.False);
        }

        // Exactly one summary per run, and it is the last line (design, The summary line). FakeLogger keeps a dictionary
        // as one entry per pair, so SkippedByReason reads as ["[Disabled, 1]", ...], as the scanner's ScannerStatuses does.
        private FakeLogRecord TheSummary()
        {
            IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
            Assert.That(records.Count(record => record.GetStructuredStateValue("EnqueueRunStatus") != null), Is.EqualTo(1), "summary lines");
            FakeLogRecord last = records[records.Count - 1];
            Assert.That(last.GetStructuredStateValue("EnqueueRunStatus"), Is.Not.Null, "the summary is not the last line: " + last.Message);
            return last;
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
