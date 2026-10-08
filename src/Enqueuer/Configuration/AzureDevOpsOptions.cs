using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Enqueuer.Configuration
{
    // Where the repositories are listed from, bound from the "Enqueuer:AzureDevOps" configuration section and
    // validated when the host starts (RepoScanner ADR-022)
    public sealed record AzureDevOpsOptions : IValidatableObject
    {
        public const string SectionName = "Enqueuer:AzureDevOps";

        // https://dev.azure.com/<organisation>: the one the scanner clones from, since a PAT belongs to one
        // organisation (Issue 2 design, decision 2)
        public string OrganizationUrl { get; init; } = string.Empty;

        // The scanner's personal access token, Code (Read) only (decision 4). Locally the user environment variable
        // Enqueuer__AzureDevOps__Pat (decision 3), in Azure a Key Vault secret reference; never an appsettings file
        // (RepoScanner ADR-007). [Required] also rejects an empty or whitespace-only value.
        [Required(ErrorMessage = "Enqueuer:AzureDevOps:Pat is required. Locally, set the Enqueuer__AzureDevOps__Pat user environment variable to the scanner's personal access token, which has Code (Read).")]
        public string Pat { get; init; } = string.Empty;

        // Runs after the attribute checks above pass. The message never repeats the value, which could hold a password.
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            // https only, so the PAT is never sent in the clear. A user name in the URL, as Azure DevOps' Clone
            // dialog offers it, is the pattern RepoScanner flags.
            bool valid = Uri.TryCreate(OrganizationUrl, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.UserInfo.Length == 0;

            if (!valid)
            {
                yield return new ValidationResult(
                    "Enqueuer:AzureDevOps:OrganizationUrl must be an https URL such as https://dev.azure.com/<organisation>, with no user name in it",
                    new[] { nameof(OrganizationUrl) });
            }
        }

        // A record's ToString prints every property, so a settings object in a log line would print the PAT
        private bool PrintMembers(StringBuilder builder)
        {
            builder.Append("OrganizationUrl = ").Append(OrganizationUrl).Append(", Pat = ***");
            return true;
        }
    }
}
