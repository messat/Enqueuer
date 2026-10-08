# Issue 3: Scan request sending (design)

- **Status:** Draft, for review
- **Date:** 2026-10-08
- **Work item:** Issue 3, #81 (tasks #109 to #111)
- **Branch:** `feature/81-scan-request-sending`

## Purpose

After Issue 2 the enqueuer lists the repositories, chooses which to scan, logs what it chose, and sends nothing: its last line is `Nothing sent: sending scan requests arrives in Issue 3`. This Issue sends one scan request per chosen repository to `scan-nightly`, in exactly the format the scanner reads ([RepoScanner Issue 7 design, Message format](https://dev.azure.com/Blacklight-Internal/Lumina/_git/RepoScanner?path=/docs/design/2026-09-29-queue-consumer-and-execution-loop.md&version=GBdevelop)), so the scanner's nightly job has its night's work.

It is the first time the enqueuer writes anything, and the second service it talks to, after Azure DevOps.

```
[Azure DevOps] --list--> [Enqueuer] --one message per repository--> [scan-nightly] --> [Scanner]
                Issue 2              Issue 3                                        RepoScanner Issue 7
```

What happens in one run:

1. Settings are checked at start-up, as today. There are no new settings.
2. The repositories are listed and chosen, as in Issue 2.
3. For each repository to scan, in the order Azure DevOps listed them, a scan request is built with a new correlation ID and sent to `scan-nightly`.
4. One line per message names its repository and correlation ID (decision 15), one line says how many were sent, and the enqueuer exits 0.
5. If the queue does not exist, one line says so and the enqueuer exits 2. If a send still fails after its retries, one line says which and the enqueuer exits 1.

## Success criteria

From Issue 3 (#81):

1. One message per listed repository lands on `scan-nightly` in Azurite, and the scanner scans it.
2. Every message matches the example message from the Issue 7 design (contract test), and the scanner has a test that accepts the same example.
3. A failed send exits 1; a missing queue exits 2.

"Listed" in criterion 1 means chosen to scan: a repository Issue 2 skips gets no message. The second half of criterion 2 is already met: RepoScanner's `ScanRequestReaderTests.TryRead_TheExampleInDocs_IsAccepted` reads its `docs/contracts/scan-request.example.json` and accepts it (RepoScanner Issue 11, #101, Done).

## Decisions

Decisions 12, 15 and 19 were agreed in the design discussion on 2026-10-08. The rest are proposed here, for review. Decisions 8, 9 and 11 rest on Microsoft's documentation, checked on 2026-10-08.

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | The format | The scanner's, unchanged: `schemaVersion`, `correlationId`, `repositoryId`, `projectName`, `repositoryName` and `defaultBranch` | The scanner reads it and owns it (RepoScanner Issue 7 design, decision 1). The enqueuer keeps its own copy of `ScanRequest` (ENQ-ADR-001) |
| 2 | How it is encoded | Plain UTF-8 JSON, not Base64. `QueueMessageEncoding.None` is set on the client, though it is the SDK's default | The scanner reads plain JSON and moves a Base64 body to the poison queue (RepoScanner Issue 7 design, decision 3). Set in the code, as the scanner does, so a change of default cannot alter it unnoticed |
| 3 | How the JSON is written | By `ScanRequestWriter`, with `Utf8JsonWriter`: the six names in the example's camelCase and order, written out in the code, with no indentation | Renaming a C# property cannot change what is sent. The scanner ignores case and order, but a message that looks like the example is easier to check in Storage Explorer. No indentation, as RepoScanner's `enqueue-local.ps1` sends it |
| 4 | The correlation ID | A new `Guid.NewGuid()` per message, made by the orchestrator and passed to `ScanRequest.For`. Never reused across messages or runs | The scanner never makes one for a queue message: it reads it, saves it as `ScanRuns.CorrelationId` and puts it on every log line about the scan (RepoScanner Issue 10 design, decision 1). The dashboard makes its own for each Scan now click. Made outside the builder, so the builder is a pure function and the contract test can rebuild the example exactly |
| 5 | The contract | `docs/contracts/scan-request.example.json` copied unchanged from RepoScanner, at the same path. The contract test (#109) checks that a request built from the example's own values is written as JSON equal to the example, and that every message the enqueuer builds keeps the example's field names, value kinds and rules | ENQ-ADR-001. Equal JSON catches a renamed, missing, extra or retyped field. The rules catch a value the scanner would refuse |
| 6 | How the test finds the example | A `RepositoryRoot` test helper that walks up from the test's folder to `Enqueuer.sln`, as RepoScanner's tests find theirs | The file stays in `docs/contracts`, where ENQ-ADR-001 puts it, with no second copy in the test project |
| 7 | Which queue | `Enqueuer:Queue:Name`, `scan-nightly` in `Development`. Issue 1's `QueueOptions` already has `Name`, `ConnectionString` and `ServiceUri` | The queue name is configuration, not code (RepoScanner ADR-011). The enqueuer never sends to `scan-ondemand`: that is the dashboard's |
| 8 | Signing in | Locally, `ConnectionString`: `UseDevelopmentStorage=true`, Azurite. In Azure, `ServiceUri` with `DefaultAzureCredential`, `AZURE_CLIENT_ID` picking `id-scanner`, which holds Storage Queue Data Message Sender on `scan-nightly` only | RepoScanner ADR-007 and ADR-014. Built now, as the scanner did (its Issue 7 design, decision 19), so moving to Azure is a settings change. Message Sender allows adding messages and nothing else. The role and `AZURE_CLIENT_ID` are Issue 6's |
| 9 | Creating the queue | Never. Locally it is created once by hand, with `az storage queue create` (Local setup) | Message Sender cannot create a queue. The scanner never creates one either (its Issue 7 design, decision 18) |
| 10 | A missing queue | Found on the first send (Azure's `QueueNotFound`) and thrown as `ScanQueueNotFoundException`. `Program` logs `Queue missing, enqueuer stopped: {Problem}` as one line and exits 2 | Fixed like a setting, by creating the queue or correcting its name, as Issue 1's exit code 2 already says. Not checked at start-up, because Message Sender cannot read a queue's properties. The same first words as the scanner's line |
| 11 | Retries | The storage SDK's own: 5 retries, exponential from 0.8 seconds up to 1 minute apart, 100 seconds per attempt. Nothing added | Microsoft's defaults for storage clients, which raise Azure.Core's 3 retries to 5. They already ride out a brief storage blip. Issue 2's resilience handler is for `HttpClient`, which the storage SDK does not use |
| 12 | A failed send | Stop at the first request that still fails after its retries, and exit 1. The requests already sent stay on the queue, and the rest are not tried | After 5 retries a send fails because the queue cannot be reached or the identity is refused, so the next request would fail the same way, after its own retries. Every request comes from one tested writer, so no single message is bad on its own. A re-run sends every repository again (decision 14). Sending the rest and then exiting 1 was turned down: it saves the other repositories only when the failure belongs to one message, and when the queue times out it spends about 10 minutes of retries on each one |
| 13 | What a failed send logs | `Program` logs `Scan requests could not be sent, enqueuer stopped: {Problem}` as one line and exits 1. `{Problem}` names the repository, the queue and Azure's status and error code, never the SDK's own message (When a send fails) | The SDK's message runs to many lines, with every response header. One line, as Issue 2's failures are. The original is kept as the inner exception |
| 14 | Duplicates | Not prevented. A re-run after a failure, or the job's own retry in Azure (Issue 6), sends every repository again | Storage queues deliver at least once, so the scanner must cope with duplicates anyway. Its Issue 13 (#76) drops a request already satisfied by a scan that started after it was queued. Until #76 is built a duplicate is scanned again, which is harmless: findings are matched on fingerprint (RepoScanner ADR-024), and a repository being scanned is skipped (RepoScanner ADR-027) |
| 15 | Logging | One Information line per message, `Queued {ProjectName}/{RepositoryName} with correlation {CorrelationId}`, logged once it is sent, then `Sent {SentCount} scan requests to {QueueName}` in place of `Nothing sent: ...` | The correlation ID is how a sent request is found in the scanner's `ScanRuns` table and log (#111), and after a failed send the `Queued` lines show exactly which requests reached the queue. This brings forward part of Issue 4's notes, which ask for each correlation ID to be logged with its repository: logged as a named property now, Issue 4 only adds the run summary and the Log Analytics query. Leaving every per-message line to Issue 4 was turned down: #111 would have to read the IDs off the queue with `az storage message peek`, and a failed run would not show what was sent |
| 16 | Message lifetime | Visible at once. Azure's default time-to-live, 7 days | A message that expired unread would be a repository not scanned, with nothing to say so. Older duplicates are #76's to drop, not the queue's |
| 17 | Order and pace | One at a time, in the order Azure DevOps listed them | Each send takes milliseconds, so a few hundred repositories take seconds. Sending in parallel would only make stopping at the first failure harder |
| 18 | Tests | `FakeQueueClient`, a hand-written `QueueClient` like RepoScanner's, stands in for the real one in the sender's, the orchestrator's and `Program`'s tests. `dotnet test` still needs no Azurite, no network and no PAT | As Issue 2's `FakeAzureDevOps` replaces the HTTP handler, so the real sender and its error handling run in every test. `ProgramTests` would otherwise reach Azurite |
| 19 | The end-to-end check (#111) | By hand: the numbered steps under End to end, in this design and the README, with what each printed in the pull request. No script | It proves the Issue once. Issue 5 proves the container queuing to Azurite too, and can script both then. A `scripts/test-end-to-end.ps1` like RepoScanner's `test-container.ps1` was turned down for now: repeatable, but a few hundred lines of PowerShell to keep |
| 20 | New packages | `Azure.Storage.Queues` 12.27.1 and `Azure.Identity` 1.21.0, the versions in RepoScanner's `Directory.Packages.props` | Decision 8. Copied, not kept in step (ENQ-ADR-001) |
| 21 | New ADRs | None | RepoScanner ADR-004, ADR-007, ADR-013 and ADR-014, and ENQ-ADR-001, already decide the queue, the identity, its role and the contract |
| 22 | Where this document lives | `docs/design/`, beside Issue 2's | As decided in Issue 2 |

## Project layout

```
src/Enqueuer/
  Queue/           ScanRequest, ScanRequestWriter                         #109
                   QueueClients, ScanRequestSender,
                   ScanQueueNotFoundException,
                   ScanQueueUnavailableException                          #110
  EnqueueOrchestrator.cs    builds, sends and logs one request each       #110
  Program.cs                registrations; the two one-line failures      #110
  Enqueuer.csproj           + Azure.Storage.Queues, Azure.Identity        #110

Directory.Packages.props    + Azure.Storage.Queues 12.27.1,
                              Azure.Identity 1.21.0                       #110

docs/contracts/scan-request.example.json   copied from RepoScanner       #109

tests/Enqueuer.Tests/
  RepositoryRoot.cs (test helper)                                         #109
  Queue/           ScanRequestTests, ScanRequestWriterTests,
                   ScanRequestContract (test helper)                      #109
                   QueueClientsTests, ScanRequestSenderTests,
                   FakeQueueClient (test helper)                          #110
  EnqueueOrchestratorTests.cs, ProgramTests.cs   updated                  #110

README.md                                                                 #110, #111
```

`ExitCodes.cs` is unchanged: Issue 1 already wrote 1 as "a send failed" and 2 as "the queue is missing". Classes are `sealed` or `static` and follow RepoScanner ADR-021, as in Issues 1 and 2. `QueueClient`, `ScanRequestSender` and `EnqueueOrchestrator` are singletons: the job runs once.

## The message (#109)

```csharp
namespace Enqueuer.Queue
{
    // One scan request, in the format the scanner reads (RepoScanner Issue 7 design, Message format). The enqueuer's
    // own copy (ENQ-ADR-001), kept in step with the scanner's by the contract test on
    // docs/contracts/scan-request.example.json.
    public sealed record ScanRequest(Guid CorrelationId, Guid RepositoryId, string ProjectName, string RepositoryName, string DefaultBranch)
    {
        // The only version the scanner reads. Raised only when an older scanner could not read the format safely.
        public const int SchemaVersion = 1;

        // The request for one repository Issue 2 chose, with the correlation ID the caller made (decision 4)
        public static ScanRequest For(RepositoryToScan repository, Guid correlationId);
    }

    public static class ScanRequestWriter
    {
        // Plain JSON: the six fields in the example's names and order, with no indentation (decision 3)
        public static string Write(ScanRequest request);
    }
}
```

The request for `victim-repo` is written as:

```json
{"schemaVersion":1,"correlationId":"5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c","repositoryId":"0f8fad5b-d9cb-469f-a165-70867728950e","projectName":"Kudos Wall","repositoryName":"victim-repo","defaultBranch":"main"}
```

GUIDs are written in their usual lower-case form with hyphens. A character JSON needs escaped is escaped by `Utf8JsonWriter`'s default rules, and the scanner reads it back as it was.

`ScanRequestContract`, a test helper, holds the rules each message must keep. They are the scanner's (RepoScanner Issue 7 design, Message format):

| Field | Rule |
|---|---|
| The field names | Exactly the example's six, in its case, each once |
| `schemaVersion` | The number `1`, the example's |
| `correlationId` | A GUID, not all zeros |
| `repositoryId` | A GUID, not all zeros |
| `projectName` | Not empty |
| `repositoryName` | Not empty |
| `defaultBranch` | Not empty, and not starting `refs/` |

The scanner also refuses a `repositoryName` of `.` or `..`, or one holding `\`, `/` or another character a file name cannot hold. The enqueuer sends Azure DevOps' name unchanged and does not repeat that check: Azure DevOps' own naming rules refuse every one of those characters, and a name starting or ending with a period ([Naming restrictions, Git repos](https://learn.microsoft.com/azure/devops/organizations/settings/naming-restrictions#azure-repos), checked on 2026-10-08).

## Sending (#110)

```csharp
namespace Enqueuer.Queue
{
    // Builds the client for Enqueuer:Queue from the validated settings (Issue 3 design, decision 8)
    public static class QueueClients
    {
        public static QueueClient Create(QueueOptions options);
    }

    // Sends one scan request at a time, turning Azure's failures into the two the enqueuer reports (decisions 10
    // and 13)
    public sealed class ScanRequestSender
    {
        public ScanRequestSender(QueueClient queue);

        public Task SendAsync(ScanRequest request, CancellationToken cancellationToken);
    }
}
```

`QueueClients.Create` is RepoScanner's, cut to one queue:

- `QueueClientOptions` with `MessageEncoding = QueueMessageEncoding.None` (decision 2).
- With `ServiceUri`: the queue's URI from `QueueUriBuilder` and `new DefaultAzureCredential()`. `AZURE_CLIENT_ID` picks the identity (decision 8).
- Otherwise: `new QueueClient(ConnectionString, Name, clientOptions)`. `QueueOptions` already requires exactly one of the two.

`Program.CreateBuilder` registers the client as a singleton built from `IOptions<QueueOptions>`, then `ScanRequestSender`. A test registers `FakeQueueClient` after them, through `RunAsync`'s `configureServices`, and the last registration wins.

`SendAsync`:

1. Writes the body with `ScanRequestWriter`.
2. Calls `SendMessageAsync(body, visibilityTimeout: null, timeToLive: null, cancellationToken)`: visible at once, Azure's default lifetime (decision 16).
3. Turns a failure into one of the two exceptions below.

## When a send fails (#110)

```csharp
namespace Enqueuer.Queue
{
    // The queue does not exist: fixed by creating it or correcting Enqueuer:Queue:Name, so Program exits 2
    // (Issue 3 design, decision 10)
    public sealed class ScanQueueNotFoundException : Exception
    {
        public ScanQueueNotFoundException(string queueName, Exception innerException);
    }

    // A send still failed after the SDK's retries, so Program exits 1 (decisions 12 and 13)
    public sealed class ScanQueueUnavailableException : Exception
    {
        public ScanQueueUnavailableException(string message, Exception innerException);
    }
}
```

| What happened | Exception | Message |
|---|---|---|
| Azure answered `QueueNotFound` | `ScanQueueNotFoundException` | `The queue scan-nightly does not exist. Create it, or correct Enqueuer:Queue:Name.` |
| Any other answer from Azure, after the retries, such as `403` | `ScanQueueUnavailableException` | `The scan request for Kudos Wall/victim-repo could not be sent to scan-nightly: Azure answered 403 (AuthorizationPermissionMismatch).` |
| A network error or a timeout after the retries, which Azure.Core throws as an `AggregateException`, or `DefaultAzureCredential` getting no token | `ScanQueueUnavailableException` | `The scan request for Kudos Wall/victim-repo could not be sent to scan-nightly: ` and the first line of the exception's message |

The original is the inner exception in each case. If the token passed to `SendAsync` is cancelled, `OperationCanceledException` goes through unchanged, as Issue 1 handles it today. Whether Azurite stopped gives a `RequestFailedException` or an `AggregateException` is checked in #110's manual checks; either way it is the last row's message.

`Program.RunAsync` gets two new `catch` blocks, beside Issue 2's:

```csharp
catch (ScanQueueNotFoundException exception)
{
    // Fixed by creating the queue or correcting its name, like any setting (Issue 3 design, decision 10). The
    // scanner logs the same first words.
    Log.Fatal("Queue missing, enqueuer stopped: {Problem}", exception.Message);
    return ExitCodes.InvalidConfiguration;
}
catch (ScanQueueUnavailableException exception)
{
    // The queue could not be reached or refused the identity, after the SDK's retries (decisions 12 and 13)
    Log.Fatal("Scan requests could not be sent, enqueuer stopped: {Problem}", exception.Message);
    return ExitCodes.Failure;
}
```

Both are one line with no stack trace: neither is fixed by changing code.

## The orchestrator (#110)

`EnqueueOrchestrator` gets `ScanRequestSender` through its constructor. `RunAsync` keeps Issue 2's steps 1 to 5, then:

6. For each repository to scan, in order: builds `ScanRequest.For(repository, Guid.NewGuid())`, sends it, and logs `Queued {ProjectName}/{RepositoryName} with correlation {CorrelationId}` (decision 15).
7. Logs `Sent {SentCount} scan requests to {QueueName}`, in place of `Nothing sent: sending scan requests arrives in Issue 3`.

A `Queued` line is logged only once its send has succeeded, so after a failure the `Queued` lines above the failure line are exactly the requests on the queue. With nothing to scan, nothing is sent and the last line is `Sent 0 scan requests to scan-nightly`.

## Settings

No new settings. Sending uses Issue 1's:

| Setting | Required | `Development` | Notes |
|---|---|---|---|
| `Enqueuer:Queue:Name` | Yes | `scan-nightly` | The queue the requests go to |
| `Enqueuer:Queue:ConnectionString` | One of these two | `UseDevelopmentStorage=true` | Local only, Azurite |
| `Enqueuer:Queue:ServiceUri` | One of these two | | Azure only, with `DefaultAzureCredential`. `AZURE_CLIENT_ID` is set on the job in Issue 6 |

## Local setup

Azurite runs on this machine, as for the scanner (RepoScanner README, Scan from the queue locally):

```powershell
npm install -g azurite
azurite --location c:\azurite_data
```

Then, in another terminal, create the queue and the scanner's poison queue once. The enqueuer never creates them (decision 9). Creating one that exists already does nothing:

```powershell
"scan-nightly", "scan-nightly-poison" | ForEach-Object { az storage queue create --name $_ --connection-string "UseDevelopmentStorage=true" --only-show-errors --output none }
```

RepoScanner's `scripts/enqueue-local.ps1` also creates them, but it adds a message too.

## End to end (#111)

These steps are run by hand once #110 is done, and what each printed goes in the pull request (decision 19). They need Azurite, the Azure CLI, SQL Server Express (`.\SQLEXPRESS`) with RepoScanner's database, and both PAT variables set:

1. Empty the queue: `az storage message clear --queue-name scan-nightly --connection-string "UseDevelopmentStorage=true"`.
2. Run the enqueuer: `dotnet run --project src/Enqueuer`. Expected: a `Queued` line for each of BlacklightInternalsMo's 5 repositories, `Sent 5 scan requests to scan-nightly`, and `$LASTEXITCODE` 0.
3. Look at the queue: `az storage message peek --queue-name scan-nightly --num-messages 32 --connection-string "UseDevelopmentStorage=true"`. Expected: 5 messages, each plain JSON with a correlation ID from step 2.
4. In RepoScanner, run the scanner: `dotnet run --project src/RepoScanner.Scanner`. Expected: it scans each repository, the queue ends empty, and it exits 0.
5. For each correlation ID from step 2: `sqlcmd -S .\SQLEXPRESS -E -C -d RepoScanner -Q "SELECT Status FROM ScanRuns WHERE CorrelationId = '<id>'"`. Expected: one row, `Completed` or `PartiallyCompleted`.
6. A missing queue: `dotnet run --project src/Enqueuer -- --Enqueuer:Queue:Name scan-missing-zz9q1`. Expected: one line starting `Queue missing, enqueuer stopped`, and `$LASTEXITCODE` 2.
7. A failed send: stop Azurite, then run the enqueuer. Expected: after the retries, about 25 seconds, one line starting `Scan requests could not be sent, enqueuer stopped`, and `$LASTEXITCODE` 1.

## README

- **Run:** the enqueuer now needs Azurite and the queue; the local setup above.
- **What it logs:** the `Queued` lines and the `Sent` line, replacing the `Nothing sent` example, from a real run.
- **Exit codes:** what a missing queue and a failed send look like.
- **Scan what it queued:** the end-to-end steps.
- **Known limitations:** those below.

## Testing

Each task is written test first. `dotnet test` still needs no network, no Azurite and no PAT.

`FakeQueueClient` is RepoScanner's, cut to what the enqueuer uses: it keeps every body it is sent, with its visibility timeout and time-to-live, and can be told to fail a chosen send with a given exception. `ProgramTests` that run the whole program put it in place of the real client, so none reaches Azurite.

| Task | Tests |
|---|---|
| #109 | `ScanRequest.For` copies the four fields and the given correlation ID. `ScanRequestWriter`: the example's own values are written as JSON equal to `docs/contracts/scan-request.example.json` (`JsonNode.DeepEquals`); the names are in the example's case and order, with no indentation; a project name with a space, a branch with a slash (`feature/x`) and a repository name with non-ASCII letters read back unchanged with `System.Text.Json`; each written message keeps `ScanRequestContract`'s rules |
| #110 | `QueueClients`: a connection string and a service URI each give a client for the named queue. `ScanRequestSender` against `FakeQueueClient`: one send, whose body is exactly `ScanRequestWriter`'s, visible at once with the default lifetime; `QueueNotFound` throws `ScanQueueNotFoundException` naming the queue; a `403` throws `ScanQueueUnavailableException` naming the repository, the queue, the status and the error code, without the SDK's message; an `AggregateException` gives its first line; cancellation goes through unchanged. `EnqueueOrchestrator`: one send per repository to scan, in order, and none for a skipped one; every body keeps `ScanRequestContract`'s rules; the correlation IDs all differ and none is all zeros; a `Queued` line per message with `CorrelationId` as a named property, logged after its send; the `Sent` line with the count; when the 2nd of 3 sends fails, the 1st is sent, the 3rd is not tried, the exception goes up and no `Sent` line is logged. `Program`: a missing queue exits 2 with one line starting `Queue missing`; a failed send exits 1 with one line starting `Scan requests could not be sent`, and no stack trace; a good run exits 0 with the requests on the fake queue |
| #111 | The end-to-end steps above |

- The 80% line coverage gate on the `Enqueuer` project covers the new classes.
- `EnqueueOrchestratorTests.RunAsync_UntilIssue3_SaysNothingWasSent` is replaced by the `Sent` line's test.

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ENQ-ADR-001):

1. **#81** This design document.
2. **#109** The example file copied from RepoScanner, `ScanRequest`, `ScanRequestWriter`, `RepositoryRoot`, `ScanRequestContract` and the contract tests. No new package.
3. **#110** The two packages, `QueueClients`, `ScanRequestSender`, the two exceptions, `Program`'s registrations and two one-line failures, the orchestrator sending and logging, `FakeQueueClient`, `ProgramTests` kept off Azurite, and the README.
4. **#111** The end-to-end steps against Azurite and the scanner, the README's end-to-end section, and the results in the pull request.

## Not in Issue 3

| Item | Where it is on the board |
|---|---|
| The run summary line, and one Log Analytics query on `correlationId` finding both apps' lines. The per-message `Queued` line is brought forward into this Issue (decision 15) | Issue 4 (#82) |
| The enqueuer's container reaching Azurite | Issue 5 (#83) |
| `id-scanner`'s Message Sender role, `AZURE_CLIENT_ID`, `ServiceUri`, the schedule and `replicaTimeout` | Issue 6 (#84), out of scope in this release (RepoScanner ADR-029) |
| Dropping a request that is already satisfied | RepoScanner Issue 13 (#76) |
| Scan now's request to `scan-ondemand` | The dashboard (#88) |

## Known limitations

- A missing queue is found only when there is something to send, and only after Azure DevOps has been called. With no repository to scan, the enqueuer exits 0 without touching the queue.
- After a failed send, the requests already sent stay on the queue and a re-run sends them again. Until RepoScanner #76 is built, those repositories are scanned twice.
- One send that keeps timing out takes about 10 minutes across its 6 attempts. Issue 6's `replicaTimeout` must allow for it.
- The enqueuer trusts Azure DevOps' repository names. A name the scanner would refuse, possible only if it was made through the API around the naming rules, gets that one message moved to `scan-nightly-poison` by the scanner, with its warning.
- The two copies of the format are tied together only by the contract tests on each side (ENQ-ADR-001).
