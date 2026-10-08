using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
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
            new TestCaseData(ArgsWithoutPat, "Enqueuer__AzureDevOps__Pat").SetArgDisplayNames("missing Pat")
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

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(output, Does.Contain("Enqueuer starting in"));
                    Assert.That(output, Does.Contain("scan-test"));
                    Assert.That(output, Does.Contain("Listed 3 repositories in https://dev.azure.com/BlacklightInternalsMo"));
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

                string[] failures = FatalLines(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(1));
                    Assert.That(failures, Has.Length.EqualTo(1), output);
                    Assert.That(failures[0], Does.Contain("Azure DevOps refused the token, enqueuer stopped: Azure DevOps refused the token in Enqueuer:AzureDevOps:Pat"));
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

                string[] failures = FatalLines(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(1));
                    Assert.That(failures, Has.Length.EqualTo(1), output);
                    Assert.That(failures[0], Does.Contain("Azure DevOps could not list the repositories, enqueuer stopped: Repositories could not be listed from https://dev.azure.com/BlacklightInternalsMo: Azure DevOps answered 404 (Not Found)."));
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
                    Assert.That(output, Does.Contain("Sent 1 scan requests to scan-test"));
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

                string[] failures = FatalLines(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(2));
                    Assert.That(failures, Has.Length.EqualTo(1), output);
                    Assert.That(failures[0], Does.Contain("Queue missing, enqueuer stopped: The queue scan-test does not exist. Create it, or correct Enqueuer:Queue:Name."));
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

                string[] failures = FatalLines(output);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(exitCode, Is.EqualTo(1));
                    Assert.That(failures, Has.Length.EqualTo(1), output);
                    Assert.That(failures[0], Does.Contain("Scan requests could not be sent, enqueuer stopped: The scan request for Kudos Wall/victim-repo could not be sent to scan-test: Azure answered 403 (AuthorizationPermissionMismatch)."));
                    Assert.That(output, Does.Not.Contain("   at "), "a failed send should not print a stack trace");
                    Assert.That(output, Does.Not.Contain("Headers"), "the SDK's own message should not be printed");
                }
            }
        }

        // Issue 2, criterion 4: whatever happens, nothing the enqueuer prints holds the PAT, plain or in Base64
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
                bool leaked = output.Contains(Pat, StringComparison.Ordinal) || output.Contains(base64, StringComparison.Ordinal);

                Assert.That(leaked, Is.False, "the PAT is in the output");
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

        // The lines Serilog wrote at Fatal, the level Program uses for the reason it stopped
        private static string[] FatalLines(string output)
        {
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(" FTL] ", StringComparison.Ordinal))
                .ToArray();
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
