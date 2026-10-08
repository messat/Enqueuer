using System;
using Azure.Identity;
using Azure.Storage.Queues;
using Enqueuer.Configuration;

namespace Enqueuer.Queue
{
    // Builds the client for Enqueuer:Queue from the validated settings (Issue 3 design, decision 8), as RepoScanner's
    // QueueClients does for one queue. Building it makes no network call.
    public static class QueueClients
    {
        public static QueueClient Create(QueueOptions options)
        {
            // Messages are plain text, not Base64, as the scanner reads them (decision 2). None is the SDK's default;
            // it is set here so that it cannot change unnoticed.
            QueueClientOptions clientOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None };

            if (!string.IsNullOrWhiteSpace(options.ServiceUri))
            {
                // Azure: no secret, the job's managed identity. AZURE_CLIENT_ID picks id-scanner (RepoScanner ADR-014).
                // QueueOptions has already checked that ServiceUri is an absolute https URL.
                Uri queueUri = new QueueUriBuilder(new Uri(options.ServiceUri)) { QueueName = options.Name }.ToUri();
                return new QueueClient(queueUri, new DefaultAzureCredential(), clientOptions);
            }

            // Local: Azurite's connection string. QueueOptions requires one of the two, so this is only reached without
            // one if the settings were never validated.
            string connectionString = options.ConnectionString
                ?? throw new InvalidOperationException("Enqueuer:Queue:ConnectionString or Enqueuer:Queue:ServiceUri must be set.");
            return new QueueClient(connectionString, options.Name, clientOptions);
        }
    }
}
