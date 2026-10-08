using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        // Valid queue settings passed as command-line arguments, the last configuration source to be read
        private static readonly string[] ValidArgs =
        {
            "--Enqueuer:Queue:Name", "scan-test",
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=true"
        };

        // Queue settings with the required Name missing
        private static readonly string[] ArgsWithoutQueueName =
        {
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=true"
        };

        // Queue settings with a Name Azure Storage would refuse (capitals and an underscore)
        private static readonly string[] ArgsWithInvalidQueueName =
        {
            "--Enqueuer:Queue:Name", "Scan_Nightly",
            "--Enqueuer:Queue:ConnectionString", "UseDevelopmentStorage=true"
        };

        [Test]
        public async Task Main_WithValidQueueSettings_ReturnsZero()
        {
            int exitCode = await Program.Main(ValidArgs);

            Assert.That(exitCode, Is.EqualTo(0));
        }

        [Test]
        public async Task Main_WithValidQueueSettings_RunsTheOrchestrator()
        {
            string output = await CaptureConsoleAsync(() => Program.Main(ValidArgs));

            Assert.That(output, Does.Contain("Enqueuer starting in"));
            Assert.That(output, Does.Contain("scan-test"));
            Assert.That(output, Does.Contain("No repositories to queue, exiting"));
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

        // Issue 1 acceptance criterion: ... with one line, and no stack trace
        [Test]
        public async Task Main_WithoutQueueName_WritesOneLineWithoutStackTrace()
        {
            string output = await CaptureConsoleAsync(() => Program.Main(ArgsWithoutQueueName));

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.That(lines, Has.Length.EqualTo(1), output);
            Assert.That(lines[0], Does.Contain("Invalid configuration"));
            Assert.That(lines[0], Does.Contain("The Name field is required."));
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
