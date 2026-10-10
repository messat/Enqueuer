using System.ComponentModel.DataAnnotations;

namespace Enqueuer.Configuration
{
    // How the console writes log lines (#112, Issue 4 design decision 4, ENQ-ADR-002): read from the Enqueuer:Logging
    // section and checked when the host starts, like every settings group, so a wrong value is one line and exit 2.
    // As RepoScanner's LoggingOptions, with Enqueuer: for Scanner:.
    public sealed record LoggingOptions
    {
        public const string SectionName = "Enqueuer:Logging";

        // Text or Json, in any case. Not set: text in Development, JSON everywhere else (ConsoleLogFormat).
        [RegularExpression("^(?i:text|json)$", ErrorMessage = "Enqueuer:Logging:Format must be Text or Json")]
        public string? Format { get; init; }
    }
}
