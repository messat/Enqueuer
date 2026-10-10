using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using Enqueuer.Tests.AzureDevOps;
using Enqueuer.Tests.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Serilog.Extensions.Logging;

namespace Enqueuer.Tests
{
    [TestFixture]
    public class ProgramTests
    {
        // Every run names its environment on the command line, which overrides DOTNET_ENVIRONMENT: in a shell
        // where that is Development, appsettings.Development.json would otherwise fill in the settings a test
        // leaves out

        // A fake PAT: values starting "your-" are placeholders to RepoScanner's self-scan
        private const string Pat = "your-test-pat-0001";

        // One repository to scan, so the run reaches the queue. The default sample listing skips all of its own, so the
        // tests that use it send nothing and need no queue fake (Issue 3 design, Testing).
        private const string OneToScan = """
            {
              "count": 1,
              "value": [
                { "id": "0f8fad5b-d9cb-469f-a165-70867728950e", "name": "victim-repo", "project": { "name": "Kudos Wall" }, "defaultBranch": "refs/heads/main" }
              ]
            }
            """;

        // Valid settings passed as command-line arguments, the last configuration source to be read
        private static readonly string[] ValidArgs =
        {
            "--environment", "Test",
            "--Enqueuer:Queue:Name", "scan-test",
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Enqueuer:AzureDevOps:OrganizationUrl", "https://dev.azure.com/BlacklightInternalsMo",
            "--Enqueuer:AzureDevOps:Pat", Pat
        };

        // Queue settings with the required Name missing
        private static readonly string[] ArgsWithoutQueueName =
        {
            "--environment", "Test",
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Enqueuer:AzureDevOps:OrganizationUrl", "https://dev.azure.com/BlacklightInternalsMo",
            "--Enqueuer:AzureDevOps:Pat", Pat
        };

        // Queue settings with a Name Azure Storage would refuse (capitals and an underscore)
        private static readonly string[] ArgsWithInvalidQueueName =
        {
            "--environment", "Test",
            "--Enqueuer:Queue:Name", "Scan_Nightly",
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Enqueuer:AzureDevOps:OrganizationUrl", "https://dev.azure.com/BlacklightInternalsMo",
            "--Enqueuer:AzureDevOps:Pat", Pat
        };

        // Queue settings with a ServiceUri that is not a URL at all
        private static readonly string[] ArgsWithMalformedServiceUri =
        {
            "--environment", "Test",
            "--Enqueuer:Queue:Name", "scan-test",
            "--Enqueuer:Queue:ServiceUri", "http://[bad",
            "--Enqueuer:AzureDevOps:OrganizationUrl", "https://dev.azure.com/BlacklightInternalsMo",
            "--Enqueuer:AzureDevOps:Pat", Pat
        };

        // Queue settings with a connection string the storage SDK cannot read, as with a typo
        private static readonly string[] ArgsWithMalformedConnectionString =
        {
            "--environment", "Test",
            "--Enqueuer:Queue:Name", "scan-test",
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=ture",
            "--Enqueuer:AzureDevOps:OrganizationUrl", "https://dev.azure.com/BlacklightInternalsMo",
            "--Enqueuer:AzureDevOps:Pat", Pat
        };

        // Valid queue settings and no PAT, as on a machine where Enqueuer__AzureDevOps__Pat is not set. The PAT is
        // blanked on the command line, which is read last, so a developer's own Enqueuer__AzureDevOps__Pat cannot
        // fill it in (review finding 1).
        private static readonly string[] ArgsWithoutPat =
        {
            "--environment", "Test",
            "--Enqueuer:Queue:Name", "scan-test",
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Enqueuer:AzureDevOps:OrganizationUrl", "https://dev.azure.com/BlacklightInternalsMo",
            "--Enqueuer:AzureDevOps:Pat="
        };

