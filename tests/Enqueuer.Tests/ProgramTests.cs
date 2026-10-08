using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Enqueuer.Tests.AzureDevOps;
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

        // Valid queue settings and no PAT, as on a machine where Enqueuer__AzureDevOps__Pat is not set
        private static readonly string[] ArgsWithoutPat =
        {
            "--environment", "Test",
            "--Enqueuer:Queue:Name", "scan-test",
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Enqueuer:AzureDevOps:OrganizationUrl", "https://dev.azure.com/BlacklightInternalsMo"
        };

        // Two settings groups wrong at once, as on a new machine: no queue name and no PAT
        private static readonly string[] ArgsWithoutQueueNameOrPat =
        {
            "--environment", "Test",
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=true",
            "--Enqueuer:AzureDevOps:OrganizationUrl", "https://dev.azure.com/BlacklightInternalsMo"
        };

        // Each missing or wrong setting, with words its one line must contain
        private static readonly TestCaseData[] BadSettings =
        {
            new TestCaseData(ArgsWithoutQueueName, "The Name field is required.").SetArgDisplayNames("missing Name"),
            new TestCaseData(ArgsWithInvalidQueueName, "Name must be 3 to 63 characters").SetArgDisplayNames("wrong Name"),
            new TestCaseData(ArgsWithMalformedServiceUri, "Enqueuer:Queue:ServiceUri").SetArgDisplayNames("malformed ServiceUri"),
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
