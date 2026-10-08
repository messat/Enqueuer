# ENQ-ADR-001: The enqueuer has its own repository and its own copy of the scan request

- **Status:** Accepted
- **Date:** 2026-10-08
- **Source:** Agreed 2026-10-01 for enqueuer Issue 1 (#79); repository created and ADR numbering decided 2026-10-08, task #102

## Context

The enqueuer is the scheduled job that lists every repository in the Azure DevOps organisation and puts one scan request per repository on `scan-nightly` (epic #78). It is a second console app, separate from the scanner. The scanner lives in the RepoScanner repository; the enqueuer does not.

It needs exactly one thing from RepoScanner: the format of the scan request, which the scanner reads and owns ([Issue 7 design, Message format](https://dev.azure.com/Blacklight-Internal/Lumina/_git/RepoScanner?path=/docs/design/2026-09-29-queue-consumer-and-execution-loop.md&version=GBdevelop)). It needs no database, no `RepoScanner.Data` and no scanner code. The format is six fields: `schemaVersion`, `correlationId`, `repositoryId`, `projectName`, `repositoryName` and `defaultBranch`.

RepoScanner ADR-029 ([This release runs locally, without CI/CD pipelines](https://dev.azure.com/Blacklight-Internal/Lumina/_git/RepoScanner?path=/docs/adr/ADR-029-this-release-runs-locally-without-pipelines.md&version=GBdevelop)) keeps the enqueuer in its own repository and leaves how that repository is set up to this decision. No pipeline is built in this release, so nothing could publish a shared package.

## Decision

**Its own repository.** The enqueuer's code, tests and history live in the `Enqueuer` repository in the Lumina project, not in RepoScanner. A change to one app is reviewed, merged and released without touching the other.

**Its own copy of the scan request.** The enqueuer keeps a small `ScanRequest` type of its own rather than referencing RepoScanner's or a shared NuGet package. A package would need a feed and a pipeline to publish it, which this release does not have (RepoScanner ADR-029), for a type of six fields; RepoScanner ADR-010 rejects that kind of complexity.

**One example message keeps the two copies in step.** RepoScanner's `docs/contracts/scan-request.example.json` (#101) is copied into this repository at the same path. The enqueuer's contract test (#109) checks every message it builds against the example's fields and rules; the scanner's own test reads the same file and accepts it. A change to the format changes the example in both repositories together, with a `schemaVersion` bump when an older scanner could not read the new message safely. The scanner's change is merged first, so the scanner can read a new message before the enqueuer sends one.

**Branches and pull requests follow RepoScanner ADR-020, as amended by ADR-029.** `develop` is the default branch. Work happens on `feature/<work-item-id>-<short-name>` branches and reaches `develop` only through a pull request. `develop` and `main` each require one reviewer (self-approval allowed), a linked work item and resolved comments, with no build validation policy. Commit messages start "This commit ..." and end with the task's `#<id>`.

**ADRs are numbered ENQ-ADR-001 onwards.** This repository's decisions never take a RepoScanner number, so a bare "ADR-014" on the board always means RepoScanner's. A RepoScanner decision is cited here as "RepoScanner ADR-014". RepoScanner's ADR-001 to ADR-019 describe the whole system, the enqueuer included, and still apply.

## Consequences

Two copies of the scan request exist, and nothing but the contract tests ties them together. A copy changed on one side alone passes its own build. In Azure the scanner would move any message it cannot read, including one with an unknown `schemaVersion`, to its poison queue on the first read and log the warning its alert watches, so drift would be seen, but only after a night's messages were lost.

Build settings are copied too: the enqueuer starts from RepoScanner's `Directory.Build.props`, `Directory.Packages.props` and `global.json` (#104). A later change to either repository's build rules is not carried to the other.

Nothing runs the tests on a pull request. `dotnet test`, with its 80% line coverage gate, is run by hand before a pull request is completed (RepoScanner ADR-029). If the pipelines come back, enqueuer Issue 6 (#84) gives this repository its own pipeline, as planned on 2026-10-02, and this ADR gets a dated amendment.
