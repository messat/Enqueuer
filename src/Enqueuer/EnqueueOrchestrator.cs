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
    // then return. The listing is Issue 2, the sending Issue 3, and the summary line that ends every run Issue 4.
    public sealed class EnqueueOrchestrator
    {
        // The summary line (#113), logged last in every run that gets past start-up (Issue 4 design, decisions 2 and 5).
        // Its property names are what the Log Analytics searches use, and are not the scanner's where the same name would
        // mean something else (Issue 4 design, decision 6).
        private const string SummaryTemplate =
            "Enqueue run {EnqueueRunStatus} in {EnqueueDurationMs} ms: {ListedCount} listed in {OrganizationUrl}, {ToScanCount} to queue, "
            + "{SkippedRepositoryCount} skipped {SkippedByReason}, {SentCount} sent to {QueueName}, {NotSentCount} not sent";

        // The same properties with the reason added, so one search reads both (design, The summary line)
        private const string FailedSummaryTemplate = SummaryTemplate + ". Error: {ErrorMessage}";

        private readonly ILogger<EnqueueOrchestrator> _logger;
        private readonly IHostEnvironment _environment;
        private readonly QueueOptions _queueOptions;
        private readonly AzureDevOpsOptions _azureDevOpsOptions;
        private readonly AzureDevOpsRepositoryClient _repositoryClient;
        private readonly ScanRequestSender _sender;
        private readonly TimeProvider _timeProvider;

        public EnqueueOrchestrator(
            ILogger<EnqueueOrchestrator> logger,
            IHostEnvironment environment,
            IOptions<QueueOptions> queueOptions,
            IOptions<AzureDevOpsOptions> azureDevOpsOptions,
            AzureDevOpsRepositoryClient repositoryClient,
            ScanRequestSender sender,
            TimeProvider timeProvider)
        {
            _logger = logger;
            _environment = environment;
            _queueOptions = queueOptions.Value;
            _azureDevOpsOptions = azureDevOpsOptions.Value;
            _repositoryClient = repositoryClient;
            _sender = sender;
            _timeProvider = timeProvider;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            // Read before the first line, so the duration the summary gives is the whole run (Issue 4 design, decision 11)
            DateTimeOffset started = _timeProvider.GetUtcNow();

            // Made before the try, so the catch can log what the run had counted when it stopped (design, How RunAsync keeps
            // its counts)
            RunCounts counts = new RunCounts();

            // {EnvironmentName} and {QueueName} are named properties, not string formatting: the values
            // are kept as separate fields on the log event so they can be queried later
            _logger.LogInformation("Enqueuer starting in {EnvironmentName} for queue {QueueName}", _environment.EnvironmentName, _queueOptions.Name);

            // Everything after the start line is inside the try, so whatever ends the run, the summary is its last line
            // (Issue 4 design, decision 2)
            try
            {
                // Every repository in every project, with one call (Issue 2 design, decision 1)
                IReadOnlyList<AzureDevOpsRepository> repositories = await _repositoryClient.ListAsync(cancellationToken);
                RepositorySelection selection = RepositorySelector.Select(repositories);

                counts.ListedCount = repositories.Count;
                counts.ToScanCount = selection.ToScan.Count;
                counts.SkippedRepositoryCount = selection.Skipped.Count;

                // One line per skipped repository with its reason (criterion 2). {SkipReason} is the enum's name, and the
                // summary counts the skips by it (Issue 4 design, decision 14).
                foreach (SkippedRepository skipped in selection.Skipped)
                {
                    counts.SkippedByReason[skipped.Reason.ToString()]++;
                    _logger.LogInformation("Skipped {ProjectName}/{RepositoryName}: {SkipReason}", skipped.ProjectName, skipped.Name, skipped.Reason);
                }

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

                    // Counted only once sent, so after a failure the summary's NotSentCount is the requests not on the queue
                    // (Issue 4 design, decision 3)
                    counts.SentCount++;

                    // Logged only once sent, so after a failure these lines are exactly the requests on the queue
                    // (decision 15)
                    _logger.LogInformation(
                        "Queued {ProjectName}/{RepositoryName} with correlation {CorrelationId}",
                        request.ProjectName,
                        request.RepositoryName,
                        request.CorrelationId);
                }

                LogSummary(EnqueueRunStatus.Completed, started, counts, null);
            }
            catch (Exception exception)
            {
                // Stopped only when the job's own token was cancelled, as Ctrl+C or SIGTERM does. A cancellation it did not
                // cause, such as a timeout, and every other exception, a bug included, is Failed (Issue 4 design, decision 12).
                EnqueueRunStatus status = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? EnqueueRunStatus.Stopped
                    : EnqueueRunStatus.Failed;
                LogSummary(status, started, counts, exception.Message);

                // Unchanged, so Program's catch blocks still log their one Fatal line and pick the exit code, and a bug keeps
                // its stack trace (Issue 4 design, decisions 5 and 13)
                throw;
            }
        }

        // The one summary line: Information when the run completed, a warning with the reason added when it did not
        private void LogSummary(EnqueueRunStatus status, DateTimeOffset started, RunCounts counts, string? errorMessage)
        {
            // Read when the summary is written, so it measures the run, not the start-up (Issue 4 design, decision 11)
            long durationMs = (long)(_timeProvider.GetUtcNow() - started).TotalMilliseconds;

            // The repositories chosen to queue that were not sent: the run stops at the first failed send, so the rest are
            // never tried, and ToScanCount is always SentCount plus this (Issue 4 design, decision 3)
            int notSentCount = counts.ToScanCount - counts.SentCount;

            if (status == EnqueueRunStatus.Completed)
            {
                _logger.LogInformation(
                    SummaryTemplate,
                    status,
                    durationMs,
                    counts.ListedCount,
                    _azureDevOpsOptions.OrganizationUrl,
                    counts.ToScanCount,
                    counts.SkippedRepositoryCount,
                    counts.SkippedByReason,
                    counts.SentCount,
                    _queueOptions.Name,
                    notSentCount);
            }
            else
            {
                _logger.LogWarning(
                    FailedSummaryTemplate,
                    status,
                    durationMs,
                    counts.ListedCount,
                    _azureDevOpsOptions.OrganizationUrl,
                    counts.ToScanCount,
                    counts.SkippedRepositoryCount,
                    counts.SkippedByReason,
                    counts.SentCount,
                    _queueOptions.Name,
                    notSentCount,
                    errorMessage);
            }
        }

        // What one run has counted so far. All 0 until the run sets them, so a run that stops before the listing, or
        // during it, logs 0 listed (design, How RunAsync keeps its counts).
        private sealed class RunCounts
        {
            public RunCounts()
            {
                // Every reason at 0, in the enum's order, so a search can read any of them without checking it exists
                // (Issue 4 design, decision 14)
                foreach (SkipReason reason in Enum.GetValues<SkipReason>())
                {
                    SkippedByReason[reason.ToString()] = 0;
                }
            }

            public int ListedCount { get; set; }

            public int ToScanCount { get; set; }

            public int SkippedRepositoryCount { get; set; }

            public int SentCount { get; set; }

            // A dictionary, so Serilog writes it as one JSON object, {"Disabled":0,"InMaintenance":0,"Empty":0}
            public Dictionary<string, int> SkippedByReason { get; } = new Dictionary<string, int>();
        }
    }
}
