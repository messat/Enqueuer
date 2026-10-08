using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enqueuer.AzureDevOps;
using Enqueuer.Configuration;
using Enqueuer.Queue;
using Enqueuer.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enqueuer
{
    // Runs one execution of the enqueuer job: list every repository, send one scan request each,
    // then return. The listing is Issue 2 and the sending Issue 3.
    public sealed class EnqueueOrchestrator
    {
        private readonly ILogger<EnqueueOrchestrator> _logger;
        private readonly IHostEnvironment _environment;
        private readonly QueueOptions _queueOptions;
        private readonly AzureDevOpsOptions _azureDevOpsOptions;
        private readonly AzureDevOpsRepositoryClient _repositoryClient;
        private readonly ScanRequestSender _sender;

        public EnqueueOrchestrator(
            ILogger<EnqueueOrchestrator> logger,
            IHostEnvironment environment,
            IOptions<QueueOptions> queueOptions,
            IOptions<AzureDevOpsOptions> azureDevOpsOptions,
            AzureDevOpsRepositoryClient repositoryClient,
            ScanRequestSender sender)
        {
            _logger = logger;
            _environment = environment;
            _queueOptions = queueOptions.Value;
            _azureDevOpsOptions = azureDevOpsOptions.Value;
            _repositoryClient = repositoryClient;
            _sender = sender;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            // {EnvironmentName} and {QueueName} are named properties, not string formatting: the values
            // are kept as separate fields on the log event so they can be queried later
            _logger.LogInformation("Enqueuer starting in {EnvironmentName} for queue {QueueName}", _environment.EnvironmentName, _queueOptions.Name);

            // Every repository in every project, with one call (Issue 2 design, decision 1)
            IReadOnlyList<AzureDevOpsRepository> repositories = await _repositoryClient.ListAsync(cancellationToken);
            RepositorySelection selection = RepositorySelector.Select(repositories);

            // One line per skipped repository with its reason (criterion 2). {SkipReason} is the enum's name, so
            // Issue 4 can count skips by reason.
            foreach (SkippedRepository skipped in selection.Skipped)
            {
                _logger.LogInformation("Skipped {ProjectName}/{RepositoryName}: {SkipReason}", skipped.ProjectName, skipped.Name, skipped.Reason);
            }

            _logger.LogInformation(
                "Listed {ListedCount} repositories in {OrganizationUrl}: {ToScanCount} to queue, {SkippedCount} skipped",
                repositories.Count,
                _azureDevOpsOptions.OrganizationUrl,
                selection.ToScan.Count,
                selection.Skipped.Count);

            // A PAT whose owner can see no project lists nothing rather than being refused: not an error the job can
            // fix, but it must not pass silently (decision 14)
            if (repositories.Count == 0)
            {
                _logger.LogWarning("Azure DevOps listed no repositories in {OrganizationUrl}. Check that the PAT's owner can see its projects", _azureDevOpsOptions.OrganizationUrl);
            }

            // One at a time, in the order listed (Issue 3 design, decision 17). A send that still fails after the SDK's
            // retries throws and ends the run here: the requests already sent stay on the queue (decision 12).
            foreach (RepositoryToScan repository in selection.ToScan)
            {
                // A new correlation ID for each message; the scanner keeps it on its scan run and log lines (decision 4)
                ScanRequest request = ScanRequest.For(repository, Guid.NewGuid());
                await _sender.SendAsync(request, cancellationToken);

                // Logged only once sent, so after a failure these lines are exactly the requests on the queue
                // (decision 15)
                _logger.LogInformation(
                    "Queued {ProjectName}/{RepositoryName} with correlation {CorrelationId}",
                    request.ProjectName,
                    request.RepositoryName,
                    request.CorrelationId);
            }

            _logger.LogInformation("Sent {SentCount} scan requests to {QueueName}", selection.ToScan.Count, _queueOptions.Name);
        }
    }
}
