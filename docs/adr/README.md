# Architecture Decision Records

Each file records one decision: the context that forced it, what was decided, and what follows from it.

This repository's decisions are numbered ENQ-ADR-001 onwards, so they never clash with RepoScanner's ADR numbers. A RepoScanner decision is cited as "RepoScanner ADR-014". RepoScanner's ADR-001 to ADR-019 describe the whole system, the enqueuer included, and still apply here: see [RepoScanner's ADR index](https://dev.azure.com/Blacklight-Internal/Lumina/_git/RepoScanner?path=/docs/adr/README.md&version=GBdevelop). The next new ADR is **ENQ-ADR-003**.

| ADR | Decision | Status |
|---|---|---|
| [ENQ-ADR-001](ENQ-ADR-001-own-repository-and-own-copy-of-the-scan-request.md) | The enqueuer has its own repository and its own copy of the scan request | Accepted |
| [ENQ-ADR-002](ENQ-ADR-002-json-log-lines-outside-development.md) | JSON log lines on stdout outside Development | Accepted |

## Writing a new ADR

Copy the layout of an existing file: a title `# ENQ-ADR-NNN: <decision>`, the status, date and source, then `## Context`, `## Decision` and `## Consequences`. A decision is never edited to mean something else: it is amended with a dated note, or superseded by a new ADR that says which one it replaces. An ADR is added in the same pull request as the change it explains.
