using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Enqueuer.AzureDevOps;
using Enqueuer.Configuration;
using Enqueuer.Logging;
using Enqueuer.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Enqueuer
{
    public static class Program
    {
        // Time, level, class name, message
        private const string ConsoleOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        // The return value is the process exit code the Container Apps Job sees (see ExitCodes).
        public static async Task<int> Main(string[] args)
        {
            return await RunAsync(args, configureServices: null);
        }

        // Separate from Main so tests can add services (e.g. one that fails on purpose) and check
        // the exit code. Internal: visible to Enqueuer.Tests only (see the .csproj).
        internal static async Task<int> RunAsync(string[] args, Action<IServiceCollection>? configureServices)
        {
            // Start-up logger: exists before the host is built and after it is disposed, so a
            // failure to start is always logged. The host has its own logger for normal running.
            // JSON or text as the host's is, so a fatal line such as a rejected PAT reaches Log
            // Analytics as JSON too (#112, Issue 4 design decision 4)
            Log.Logger = WriteToConsole(
                    new LoggerConfiguration().Enrich.WithProperty("SourceContext", typeof(Program).FullName).WriteTo,
                    ConsoleLogFormat.IsJsonBeforeTheHost(args))
                .CreateLogger();

            try
            {
                HostApplicationBuilder builder = CreateBuilder(args);
                configureServices?.Invoke(builder.Services);

                using (IHost host = builder.Build())
                {
                    // Starting the host runs start-up checks, including settings validation
                    await host.StartAsync();

                    // Stops the work cleanly if the job is asked to shut down (Ctrl+C locally, SIGTERM in Azure).
                    // The host is never left running with host.Run(): the job succeeds only when the process exits.
                    IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

                    EnqueueOrchestrator orchestrator = host.Services.GetRequiredService<EnqueueOrchestrator>();
                    await orchestrator.RunAsync(lifetime.ApplicationStopping);

                    await host.StopAsync();
                }

                return ExitCodes.Success;
            }
            catch (OptionsValidationException exception)
            {
                // A settings problem is fixed by changing settings, not code: list what is wrong, no stack trace
                Log.Fatal("Invalid configuration, enqueuer not started: {ValidationFailures}", exception.Failures);
                return ExitCodes.InvalidConfiguration;
            }
            catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OptionsValidationException))
            {
                // Each settings group is checked on its own, so with more than one wrong, as on a new machine without
                // the queue name or the PAT, the host reports them together: still one line, exit 2
                IEnumerable<string> failures = exception.InnerExceptions.Cast<OptionsValidationException>().SelectMany(inner => inner.Failures);
                Log.Fatal("Invalid configuration, enqueuer not started: {ValidationFailures}", failures);
                return ExitCodes.InvalidConfiguration;
            }
            catch (AzureDevOpsAuthenticationException exception)
            {
                // Fixed by renewing the PAT, not code: one line, no stack trace. The scanner logs the same first words,
                // so one alert catches both jobs when the shared PAT expires (Issue 2 design, decision 7).
                Log.Fatal("Azure DevOps refused the token, enqueuer stopped: {Problem}", exception.Message);
                return ExitCodes.Failure;
            }
            catch (AzureDevOpsUnavailableException exception)
            {
                // The network, a timeout, another status or an answer that is not a repository list (decision 8):
                // one line saying which, no stack trace
                Log.Fatal("Azure DevOps could not list the repositories, enqueuer stopped: {Problem}", exception.Message);
                return ExitCodes.Failure;
            }
            catch (ScanQueueNotFoundException exception)
            {
                // Fixed by creating the queue or correcting its name, like any setting (Issue 3 design, decision 10). The
                // scanner logs the same first words.
                Log.Fatal("Queue missing, enqueuer stopped: {Problem}", exception.Message);
                return ExitCodes.InvalidConfiguration;
            }
            catch (ScanQueueUnavailableException exception)
            {
                // The queue could not be reached or refused the identity, after the SDK's retries (decisions 12 and 13)
                Log.Fatal("Scan requests could not be sent, enqueuer stopped: {Problem}", exception.Message);
                return ExitCodes.Failure;
            }
            catch (Exception exception)
            {
                // Anything else is unexpected: log it with the full exception so it can be diagnosed
                Log.Fatal(exception, "Enqueuer failed");
                return ExitCodes.Failure;
            }
            finally
            {
                // Write out anything still buffered before the process exits
                await Log.CloseAndFlushAsync();
            }
        }

        // One CLEF JSON object per line for Log Analytics, with the finished message and every property, or
        // the readable text line (#112, ENQ-ADR-002)
        private static LoggerConfiguration WriteToConsole(LoggerSinkConfiguration writeTo, bool json)
        {
            return json
                ? writeTo.Console(new RenderedCompactJsonFormatter())
                : writeTo.Console(outputTemplate: ConsoleOutputTemplate);
        }

        // Configures the host without running it, so tests can build it and inspect the services.
        public static HostApplicationBuilder CreateBuilder(string[] args)
        {
            // Sets up configuration (appsettings files, environment variables, command line),
            // dependency injection and default logging.
            // Content root is the app's own folder, so appsettings files are found wherever
            // the app is started from (dotnet run from the repo root, or /app in the container).
            HostApplicationBuilderSettings settings = new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            };
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(settings);

            // Remove the default logging providers (Console, Debug, EventSource, EventLog):
            // Serilog is the only place logs go
            builder.Logging.ClearProviders();

            // Text in Development, JSON elsewhere, unless Enqueuer:Logging:Format says (#112, Issue 4 design
            // decision 4, ENQ-ADR-002)
            bool json = ConsoleLogFormat.IsJson(builder.Configuration[ConsoleLogFormat.SettingName], builder.Environment.EnvironmentName);
            builder.Services.AddSerilog(logger => WriteToConsole(logger
                // Our own code logs from Information upwards
                .MinimumLevel.Information()
                // Framework messages (e.g. "Application started. Press Ctrl+C") only when they matter
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                // The host's own "Hosting failed to start" error is not needed: Main logs every
                // start-up failure once, with the matching exit code
                .MinimumLevel.Override("Microsoft.Extensions.Hosting", LogEventLevel.Fatal)
                // The HTTP client logs every request, and Polly every attempt, at Information: only their
                // warnings and errors are wanted (Issue 2 design, decision 15)
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .MinimumLevel.Override("Polly", LogEventLevel.Warning)
                // Lets later code add properties (e.g. CorrelationId, Issue 4) to every log line in a scope
                .Enrich.FromLogContext()
                .WriteTo, json),
                // Keep the start-up logger in Log.Logger: the host's logger is disposed with the host,
                // before Main's catch blocks run
                preserveStaticLogger: true);

            // How the console writes log lines: checked when the host starts, so a wrong Enqueuer:Logging:Format
            // is one line and exit 2 like any setting (#112)
            builder.Services.AddOptions<LoggingOptions>()
                .Bind(builder.Configuration.GetSection(LoggingOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Queue settings: read from the Enqueuer:Queue section, checked against the rules on
            // QueueOptions, and checked when the host starts rather than when first used
            builder.Services.AddOptions<QueueOptions>()
                .Bind(builder.Configuration.GetSection(QueueOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Azure DevOps settings, read from the Enqueuer:AzureDevOps section and checked the same way
            builder.Services.AddOptions<AzureDevOpsOptions>()
                .Bind(builder.Configuration.GetSection(AzureDevOpsOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Register the classes the host can create for us. The Azure DevOps client comes with its named HTTP
            // client, headers and retries (Issue 2 design, The call).
            AzureDevOpsRepositoryClient.Register(builder.Services);

            // The queue the scan requests go to: Azurite's connection string locally, the job's managed identity in
            // Azure (Issue 3 design, decision 8). Building the client makes no network call.
            builder.Services.AddSingleton<QueueClient>(services => QueueClients.Create(services.GetRequiredService<IOptions<QueueOptions>>().Value));
            builder.Services.AddSingleton<ScanRequestSender>();

            builder.Services.AddSingleton<EnqueueOrchestrator>();

            return builder;
        }
    }
}
