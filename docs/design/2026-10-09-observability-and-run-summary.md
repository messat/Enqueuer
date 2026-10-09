# Issue 4: Observability and run summary (design)

- **Status:** Draft, for review
- **Date:** 2026-10-09
- **Work item:** Issue 4, #82 (tasks #112, #113 and #121)
- **Branch:** `feature/82-observability-and-run-summary`

## Purpose

At 02:00 the log is the only witness. After Issue 3 it says what was skipped and what was queued, but it has two gaps:

- **No line says how the night went.** A good run ends `Sent 5 scan requests to scan-nightly`. A run that fails ends with a Fatal line and no counts, so working out how many repositories missed the night's scan means counting `Queued` lines by hand.
- **Every line is plain text.** The `Queued` line already names `{CorrelationId}`, `{ProjectName}` and `{RepositoryName}` with the scanner's property names (Issue 3, decision 15), but the console writes each line as a sentence, so the names are lost before Log Analytics sees them. The scanner writes JSON outside Development since its Issue 10 (RepoScanner ADR-028), so its half of the join is ready and the enqueuer's is not.

After this Issue, one run logs:

```
[Enqueuer starting] -> [Skipped ...]* -> [Queued ... correlation X]* -> [Enqueue run <status> ... : counts]   << new, always
                                                  |
                                                  +-- the same X is on every scanner line for that scan
```