        // Two settings groups wrong at once, as on a new machine: no queue name and no PAT, blanked as above
        private static readonly string[] ArgsWithoutQueueNameOrPat =
        {
            "--environment", "Test",
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Enqueuer:AzureDevOps:OrganizationUrl", "https://dev.azure.com/BlacklightInternalsMo",
            "--Enqueuer:AzureDevOps:Pat="
        };

        // Each missing or wrong setting, with words its one line must contain
        private static readonly TestCaseData[] BadSettings =
        {
            new TestCaseData(ArgsWithoutQueueName, "The Name field is required.").SetArgDisplayNames("missing Name"),
            new TestCaseData(ArgsWithInvalidQueueName, "Name must be 3 to 63 characters").SetArgDisplayNames("wrong Name"),
            new TestCaseData(ArgsWithMalformedServiceUri, "Enqueuer:Queue:ServiceUri").SetArgDisplayNames("malformed ServiceUri"),
            new TestCaseData(ArgsWithMalformedConnectionString, "Enqueuer:Queue:ConnectionString").SetArgDisplayNames("malformed ConnectionString"),
            new TestCaseData(ArgsWithoutPat, "Enqueuer__AzureDevOps__Pat").SetArgDisplayNames("missing Pat"),
            // Fails at start-up like the others, so Main never reaches Azure DevOps
            new TestCaseData(With("--Enqueuer:Logging:Format", "Xml"), "Enqueuer:Logging:Format must be Text or Json").SetArgDisplayNames("wrong Format")
        };

        // Main would reach the real Azure DevOps, so a run that gets as far as listing goes through RunAsync with
        // FakeAzureDevOps beneath the HTTP client (Issue 2 design, Testing)
        [Test]
        public async Task RunAsync_WithValidSettings_ReturnsZero()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                int exitCode = await Program.RunAsync(ValidArgs, azureDevOps.ReplaceAzureDevOps);

