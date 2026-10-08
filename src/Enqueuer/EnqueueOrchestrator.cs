using System.Threading;
using System.Threading.Tasks;
using Enqueuer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enqueuer
{
    // Runs one execution of the enqueuer job: list every repository, send one scan request each,
    // then return. The listing arrives in Issue 2 and the sending in Issue 3; until then there is
    // never anything to queue.
    public sealed class EnqueueOrchestrator
    {
        private readonly ILogger<EnqueueOrchestrator> _logger;
        private readonly IHostEnvironment _environment;
        private readonly QueueOptions _queueOptions;

        public EnqueueOrchestrator(ILogger<EnqueueOrchestrator> logger, IHostEnvironment environment, IOptions<QueueOptions> queueOptions)
        {
            _logger = logger;
            _environment = environment;
            _queueOptions = queueOptions.Value;
        }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            // {EnvironmentName} and {QueueName} are named properties, not string formatting: the values
            // are kept as separate fields on the log event so they can be queried later
            _logger.LogInformation("Enqueuer starting in {EnvironmentName} for queue {QueueName}", _environment.EnvironmentName, _queueOptions.Name);
            _logger.LogInformation("No repositories to queue, exiting");

            return Task.CompletedTask;
        }
    }
}