1. `Enqueuer starting in {EnvironmentName} for queue {QueueName}`, as today.
2. One `Skipped` line per skipped repository, as today.
3. One `Queued` line per message sent, as today.
4. One summary line, last, whatever the outcome (#113). It replaces today's `Listed ...` and `Sent ...` lines.
5. When the run failed, `Program`'s one Fatal line follows, as today.

Outside Development, each line is one JSON object, so a search can filter on any property (#112).

## Success criteria

From Issue 4 (#82):

1. The summary line is asserted in a test.
2. A query on one `correlationId` returns the enqueuer's line and the scanner's lines for that repository.

There is no Log Analytics in this release (RepoScanner ADR-029), so criterion 2 is met locally (decision 1): a test checks that each `Queued` line's `CorrelationId` is the one in the message it sent, and #121 runs both apps writing JSON and finds one correlation ID in both outputs with one PowerShell filter. The Kusto searches are written into the README for when the apps are deployed, marked as not yet run.

## Decisions

Decisions 1 to 10 were agreed in the design discussion on 2026-10-09. Decisions 11 to 17 are proposed here, for review.

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | How criterion 2 is proved without Log Analytics | A local stand-in. `ProgramTests` checks each `Queued` line's `CorrelationId` against the message the fake queue was sent. #121 runs the enqueuer and the scanner locally, both writing JSON, saves both outputs, and one PowerShell filter on a correlation ID returns the enqueuer's `Queued` line and the scanner's lines for that scan. What it printed goes in the pull request. The Kusto searches go in the README, marked as not yet run | ADR-029: no Azure resource is created in this release. "Test only, query later" was turned down: it never shows the scanner's lines. The Kusto emulator was turned down, as in RepoScanner Issue 10 (decision 14): several GB, with the lines loaded by hand |
| 2 | Which runs get a summary | Every run that gets past start-up: completed, listing failed (PAT refused, Azure DevOps unavailable), queue missing, a send failed, a bug, or stopped. A run whose settings are wrong (exit 2 before the host starts) gets none | Nothing was listed or sent, and the Fatal line already says why |
| 3 | What "failed" counts | `NotSentCount`: the repositories chosen to queue that were not sent, so `ToScanCount = SentCount + NotSentCount` always | The run stops at the first failed send (Issue 3, decision 12), so at most one send ever fails and the rest are never tried. "How many miss tonight's scan" is the number someone acts on |
| 4 | JSON logs (#112) | As the scanner's Issue 10: a setting `Enqueuer:Logging:Format`, `Text` or `Json` in any case; when not set, text in Development and JSON in every other environment; one `ConsoleLogFormat` class used by both loggers; `RenderedCompactJsonFormatter` from `Serilog.Formatting.Compact` 3.0.0. A new ENQ-ADR-002 adopts RepoScanner ADR-028 for the enqueuer | The same shape on both sides, so one search reads both apps. Only RepoScanner's ADR-001 to ADR-019 apply here without saying so (ADR index), so the enqueuer's own decision is an ENQ-ADR |
| 5 | Where the summary is logged | By `EnqueueOrchestrator`, which counts as it goes and logs the summary in its own `try`/`catch`, then rethrows unchanged | Tested with `FakeLogger`, like the rest of the orchestrator, and the same pattern as the scanner, whose summary comes from the class doing the work. Logging it from `Program` after the Fatal line, with the exit code, was turned down: it would be written by the start-up logger, tested only through console output, and would tie `Program` to the orchestrator's counts. The cost: on a failure the summary comes just before the Fatal line, not after it |
| 6 | Property names | `EnqueueRunStatus`, `EnqueueDurationMs` and `SkippedRepositoryCount`, not the scanner's `ScanRunStatus`, `DurationMs` and `SkippedCount`. `ErrorMessage` keeps the scanner's name | The scanner's rule is that one name means one thing everywhere (RepoScanner Issue 10, decision 7), and both apps' lines will share one workspace. Its `DurationMs` is a scan run's time and its `SkippedCount` counts skipped messages. `ErrorMessage` means the same on both sides: why a run did not complete |
| 7 | Today's `Listed ...` and `Sent ...` lines | Replaced by the summary. The `Skipped`, `Queued` and "listed no repositories" lines stay | Every count in them is in the summary |
| 8 | A run that sends nothing | Still `Completed`, at Information. The README gets a search for completed runs with `SentCount == 0` | The level follows the status, as in the scanner. The 0 is on the summary line, and the "listed no repositories" warning still covers an empty listing |
| 9 | A logging scope, as the scanner's `ScanLogScope` | None | The scanner needs one because many classes log during one scan. Here a request has exactly one line, `Queued`, which already carries all three properties. A request whose send failed never reaches the scanner, so there is nothing to join it to |
| 10 | A third task | #121, under #82: the end-to-end check, the join test and the README's searches | As #111 in Issue 3 and #100 in RepoScanner Issue 10: the proof, once every piece is in |
| 11 | How the duration is measured | `TimeProvider`, registered as `TimeProvider.System` and passed to the orchestrator, from the start of `RunAsync` to the summary. The tests use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` 10.10.0, the scanner's version | The test can then assert an exact `EnqueueDurationMs`, as the scanner's tests do. A `Stopwatch` could only be checked as "not negative" |
| 12 | How a stopped run is told apart | An `OperationCanceledException` while the orchestrator's `CancellationToken` is cancelled is `Stopped`. Any other exception, including an `OperationCanceledException` that the token did not cause, is `Failed` | Ctrl+C locally, SIGTERM in Azure. An `OperationCanceledException` the token did not cause, such as a timeout, is a failure, not a request to stop |
| 13 | What `ErrorMessage` holds | The exception's `Message`, as thrown | The enqueuer's own exceptions are written to be logged: no PAT, no response body (Issue 2, decision 8; Issue 3, decision 13). For a bug, `Program`'s Fatal line right after still has the full exception |
| 14 | `SkippedByReason` | A dictionary with every `SkipReason` in the enum's order, zeros included: `{"Disabled":0,"InMaintenance":0,"Empty":2}` | A search reads `toint(e.SkippedByReason.Empty)` without checking it exists. The enum's name is what Issue 2 logged on each `Skipped` line |
| 15 | The organisation | The summary names `{OrganizationUrl}`, which the `Listed` line held until now | Otherwise the log would name it only on the "listed no repositories" warning and on failures |
| 16 | New packages | `Serilog.Formatting.Compact` 3.0.0 in `Enqueuer` (#112). `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 in the tests (#113) | Decisions 4 and 11. Both at RepoScanner's versions, copied on 2026-10-09 and not kept in step (ENQ-ADR-001). `Serilog.Formatting.Compact` 3.0.0 needs Serilog 4.0 or later; the enqueuer has 4.3.0 |
| 17 | Where this document lives | `docs/design/` | Same as Issues 2 and 3 |

## Project layout

```
src/Enqueuer/
  Logging/         ConsoleLogFormat                                      #112
  Configuration/   LoggingOptions                                        #112
  EnqueueRunStatus.cs       Completed, Failed, Stopped                   #113
  EnqueueOrchestrator.cs    counts, and logs the summary                 #113
  Program.cs                both loggers through WriteToConsole;
                            LoggingOptions                               #112
                            TimeProvider.System                          #113
  Enqueuer.csproj           + Serilog.Formatting.Compact                 #112

Directory.Packages.props    + Serilog.Formatting.Compact 3.0.0           #112
                            + Microsoft.Extensions.TimeProvider.Testing
                              10.10.0                                    #113

docs/adr/ENQ-ADR-002-json-log-lines-outside-development.md,
         README.md (index)                                               #112

tests/Enqueuer.Tests/
  LogLines.cs (test helper, copied from RepoScanner)                     #112
  Logging/         ConsoleLogFormatTests                                 #112
  Configuration/   LoggingOptionsTests                                   #112
  ProgramTests.cs           read through LogLines                        #112
                            the summary before the Fatal line            #113
                            the join test                                #121
  EnqueueOrchestratorTests.cs   the summary for every outcome            #113
  Enqueuer.Tests.csproj     + Microsoft.Extensions.TimeProvider.Testing  #113

README.md                                                                #112, #113, #121
```

`ExitCodes.cs` is unchanged: the summary changes no exit code.

## JSON or text (#112)

`Logging/ConsoleLogFormat` is the scanner's, with `Enqueuer:` for `Scanner:`:

- `IsJson(setting, environmentName)`: `Json` or `Text` in any case wins. Otherwise Development gets text and every other environment, Azure's Production included, gets JSON. A value that is neither is left to `LoggingOptions` to reject.
- `IsJsonBeforeTheHost(args)`: for the start-up logger, which exists before configuration is read. It takes the setting from environment variables and the command line, and the environment from `--environment`, else `DOTNET_ENVIRONMENT`, else Production. appsettings files are not read.

`Configuration/LoggingOptions` holds `Format`, checked with `[RegularExpression("^(?i:text|json)$")]`, bound from `Enqueuer:Logging` and validated on start like the other two groups, so `Enqueuer__Logging__Format=Xml` is one line and exit 2.

In `Program`, the start-up logger and the host's logger both go through one `WriteToConsole(LoggerSinkConfiguration, bool json)`: JSON writes `new RenderedCompactJsonFormatter()`, text writes today's `ConsoleOutputTemplate`. So the Fatal lines (`Azure DevOps refused the token`, `Queue missing`, ...) are JSON outside Development too. `.Enrich.FromLogContext()` and the `HttpClient` and `Polly` overrides stay as they are.

A `Queued` line as JSON, shown here across several lines:

```json
{"@t":"2026-10-09T02:00:02.1180000Z",
 "@m":"Queued \"Kudos Wall\"/\"victim-repo\" with correlation 77438d01-1e66-47ae-a0ed-bd5c86c6f446",
 "@i":"8c2a91f0",
 "ProjectName":"Kudos Wall","RepositoryName":"victim-repo","CorrelationId":"77438d01-1e66-47ae-a0ed-bd5c86c6f446",
 "SourceContext":"Enqueuer.EnqueueOrchestrator"}
```

As in the scanner: `@l` is left out for Information, and `@m` puts quotes round string values.

## The summary line (#113)

The template, logged once by `EnqueueOrchestrator.RunAsync`:

```
Enqueue run {EnqueueRunStatus} in {EnqueueDurationMs} ms: {ListedCount} listed in {OrganizationUrl}, {ToScanCount} to queue,
{SkippedRepositoryCount} skipped {SkippedByReason}, {SentCount} sent to {QueueName}, {NotSentCount} not sent
```

on one line, Information when `Completed`. When `Failed` or `Stopped`, the same template at Warning with `. Error: {ErrorMessage}` added, so the same properties are on both. As text:

```
[02:00:03 INF] Enqueuer.EnqueueOrchestrator: Enqueue run Completed in 1840 ms: 5 listed in https://dev.azure.com/BlacklightInternalsMo, 5 to queue, 0 skipped {"Disabled":0,"InMaintenance":0,"Empty":0}, 5 sent to scan-nightly, 0 not sent
[02:00:41 WRN] Enqueuer.EnqueueOrchestrator: Enqueue run Failed in 39210 ms: 40 listed in https://dev.azure.com/BlacklightInternalsMo, 39 to queue, 1 skipped {"Disabled":1,"InMaintenance":0,"Empty":0}, 20 sent to scan-nightly, 19 not sent. Error: ...
[02:00:41 FTL] Enqueuer.Program: Scan requests could not be sent, enqueuer stopped: ...
```

How `RunAsync` keeps its counts:

1. It notes the start time from `TimeProvider`, and sets every count to 0.
2. After the listing, `ListedCount`, `ToScanCount`, `SkippedRepositoryCount` and `SkippedByReason` are set from the listing and the `RepositorySelection`.
3. `SentCount` goes up by one after each send succeeds, where the `Queued` line is logged.
4. The whole body is inside `try`. With no exception, the summary is `Completed`. With an exception, it is `Stopped` or `Failed` (decision 12) with the exception's `Message` (decision 13), and the exception is rethrown unchanged with `throw;`.

When the listing fails, every count is 0: `0 listed` then means nothing was listed, not that Azure DevOps listed nothing, and the status and `ErrorMessage` say which. `Program` is unchanged: its catch blocks log the same Fatal lines with the same exit codes.

## End to end (#121)

These steps extend README **Scan what it queued**, and are run by hand once #113 is done. What each printed goes in the pull request. They need what #111 needed: Azurite with both queues, the Azure CLI, SQL Server Express with RepoScanner's database, and both PAT variables.

1. Look at the queue first, as #111 did: `az storage message peek --queue-name scan-nightly --num-messages 32 --connection-string "UseDevelopmentStorage=true"`. Expected: empty.
2. Build both apps first (`dotnet build`), so `dotnet run --no-build` prints only the app's lines.
3. Run the enqueuer writing JSON, keeping its output: `dotnet run --no-build --project src/Enqueuer -- --Enqueuer:Logging:Format Json | Tee-Object -FilePath $env:TEMP\enqueuer.log`. Expected: every line JSON, a `Queued` line per repository, the summary `Completed`, and `$LASTEXITCODE` 0.
4. In RepoScanner, run the scanner the same way: `dotnet run --no-build --project src/RepoScanner.Scanner -- --Scanner:Logging:Format Json | Tee-Object -FilePath $env:TEMP\scanner.log`.
5. Find one correlation ID from step 3 in both:

   ```powershell
   Get-Content $env:TEMP\enqueuer.log, $env:TEMP\scanner.log | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object CorrelationId -eq '<id>' | Select-Object '@t', SourceContext, '@m'
   ```

   Expected: the enqueuer's `Queued` line first, then the scanner's lines for that scan: `Message ...`, `Cloned ...`, one line per scanner, its findings, and its summary.
6. A failed run as JSON: `dotnet run --no-build --project src/Enqueuer -- --Enqueuer:Logging:Format Json --Enqueuer:Queue:Name scan-missing-zz9q1`. Expected: the summary at `"@l":"Warning"`, `Failed`, `0 sent`, with `NotSentCount` the number to queue; then the Fatal line starting `Queue missing`; and `$LASTEXITCODE` 2.

Each line is piped through `ConvertFrom-Json` on its own, so each is read as one JSON document whichever PowerShell runs it. Anything that does not hold when #121 runs is changed here and in the README to what was run, as in #111.

## Searches

For the README's new **Logs** section, in the scanner's form (RepoScanner Issue 10, Searches): Container Apps puts each line in the `Log` column of `ContainerAppConsoleLogs` as text, so a search unpacks it with `parse_json`. **Not yet run:** there is no Log Analytics in this release (ADR-029).

**One request, from the enqueuer to the end of its scan:**

```kusto
ContainerAppConsoleLogs
| extend e = parse_json(Log)
| where tostring(e.CorrelationId) == "77438d01-1e66-47ae-a0ed-bd5c86c6f446"
| project Time = todatetime(e["@t"]), Source = tostring(e.SourceContext), Message = tostring(e["@m"])
| order by Time asc
```

**Enqueue runs that did not complete, in the last week:**

```kusto
ContainerAppConsoleLogs
| where TimeGenerated > ago(7d)
| extend e = parse_json(Log)
| where isnotempty(tostring(e.EnqueueRunStatus)) and tostring(e.EnqueueRunStatus) != "Completed"
| project TimeGenerated, Status = tostring(e.EnqueueRunStatus), Sent = toint(e.SentCount), NotSent = toint(e.NotSentCount), Error = tostring(e.ErrorMessage)
```

**Nights that queued nothing:** the same, with `where tostring(e.EnqueueRunStatus) == "Completed" and toint(e.SentCount) == 0` (decision 8).

The scanner's rejected-PAT alert search (`@l` Fatal and `@m` starting `Azure DevOps refused the token`) already matches the enqueuer's line once it is JSON, as Issue 2, decision 7 intended. A test pins both.

## ENQ-ADR-002 (#112)

**ENQ-ADR-002: JSON log lines on stdout outside Development.** *Status: Accepted. Date: 2026-10-09. Source: decided while designing Issue 4 (#82).*

- **Context:** the scanner writes one CLEF JSON object per line outside Development (RepoScanner ADR-028), so Log Analytics can filter its lines on `CorrelationId`. The enqueuer makes each request's correlation ID and logs it on its `Queued` line, but writes text, so a search could only match its words. Only RepoScanner's ADR-001 to ADR-019 apply to the enqueuer without being adopted (ENQ-ADR-001).
- **Decision:** the enqueuer follows RepoScanner ADR-028: outside Development, both its loggers write CLEF with `RenderedCompactJsonFormatter`, every property at the top level. Development writes text. `Enqueuer:Logging:Format` (`Text` or `Json`) overrides the environment. The code is copied, not shared, as the scan request is.
- **Consequences:** one search on `CorrelationId` reads both apps' lines. Property names become part of the searches, so the enqueuer avoids the scanner's names where they mean something else (`EnqueueRunStatus`, `EnqueueDurationMs`, `SkippedRepositoryCount`). The two copies of `ConsoleLogFormat` are kept alike by hand. One more package to keep up to date.

## README

- **Configuration:** a row for `Enqueuer:Logging:Format` (#112).
- **What it logs:** text in Development and JSON elsewhere (#112); the summary line in place of the `Listed` and `Sent` lines, from a real run (#113).
- **Scan what it queued:** the JSON steps from End to end (#121).
- **Logs**, a new section: the searches above, marked as not yet run (#121).
- **Known limitations:** those below.

## Testing

Each task is written test first. `dotnet test` still needs no network, no Azurite and no PAT.

| Task | Tests |
|---|---|
| #112 | `ConsoleLogFormatTests`: Development gives text, Production and `Test` give JSON, and the setting overrides both ways in any case; `IsJsonBeforeTheHost` takes `--environment` before `DOTNET_ENVIRONMENT`. `LoggingOptionsTests`: empty, `Text`, `json` pass; `Xml` fails with one message. `ProgramTests`, which run as `Test`, read the console through `LogLines`: every line is JSON, the start-up logger's Fatal lines included; the refused-PAT line is `Fatal` and its `@m` starts `Azure DevOps refused the token`; with `--environment Development`, text; `--Enqueuer:Logging:Format Text` in `Test`, text; `Xml` exits 2 with one line; the `Queued` line has `CorrelationId`, `ProjectName` and `RepositoryName` at the top level |
| #113 | `EnqueueOrchestratorTests`, with `FakeLogger` and `FakeTimeProvider`: exactly one summary line, the last, for Completed with skips of each reason, Completed with nothing listed, Failed when the listing is refused, Failed when Azure DevOps is unavailable, Failed when the queue is missing on the first send, Failed when the 2nd of 3 sends fails (`1 sent`, `2 not sent`), Failed for a bug, and Stopped when cancelled; each with every property of decision 6 and the right level; `ErrorMessage` only when not Completed; the exception rethrown unchanged; an `OperationCanceledException` the token did not cause is Failed; no `Listed` or `Sent` line. `ProgramTests`: a failed send's summary is the line just before the Fatal line |
| #121 | `ProgramTests`, with `FakeQueueClient` and two repositories to scan: each `Queued` line's `CorrelationId` equals the `correlationId` in one sent message, and each sent message has one. Then the End to end steps by hand |

- The 80% line coverage gate on the `Enqueuer` project covers the new classes.
- `ProgramTests.FatalLines` becomes a filter on `LogLines.Level(line) == "Fatal"`.

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ENQ-ADR-001):

1. **#82** This design document, then the implementation plan.
2. **#112** `Serilog.Formatting.Compact`, `ConsoleLogFormat`, `LoggingOptions`, `WriteToConsole` for both loggers, `LogLines`, `ProgramTests` reading JSON, ENQ-ADR-002 and the README's Configuration row.
3. **#113** `TimeProvider`, `EnqueueRunStatus`, the orchestrator's counts and summary, its tests, the `Listed` and `Sent` lines removed, and the README's What it logs.
4. **#121** The join test, the End to end steps, the README's Logs section and Scan what it queued, and the results in the pull request.

## Not in Issue 4

| Item | Where it is on the board |
|---|---|
| Running the Kusto searches, and creating alert rules from them | Issue 6 (#84), out of scope in this release (RepoScanner ADR-029) |
| Reading the enqueuer container's JSON log in Docker Desktop | Issue 5 (#83) |
| A stopped run's Fatal line: today `Program` logs `Enqueuer failed` with the stack trace and exits 1, as for a bug | Unchanged. Not on the board |

## Known limitations

- On a failure the summary is the second-last line, with `Program`'s Fatal line after it (decision 5).
- The Kusto searches have not been run against Log Analytics. The local filter shows the property names match, not that Container Apps keeps each line whole in `Log`. For Issue 6 to check.
- A run with wrong settings has no summary line (decision 2).
- When the listing fails, the summary says `0 listed`: read it with the status and `ErrorMessage`.
- Raw JSON is harder to read than text, which is why Development keeps text.