                Assert.That(exitCode, Is.EqualTo(0));
            }
        }

        [Test]
        public async Task RunAsync_WithValidSettings_RunsTheOrchestrator()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                string output = await CaptureConsoleAsync(() => Program.RunAsync(ValidArgs, azureDevOps.ReplaceAzureDevOps));

                // The lines are JSON in Test (decision 4), so the values are read as properties, not found in a sentence
                IReadOnlyList<JsonElement> lines = LogLines.Read(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(
                        lines.Any(line => LogLines.Value(line, "EnvironmentName") == "Test" && LogLines.Value(line, "QueueName") == "scan-test"),
                        Is.True,
                        output);
                    // The counts are on the summary line, the run's last (Issue 4 design, decision 7)
                    Assert.That(
                        lines.Any(line => LogLines.Value(line, "EnqueueRunStatus") == "Completed"
                            && LogLines.Value(line, "ListedCount") == "3"
                            && LogLines.Value(line, "OrganizationUrl") == "https://dev.azure.com/BlacklightInternalsMo"),
                        Is.True,
                        output);
                    Assert.That(azureDevOps.Requests, Has.Count.EqualTo(1));
                }
            }
        }

        // Decision 7 and #108: a refused PAT is fixed by renewing it, not by changing code, so one line and no stack
        // trace. The scanner logs the same first words, and one alert watches for them.
        [Test]
        public async Task RunAsync_WhenAzureDevOpsRefusesThePat_WritesOneLineAndReturnsOne()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.Unauthorized, "{}");

                int exitCode = -1;
                string output = await CaptureConsoleAsync(async () => exitCode = await Program.RunAsync(ValidArgs, azureDevOps.ReplaceAzureDevOps));

                JsonElement[] failures = FatalLines(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(1));
                    Assert.That(failures, Has.Length.EqualTo(1), output);
                    // The scanner's alert search is a Fatal line whose message starts with these words (Issue 4 design, Searches)
                    Assert.That(LogLines.Message(failures[0]), Does.StartWith("Azure DevOps refused the token, enqueuer stopped: "));
                    Assert.That(LogLines.Value(failures[0], "Problem"), Does.StartWith("Azure DevOps refused the token in Enqueuer:AzureDevOps:Pat"));
                    Assert.That(failures[0].TryGetProperty("@x", out _), Is.False, "a refused PAT should not print a stack trace");
                    Assert.That(output, Does.Not.Contain("   at "), "a refused PAT should not print a stack trace");
                }
            }
        }

        // Decision 8: any other failure is one line too, such as an organisation that does not exist
        [Test]
        public async Task RunAsync_WhenAzureDevOpsCannotListTheRepositories_WritesOneLineAndReturnsOne()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.NotFound, "<html>404 - Page not found</html>");

                int exitCode = -1;
                string output = await CaptureConsoleAsync(async () => exitCode = await Program.RunAsync(ValidArgs, azureDevOps.ReplaceAzureDevOps));

                JsonElement[] failures = FatalLines(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(1));
                    Assert.That(failures, Has.Length.EqualTo(1), output);
                    Assert.That(LogLines.Message(failures[0]), Does.StartWith("Azure DevOps could not list the repositories, enqueuer stopped: "));
                    Assert.That(
                        LogLines.Value(failures[0], "Problem"),
                        Is.EqualTo("Repositories could not be listed from https://dev.azure.com/BlacklightInternalsMo: Azure DevOps answered 404 (Not Found)."));
                    Assert.That(failures[0].TryGetProperty("@x", out _), Is.False, "a failed listing should not print a stack trace");
                    Assert.That(output, Does.Not.Contain("   at "), "a failed listing should not print a stack trace");
                }
            }
        }

        // Issue 3, criterion 1, through the whole program: the repository's request lands on the queue
        [Test]
        public async Task RunAsync_WithARepositoryToScan_SendsItAndReturnsZero()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.OK, OneToScan);
                FakeQueueClient queue = new FakeQueueClient("scan-test");

                int exitCode = -1;
                string output = await CaptureConsoleAsync(async () => exitCode = await Program.RunAsync(ValidArgs, services =>
                {
                    azureDevOps.ReplaceAzureDevOps(services);
                    queue.ReplaceQueue(services);
                }));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(queue.Sent, Has.Count.EqualTo(1));
                    Assert.That(
                        LogLines.Read(output).Any(line => LogLines.Value(line, "EnqueueRunStatus") == "Completed" && LogLines.Value(line, "SentCount") == "1"),
                        Is.True,
                        output);
                }
            }
        }

        // Issue 3, criterion 3 and decision 10: a missing queue is fixed like a setting, so one line and exit 2
        [Test]
        public async Task RunAsync_WhenTheQueueIsMissing_WritesOneLineAndReturnsTwo()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.OK, OneToScan);
                FakeQueueClient queue = new FakeQueueClient("scan-test");
                queue.FailSend(1, new RequestFailedException(404, "The specified queue does not exist.", "QueueNotFound", null));

                int exitCode = -1;
                string output = await CaptureConsoleAsync(async () => exitCode = await Program.RunAsync(ValidArgs, services =>
                {
                    azureDevOps.ReplaceAzureDevOps(services);
                    queue.ReplaceQueue(services);
                }));

                JsonElement[] failures = FatalLines(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(2));
                    Assert.That(failures, Has.Length.EqualTo(1), output);
                    Assert.That(LogLines.Message(failures[0]), Does.StartWith("Queue missing, enqueuer stopped: "));
                    Assert.That(
                        LogLines.Value(failures[0], "Problem"),
                        Is.EqualTo("The queue scan-test does not exist. Create it, or correct Enqueuer:Queue:Name."));
                    Assert.That(failures[0].TryGetProperty("@x", out _), Is.False, "a missing queue should not print a stack trace");
                    Assert.That(output, Does.Not.Contain("   at "), "a missing queue should not print a stack trace");
                }
            }
        }

        // Issue 3, criterion 3 and decision 13: a failed send is one line giving Azure's status and error code, exit 1
        [Test]
        public async Task RunAsync_WhenASendFails_WritesOneLineAndReturnsOne()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.OK, OneToScan);
                FakeQueueClient queue = new FakeQueueClient("scan-test");
                queue.FailSend(1, new RequestFailedException(
                    403, "Service request failed.\r\nStatus: 403\r\nHeaders:\r\nx-ms-request-id: 1234", "AuthorizationPermissionMismatch", null));

                int exitCode = -1;
                string output = await CaptureConsoleAsync(async () => exitCode = await Program.RunAsync(ValidArgs, services =>
                {
                    azureDevOps.ReplaceAzureDevOps(services);
                    queue.ReplaceQueue(services);
                }));

                JsonElement[] failures = FatalLines(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(1));
                    Assert.That(failures, Has.Length.EqualTo(1), output);
                    Assert.That(LogLines.Message(failures[0]), Does.StartWith("Scan requests could not be sent, enqueuer stopped: "));
                    Assert.That(
                        LogLines.Value(failures[0], "Problem"),
                        Is.EqualTo("The scan request for Kudos Wall/victim-repo could not be sent to scan-test: Azure answered 403 (AuthorizationPermissionMismatch)."));
                    Assert.That(failures[0].TryGetProperty("@x", out _), Is.False, "a failed send should not print a stack trace");
                    Assert.That(output, Does.Not.Contain("   at "), "a failed send should not print a stack trace");
                    Assert.That(output, Does.Not.Contain("Headers"), "the SDK's own message should not be printed");
                }
            }
        }

        // Issue 4 design, decision 5: the orchestrator logs the summary and rethrows, so on a failure it is the line just
        // before Program's Fatal line. SkippedByReason is an object in JSON, with every reason in it (decision 14).
        [Test]
        public async Task RunAsync_WhenASendFails_TheSummaryComesJustBeforeTheFatalLine()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.OK, OneToScan);
                FakeQueueClient queue = new FakeQueueClient("scan-test");
                queue.FailSend(1, new RequestFailedException(403, "denied", "AuthorizationPermissionMismatch", null));

                string output = await CaptureConsoleAsync(() => Program.RunAsync(ValidArgs, services =>
                {
                    azureDevOps.ReplaceAzureDevOps(services);
                    queue.ReplaceQueue(services);
                }));

                IReadOnlyList<JsonElement> lines = LogLines.Read(output);
                JsonElement last = lines[lines.Count - 1];
                JsonElement summary = lines[lines.Count - 2];
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(LogLines.Level(last), Is.EqualTo("Fatal"), output);
                    Assert.That(LogLines.Level(summary), Is.EqualTo("Warning"), output);
                    Assert.That(LogLines.Value(summary, "EnqueueRunStatus"), Is.EqualTo("Failed"));
                    Assert.That(LogLines.Value(summary, "SentCount"), Is.EqualTo("0"));
                    Assert.That(LogLines.Value(summary, "NotSentCount"), Is.EqualTo("1"));
                    Assert.That(LogLines.Value(summary, "SkippedByReason"), Is.EqualTo("{\"Disabled\":0,\"InMaintenance\":0,\"Empty\":0}"));
                }
            }
        }

        // Issue 4 plan, Review Focus 5: a bug's message may have a line break. The summary still holds it exactly, and
        // every line is still one JSON object, because JSON writes the break as \n.
        [Test]
        public async Task RunAsync_WhenABugsMessageHasALineBreak_EveryLineIsStillOneJsonObject()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.OK, OneToScan);
                FakeQueueClient queue = new FakeQueueClient("scan-test");
                queue.FailSend(1, new InvalidOperationException("first line\nsecond line"));

                int exitCode = -1;
                string output = await CaptureConsoleAsync(async () => exitCode = await Program.RunAsync(ValidArgs, services =>
                {
                    azureDevOps.ReplaceAzureDevOps(services);
                    queue.ReplaceQueue(services);
                }));

                // Read fails the test on any line that is not one JSON object
                IReadOnlyList<JsonElement> lines = LogLines.Read(output);
                JsonElement summary = lines.Single(line => LogLines.Value(line, "EnqueueRunStatus") != null);
                JsonElement last = lines[lines.Count - 1];
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(1));
                    Assert.That(LogLines.Value(summary, "ErrorMessage"), Is.EqualTo("first line\nsecond line"));
                    Assert.That(LogLines.Level(last), Is.EqualTo("Fatal"), output);
                    Assert.That(LogLines.Message(last), Is.EqualTo("Enqueuer failed"));
                    Assert.That(last.TryGetProperty("@x", out _), Is.True, "a bug keeps its stack trace");
                }
            }
        }

        // Issue 2, criterion 4: whatever happens, nothing the enqueuer prints holds the PAT, plain or in Base64. Searched
        // as printed and, string by string, unescaped, so JSON's escaping cannot hide a leaked PAT (Issue 4 plan, Review
        // Focus 2).
        [TestCase(HttpStatusCode.OK)]
        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.NotFound)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public async Task RunAsync_WhateverAzureDevOpsAnswers_NeverPrintsThePat(HttpStatusCode status)
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(status, status == HttpStatusCode.OK ? FakeAzureDevOps.SampleListing : "");

                string output = await CaptureConsoleAsync(() => Program.RunAsync(ValidArgs, azureDevOps.ReplaceAzureDevOps));
                string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat));
                bool leaked = output.Contains(Pat, StringComparison.Ordinal)
                    || output.Contains(base64, StringComparison.Ordinal)
                    || LogLines.Read(output).SelectMany(LogLines.Strings)
                        .Any(text => text.Contains(Pat, StringComparison.Ordinal) || text.Contains(base64, StringComparison.Ordinal));

                Assert.That(leaked, Is.False, "the PAT is in the output");
            }
        }

        // Issue 4 design, decision 4: outside Development every line is one JSON object, so Log Analytics can filter on
        // any property. These runs are in Test, as every ProgramTests run is.
        [Test]
        public async Task RunAsync_OutsideDevelopment_WritesEveryLineAsJson()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.OK, OneToScan);
                FakeQueueClient queue = new FakeQueueClient("scan-test");

                int exitCode = -1;
                string output = await CaptureConsoleAsync(async () => exitCode = await Program.RunAsync(ValidArgs, services =>
                {
                    azureDevOps.ReplaceAzureDevOps(services);
                    queue.ReplaceQueue(services);
                }));

                // Read fails the test on any line that is not one JSON object
                IReadOnlyList<JsonElement> lines = LogLines.Read(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(0));
                    Assert.That(lines, Has.Count.GreaterThanOrEqualTo(3), output);
                    Assert.That(lines.All(line => line.TryGetProperty("@t", out _) && line.TryGetProperty("@m", out _)), Is.True, output);
                }
            }
        }

        // Decision 4 and criterion 2: the Queued line carries the three properties the scanner's lines carry, at the top
        // level, so one search on a correlation ID finds this line too
        [Test]
        public async Task RunAsync_QueuedLine_HasTheCorrelationIdAndRepositoryAtTheTopLevel()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.OK, OneToScan);
                FakeQueueClient queue = new FakeQueueClient("scan-test");

                string output = await CaptureConsoleAsync(() => Program.RunAsync(ValidArgs, services =>
                {
                    azureDevOps.ReplaceAzureDevOps(services);
                    queue.ReplaceQueue(services);
                }));

                JsonElement queued = LogLines.Read(output).Single(line => LogLines.Value(line, "CorrelationId") != null);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(LogLines.Value(queued, "ProjectName"), Is.EqualTo("Kudos Wall"));
                    Assert.That(LogLines.Value(queued, "RepositoryName"), Is.EqualTo("victim-repo"));
                    Assert.That(Guid.TryParse(LogLines.Value(queued, "CorrelationId"), out _), Is.True, output);
                }
            }
        }

        // Decision 4: Development keeps the readable text line
        [Test]
        public async Task RunAsync_InDevelopment_WritesText()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                string output = await CaptureConsoleAsync(() => Program.RunAsync(With("--environment", "Development"), azureDevOps.ReplaceAzureDevOps));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(NonEmptyLines(output).Any(line => line.StartsWith('{')), Is.False, output);
                    Assert.That(output, Does.Contain(" INF] Enqueuer.EnqueueOrchestrator: Enqueuer starting in Development"));
                }
            }
        }

        // Decision 4: Enqueuer:Logging:Format overrides the environment, so Text outside Development writes text
        [Test]
        public async Task RunAsync_WithFormatTextOutsideDevelopment_WritesText()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                string output = await CaptureConsoleAsync(() => Program.RunAsync(With("--Enqueuer:Logging:Format", "Text"), azureDevOps.ReplaceAzureDevOps));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(NonEmptyLines(output).Any(line => line.StartsWith('{')), Is.False, output);
                    Assert.That(output, Does.Contain(" INF] Enqueuer.EnqueueOrchestrator: Enqueuer starting in Test"));
                }
            }
        }

        // Issue 4 plan, Review Focus 1: a name a search compares as written must read back exactly, though JSON escapes some characters
        // and writes the rest as they are. A repository's name is not limited to ASCII.
        [Test]
        public async Task RunAsync_NamesThatJsonEscapes_ReadBackExactly()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.OK, """
                    {
                      "count": 1,
                      "value": [
                        { "id": "0f8fad5b-d9cb-469f-a165-70867728950e", "name": "O'Brien-Ölçek", "project": { "name": "Café & Co" }, "defaultBranch": "refs/heads/main" }
                      ]
                    }
                    """);
                FakeQueueClient queue = new FakeQueueClient("scan-test");

                string output = await CaptureConsoleAsync(() => Program.RunAsync(ValidArgs, services =>
                {
                    azureDevOps.ReplaceAzureDevOps(services);
                    queue.ReplaceQueue(services);
                }));

                JsonElement queued = LogLines.Read(output).Single(line => LogLines.Value(line, "CorrelationId") != null);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(LogLines.Value(queued, "ProjectName"), Is.EqualTo("Café & Co"));
                    Assert.That(LogLines.Value(queued, "RepositoryName"), Is.EqualTo("O'Brien-Ölçek"));
                }
            }
        }

        // Issue 4 plan, Review Focus 4: the start-up logger and the host's logger must agree, as the scanner's decision 10 found. In
        // Development a run whose PAT is refused is text from both: the host's Information lines and the start-up
        // logger's Fatal line. The JSON half is RunAsync_OutsideDevelopment_WritesEveryLineAsJson and the refused-PAT test.
        [Test]
        public async Task RunAsync_InDevelopmentWhenThePatIsRefused_BothLoggersWriteText()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                azureDevOps.Respond(HttpStatusCode.Unauthorized, "{}");

                int exitCode = -1;
                string output = await CaptureConsoleAsync(async () => exitCode = await Program.RunAsync(With("--environment", "Development"), azureDevOps.ReplaceAzureDevOps));

                string[] lines = NonEmptyLines(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(1));
                    Assert.That(lines.All(line => line.StartsWith('[')), Is.True, output);
                    Assert.That(lines.Any(line => line.Contains(" INF] ", StringComparison.Ordinal)), Is.True, "the host's logger: " + output);
                    Assert.That(lines.Any(line => line.Contains(" FTL] ", StringComparison.Ordinal)), Is.True, "the start-up logger: " + output);
                }
            }
        }

        // Decision 15: the HTTP client logs every request, and Polly every attempt, at Information. Only their
        // warnings and errors reach the log.
        [Test]
        public async Task RunAsync_WithValidSettings_LogsNoLineFromTheHttpClientOrPolly()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            {
                string output = await CaptureConsoleAsync(() => Program.RunAsync(ValidArgs, azureDevOps.ReplaceAzureDevOps));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(output, Does.Not.Contain("System.Net.Http.HttpClient"));
                    Assert.That(output, Does.Not.Contain("Polly"));
                }
            }
        }

        // Review finding 1: a developer's machine has Enqueuer__AzureDevOps__Pat set, so the "no PAT" arguments must
        // blank it on the command line, which is read last. Otherwise the test would reach the real Azure DevOps.
        [Test]
        public async Task Main_WithoutPat_IgnoresAPatInTheEnvironment()
        {
            string? before = Environment.GetEnvironmentVariable("Enqueuer__AzureDevOps__Pat");
            Environment.SetEnvironmentVariable("Enqueuer__AzureDevOps__Pat", "your-env-pat-0002");
            try
            {
                int exitCode = await Program.Main(ArgsWithoutPat);

                Assert.That(exitCode, Is.EqualTo(2));
            }
            finally
            {
                Environment.SetEnvironmentVariable("Enqueuer__AzureDevOps__Pat", before);
            }
        }

        // Issue 4 plan, Review Focus 3: the start-up logger exists before configuration is read, so it takes the format from the
        // environment itself. With Enqueuer__Logging__Format=Text its Fatal line is text although the run is in Test.
        [Test]
        public async Task Main_WithFormatTextInTheEnvironment_WritesTheStartUpLinesAsText()
        {
            string? before = Environment.GetEnvironmentVariable("Enqueuer__Logging__Format");
            Environment.SetEnvironmentVariable("Enqueuer__Logging__Format", "Text");
            try
            {
                string output = await CaptureConsoleAsync(() => Program.Main(ArgsWithoutQueueName));

                string[] lines = NonEmptyLines(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(lines, Has.Length.EqualTo(1), output);
                    Assert.That(lines[0], Does.Contain(" FTL] "));
                    Assert.That(lines[0], Does.Not.StartWith("{"));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("Enqueuer__Logging__Format", before);
            }
        }

        // Each settings group is checked on its own, so the host reports two wrong groups together: still one line,
        // naming both, and exit 2 (Issue 2 design, Settings)
        [Test]
        public async Task Main_WithTwoSettingsGroupsWrong_WritesOneLineNamingBothAndReturnsTwo()
        {
            int exitCode = -1;
            string output = await CaptureConsoleAsync(async () => exitCode = await Program.Main(ArgsWithoutQueueNameOrPat));

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(lines, Has.Length.EqualTo(1), output);
                Assert.That(output, Does.Contain("Invalid configuration"));
                Assert.That(output, Does.Contain("The Name field is required."));
                Assert.That(output, Does.Contain("Enqueuer__AzureDevOps__Pat"));
            }
        }

        // Issue 1 acceptance criterion: a missing setting exits 2
        [Test]
        public async Task Main_WithoutQueueName_ReturnsTwo()
        {
            int exitCode = await Program.Main(ArgsWithoutQueueName);

            Assert.That(exitCode, Is.EqualTo(2));
        }

        // Issue 1 acceptance criterion: a wrong setting exits 2
        [Test]
        public async Task Main_WithInvalidQueueName_ReturnsTwo()
        {
            int exitCode = await Program.Main(ArgsWithInvalidQueueName);

            Assert.That(exitCode, Is.EqualTo(2));
        }

        // A ServiceUri the configuration binder could not read used to escape validation and exit 1
        [Test]
        public async Task Main_WithMalformedServiceUri_ReturnsTwo()
        {
            int exitCode = await Program.Main(ArgsWithMalformedServiceUri);

            Assert.That(exitCode, Is.EqualTo(2));
        }

        // A connection string the SDK cannot read used to pass validation and fail when the queue client was built,
        // printing a stack trace and exiting 1 (final review of #110, finding 5)
        [Test]
        public async Task Main_WithMalformedConnectionString_ReturnsTwo()
        {
            int exitCode = await Program.Main(ArgsWithMalformedConnectionString);

            Assert.That(exitCode, Is.EqualTo(2));
        }

        // Issue 1 acceptance criterion: ... with one line, and no stack trace
        [TestCaseSource(nameof(BadSettings))]
        public async Task Main_WithBadSetting_WritesOneLineWithoutStackTrace(string[] args, string expected)
        {
            string output = await CaptureConsoleAsync(() => Program.Main(args));

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.That(lines, Has.Length.EqualTo(1), output);
            Assert.That(lines[0], Does.Contain("Invalid configuration"));
            Assert.That(lines[0], Does.Contain(expected));
            Assert.That(output, Does.Not.Contain("   at "), "A configuration error should not print a stack trace");
        }

        [Test]
        public async Task RunAsync_WhenTheRunFails_ReturnsOne()
        {
            int exitCode = await Program.RunAsync(ValidArgs, services => services.AddHostedService<FailingService>());

            Assert.That(exitCode, Is.EqualTo(1));
        }

        [Test]
        public async Task RunAsync_WhenTheRunFails_LogsTheError()
        {
            string output = await CaptureConsoleAsync(() => Program.RunAsync(ValidArgs, services => services.AddHostedService<FailingService>()));

            Assert.That(output, Does.Contain("Enqueuer failed"));
            Assert.That(output, Does.Contain("Simulated failure"));
        }

        [Test]
        public void CreateBuilder_WhenBuilt_UsesSerilogForLogging()
        {
            HostApplicationBuilder builder = Program.CreateBuilder(ValidArgs);

            using (IHost host = builder.Build())
            {
                ILoggerFactory loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();

                Assert.That(loggerFactory, Is.TypeOf<SerilogLoggerFactory>());
            }
        }

        // Decision 8: the client is built from Enqueuer:Queue, and the orchestrator gets the sender over it
        [Test]
        public void CreateBuilder_WhenBuilt_HasAClientForTheNamedQueue()
        {
            HostApplicationBuilder builder = Program.CreateBuilder(ValidArgs);

            using (IHost host = builder.Build())
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(host.Services.GetRequiredService<QueueClient>().Name, Is.EqualTo("scan-test"));
                    Assert.That(host.Services.GetRequiredService<EnqueueOrchestrator>(), Is.Not.Null);
                }
            }
        }

        [Test]
        public void StartAsync_WithoutQueueName_ThrowsOptionsValidationException()
        {
            HostApplicationBuilder builder = Program.CreateBuilder(ArgsWithoutQueueName);

            using (IHost host = builder.Build())
            {
                OptionsValidationException? exception = Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

                Assert.That(exception?.Message, Does.Contain("Name"));
            }
        }

        // A service that throws as soon as the host starts it, to simulate an unexpected failure
        internal sealed class FailingService : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Simulated failure");
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        // The JSON lines Serilog wrote at Fatal, the level Program uses for the reason it stopped. CLEF names the level
        // in @l, and LogLines.Level says Information for a line that has none.
        private static JsonElement[] FatalLines(string output)
        {
            return LogLines.Read(output).Where(line => LogLines.Level(line) == "Fatal").ToArray();
        }

        // ValidArgs followed by more. A repeated key's last value wins, for the host and for
        // ConsoleLogFormat.IsJsonBeforeTheHost alike, so this is how a test changes the environment or the format.
        private static string[] With(params string[] extra)
        {
            return ValidArgs.Concat(extra).ToArray();
        }

        // The lines of text output, without the blank ones
        private static string[] NonEmptyLines(string output)
        {
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // Runs the given code with the console redirected, and returns everything it wrote
        private static async Task<string> CaptureConsoleAsync(Func<Task> action)
        {
            TextWriter originalOut = Console.Out;

            using (StringWriter output = new StringWriter())
            {
                Console.SetOut(output);
                try
                {
                    await action();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }

                return output.ToString();
            }
        }
    }
}
