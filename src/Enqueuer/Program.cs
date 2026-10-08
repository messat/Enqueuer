using System;
using System.Threading.Tasks;
using Enqueuer.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

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
            Log.Logger = new LoggerConfiguration()
                .Enrich.WithProperty("SourceContext", typeof(Program).FullName)
                .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
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

        // Configures the host without running it, so tests can build it and inspect the services.
        public static HostApplicationBuilder CreateBuilder(string[] args)
        {
            // Sets up configuration (appsettings.json, user-secrets in Development,
            // environment variables, command line), dependency injection and default logging.
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

            builder.Services.AddSerilog(logger => logger
                // Our own code logs from Information upwards
                .MinimumLevel.Information()
                // Framework messages (e.g. "Application started. Press Ctrl+C") only when they matter
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                // The host's own "Hosting failed to start" error is not needed: Main logs every
                // start-up failure once, with the matching exit code
                .MinimumLevel.Override("Microsoft.Extensions.Hosting", LogEventLevel.Fatal)
                // Lets later code add properties (e.g. CorrelationId, Issue 4) to every log line in a scope
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: ConsoleOutputTemplate),
                // Keep the start-up logger in Log.Logger: the host's logger is disposed with the host,
                // before Main's catch blocks run
                preserveStaticLogger: true);

            // Queue settings: read from the Enqueuer:Queue section, checked against the rules on
            // QueueOptions, and checked when the host starts rather than when first used
            builder.Services.AddOptions<QueueOptions>()
                .Bind(builder.Configuration.GetSection(QueueOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Register the classes the host can create for us
            builder.Services.AddSingleton<EnqueueOrchestrator>();

            return builder;
        }
    }
}
