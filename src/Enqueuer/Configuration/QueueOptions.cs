using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Enqueuer.Configuration
{
    // Settings for the storage queue the scan requests are sent to, bound from the
    // "Enqueuer:Queue" configuration section and validated when the host starts.
    public sealed record QueueOptions : IValidatableObject
    {
        public const string SectionName = "Enqueuer:Queue";

        // Azure Storage queue names: 3 to 63 characters, lowercase letters, numbers and hyphens
        private const string QueueNamePattern = "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$";
        private const string QueueNameMessage = "{0} must be 3 to 63 characters of lowercase letters, numbers and hyphens.";

        // scan-nightly: the queue name is configuration, not code, as it is for the scanner (RepoScanner ADR-011)
        [Required]
        [RegularExpression(QueueNamePattern, ErrorMessage = QueueNameMessage)]
        public string Name { get; init; } = string.Empty;

        // Local development only (Azurite): UseDevelopmentStorage=true
        public string? ConnectionString { get; init; }

        // Azure only, used with DefaultAzureCredential (managed identity, RepoScanner ADR-007). Text, checked
        // below: as a Uri, a value the configuration binder cannot read would fail before validation runs,
        // exiting 1 with a stack trace instead of 2 with one line.
        public string? ServiceUri { get; init; }

        // Rules that involve more than one property. Runs after the attribute checks above pass.
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            bool hasConnectionString = !string.IsNullOrWhiteSpace(ConnectionString);
            bool hasServiceUri = !string.IsNullOrWhiteSpace(ServiceUri);

            if (hasConnectionString == hasServiceUri)
            {
                yield return new ValidationResult(
                    "Exactly one of Enqueuer:Queue:ConnectionString (local) or Enqueuer:Queue:ServiceUri (Azure) must be set.",
                    new[] { nameof(ConnectionString), nameof(ServiceUri) });
            }

            // DefaultAzureCredential sends its token only over https
            if (hasServiceUri
                && !(Uri.TryCreate(ServiceUri, UriKind.Absolute, out Uri? serviceUri) && serviceUri.Scheme == Uri.UriSchemeHttps))
            {
                yield return new ValidationResult(
                    "Enqueuer:Queue:ServiceUri must be an absolute https URL, such as https://<account>.queue.core.windows.net.",
                    new[] { nameof(ServiceUri) });
            }
        }
    }
}
