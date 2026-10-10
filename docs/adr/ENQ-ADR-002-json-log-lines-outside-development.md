# ENQ-ADR-002: JSON log lines on stdout outside Development

- **Status:** Accepted
- **Date:** 2026-10-09
- **Source:** Decided while designing Issue 4 (#82), task #112; the design is `docs/design/2026-10-09-observability-and-run-summary.md`

## Context

The scanner writes one CLEF JSON object per line outside Development ([RepoScanner ADR-028](https://dev.azure.com/Blacklight-Internal/Lumina/_git/RepoScanner?path=/docs/adr/ADR-028-json-log-lines-on-stdout-outside-development.md&version=GBdevelop)), so Log Analytics can filter its lines on `CorrelationId`. The enqueuer makes each request's correlation ID and logs it on its `Queued` line, but writes text, so a search could only match its words. Only RepoScanner's ADR-001 to ADR-019 apply to the enqueuer without being adopted (ENQ-ADR-001).

## Decision

The enqueuer follows RepoScanner ADR-028: outside Development, both its loggers write CLEF with `RenderedCompactJsonFormatter` from `Serilog.Formatting.Compact`, every property at the top level. Development writes text. `Enqueuer:Logging:Format` (`Text` or `Json`, in any case) overrides the environment; any other value stops the enqueuer at start-up with exit code 2. The code is copied, not shared, as the scan request is.

## Consequences

One search on `CorrelationId` reads both apps' lines. Property names become part of the searches, so the enqueuer avoids the scanner's names where they mean something else (`EnqueueRunStatus`, `EnqueueDurationMs`, `SkippedRepositoryCount`). The two copies of `ConsoleLogFormat` are kept alike by hand. One more package to keep up to date. The start-up logger exists before configuration is read, so it takes the setting from environment variables and the command line only, not from appsettings files.
