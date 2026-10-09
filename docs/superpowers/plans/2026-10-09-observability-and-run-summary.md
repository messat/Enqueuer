# Issue 4: Observability and run summary, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The morning after a run, the log says how it went, and each request's correlation ID finds the scanner's lines for its scan. Task 1 (#112) writes every log line as JSON outside Development, as the scanner does; Task 2 (#113) ends every run that got past start-up with one summary line; Task 3 (#121) proves the join by running both apps locally as JSON and documents the searches.

**Architecture:** Task 1 copies the scanner's Issue 10 logging: a static `ConsoleLogFormat` decides text or JSON for both the start-up logger and the host's logger, from a new `Enqueuer:Logging:Format` setting and the environment, and `RenderedCompactJsonFormatter` writes CLEF; a test helper, `LogLines`, reads the JSON back. Task 2 keeps the run's counts inside `EnqueueOrchestrator.RunAsync`, logs the summary in a `try`/`catch` that rethrows, and measures the run with an injected `TimeProvider`. Task 3 adds one test and otherwise runs both apps by hand against Azurite and SQL Server Express, records the results for the pull request, and writes the README's Logs section.

**Tech Stack:** .NET 10, Serilog 4.3.0, `Serilog.Formatting.Compact` 3.0.0, `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 (tests only), NUnit 4, `Microsoft.Extensions.Diagnostics.Testing` (`FakeLogger`), coverlet.msbuild; Azurite, the `az` CLI, SQL Server Express (`.\SQLEXPRESS`) and RepoScanner's scanner for Task 3.

**Spec:** [docs/design/2026-10-09-observability-and-run-summary.md](../../design/2026-10-09-observability-and-run-summary.md), committed as `1cfcd8b` (#82). Read its Decisions, JSON or text, The summary line, End to end, Searches, ENQ-ADR-002 and Testing before starting; this plan argues from it.

**Scope:** #112 (Task 1), #113 (Task 2) and #121 (Task 3). After Task 3, the whole branch gets one fresh review, then superpowers:finishing-a-development-branch.

## Global Constraints

- Branch: `feature/82-observability-and-run-summary` (already created from `develop`, with the design committed).
- **Commits:** one per board task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with the task number, such as `#112`. It never mentions Claude, Opus or any AI tool: no `Co-Authored-By` line. Push and open a pull request only when the user asks.
- **Board:** set the task to Doing when it starts and to Done when its commit is made: `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id <112|113|121> --state <Doing|Done>`.
- **Exact values:**
  - Setting `Enqueuer:Logging:Format`, section `Enqueuer:Logging`, pattern `^(?i:text|json)$`, failure message `Enqueuer:Logging:Format must be Text or Json`.
  - Summary template: `Enqueue run {EnqueueRunStatus} in {EnqueueDurationMs} ms: {ListedCount} listed in {OrganizationUrl}, {ToScanCount} to queue, {SkippedRepositoryCount} skipped {SkippedByReason}, {SentCount} sent to {QueueName}, {NotSentCount} not sent`, with `. Error: {ErrorMessage}` appended when not `Completed`.
  - Statuses `Completed`, `Failed`, `Stopped`. `SkippedByReason` keys in the enum's order: `Disabled`, `InMaintenance`, `Empty`.
- **Namespaces:** `Enqueuer.Logging` (`src/Enqueuer/Logging/`), `Enqueuer.Configuration`, `Enqueuer` for `EnqueueRunStatus`; tests in `Enqueuer.Tests`, `Enqueuer.Tests.Logging`, `Enqueuer.Tests.Configuration`.
- **Code conventions (RepoScanner ADR-021, as in Issues 1 to 3):** classes `sealed` or `static`, options as `sealed record`; explicit types, no `var`; block bodies, no expression bodies; `using (...) { }` blocks; `ImplicitUsings` is off, so every file lists its usings; a comment giving the reason for each non-obvious line, citing the Issue 4 design by decision number. Tests: NUnit, `Method_Condition_Result` names, `Assert.EnterMultipleScope()` for several asserts. NUnit.Analyzers runs with warnings as errors, so never assert on a `const`.
- **Secrets:** a manual run reads a secret from the user's environment into that one process, never printing it, because this session's shell predates the variables: `$env:<name> = [Environment]::GetEnvironmentVariable("<name>", "User")` for `Enqueuer__AzureDevOps__Pat`, `Scanner__AzureDevOps__Pat` and `Scanner__Database__ConnectionString`.
- Workspace for logs and results, git-ignored: `.superpowers/sdd/2026-10-09-observability-and-run-summary/`.
- No em dash (U+2014) in any file. British English.
- `dotnet build Enqueuer.sln` has zero warnings and `dotnet test Enqueuer.sln` passes with the 80% line coverage gate at the end of each task. 167 tests pass before Task 1. Filtered runs add `-p:CollectCoverage=false`.

## Review Focus

Inputs the spec implies but does not test by name, most likely to bite first. Each has its test in the task named.

1. **Names that JSON escapes or that are not ASCII** (Task 1), such as project `Café & Co` and repository `O'Brien-Ölçek`: the `Queued` line is still one JSON object, and `ProjectName` and `RepositoryName` read back exactly, since a search compares them as written.
2. **The PAT check fooled by JSON escaping** (Task 1): `ProgramTests.RunAsync_WhateverAzureDevOpsAnswers_NeverPrintsThePat` searches the raw output, but in JSON a `+` in the Base64 form is written `+`. It must also search every unescaped string, through `LogLines.Strings`.
3. **The format set as an environment variable** (Task 1): `Enqueuer__Logging__Format=Text` with `--environment Test` must make the start-up logger's Fatal line text too. `ConsoleLogFormatTests` only covers the command line.
4. **Both loggers agreeing** (Task 1): a run in Development whose PAT is refused writes its host lines and its start-up Fatal line as text; the same run in `Test` writes both as JSON. One logger changing alone is the scanner's decision 10 failure.
5. **An error message with a line break** (Task 2), as a bug's exception may have: in JSON the summary is still one object per line, and `ErrorMessage` holds the message exactly.

---

### Task 1: JSON logs outside Development (#112)

**Files:**
- Modify: `Directory.Packages.props`, `src/Enqueuer/Enqueuer.csproj`, `src/Enqueuer/Program.cs`, `README.md`, `docs/adr/README.md`
- Create: `src/Enqueuer/Logging/ConsoleLogFormat.cs`, `src/Enqueuer/Configuration/LoggingOptions.cs`, `docs/adr/ENQ-ADR-002-json-log-lines-outside-development.md`
- Create: `tests/Enqueuer.Tests/LogLines.cs` (test helper)
- Test: `tests/Enqueuer.Tests/Logging/ConsoleLogFormatTests.cs`, `tests/Enqueuer.Tests/Configuration/LoggingOptionsTests.cs` (new); `tests/Enqueuer.Tests/ProgramTests.cs` (modified)

**Interfaces:**
- Consumes: `Program.ConsoleOutputTemplate` and the two loggers in `Program.RunAsync` and `Program.CreateBuilder`, unchanged otherwise.
- Produces, for Tasks 2 and 3:
  - `Enqueuer.Configuration.LoggingOptions`, `public sealed record`: `public const string SectionName = "Enqueuer:Logging"`; `public string? Format { get; init; }` with `[RegularExpression("^(?i:text|json)$", ErrorMessage = "Enqueuer:Logging:Format must be Text or Json")]`.
  - `Enqueuer.Logging.ConsoleLogFormat`, `public static class`: `public const string SettingName = LoggingOptions.SectionName + ":Format"`; `public static bool IsJson(string? setting, string environmentName)`; `public static bool IsJsonBeforeTheHost(string[] args)`. Both copied from RepoScanner's `src/RepoScanner.Scanner/Logging/ConsoleLogFormat.cs`.
  - In `Program`: `private static LoggerConfiguration WriteToConsole(LoggerSinkConfiguration writeTo, bool json)`, `new RenderedCompactJsonFormatter()` when `json`, else `outputTemplate: ConsoleOutputTemplate`.
  - Test helper `Enqueuer.Tests.LogLines`, `internal static class`, copied from RepoScanner's `tests/RepoScanner.Scanner.Tests/LogLines.cs`: `IReadOnlyList<JsonElement> Read(string output)` (fails the test on a line that is not one JSON object), `string Level(JsonElement line)` (`"Information"` when `@l` is missing), `string Message(JsonElement line)`, `string? Value(JsonElement line, string name)` (a string as it is, a number or object as its raw JSON), `IEnumerable<string> Strings(JsonElement element)`.
  - In `ProgramTests`: `private static JsonElement[] FatalLines(string output)`, the lines whose `LogLines.Level` is `"Fatal"`; and `private static string[] With(params string[] extra)`, `ValidArgs` followed by `extra` (a repeated key's last value wins, for the host and for `IsJsonBeforeTheHost` alike).

- [ ] **Step 1: Set #112 to Doing**

- [ ] **Step 2: Add the package**

`Directory.Packages.props`, in the Logging group: `Serilog.Formatting.Compact` `3.0.0`, with the comment `<!-- RenderedCompactJsonFormatter: one JSON object per line outside Development (Issue 4 design, decision 4; ENQ-ADR-002). Added 2026-10-09 at RepoScanner's version. -->`. `src/Enqueuer/Enqueuer.csproj`: a `PackageReference` after `Serilog.Sinks.Console`, commented `<!-- One JSON object per log line outside Development, for Log Analytics (ENQ-ADR-002) -->`.

Run: `dotnet build Enqueuer.sln` then `dotnet list package --vulnerable --include-transitive`
Expected: 0 warnings, 0 errors; every project "has no vulnerable packages given the current sources".

- [ ] **Step 3: Copy the test helper**

`tests/Enqueuer.Tests/LogLines.cs`: RepoScanner's, in namespace `Enqueuer.Tests`, its header comment naming the enqueuer, #112 and ENQ-ADR-002. No other change.

- [ ] **Step 4: Write the failing tests**

`tests/Enqueuer.Tests/Logging/ConsoleLogFormatTests.cs`: RepoScanner's `ConsoleLogFormatTests`, namespace `Enqueuer.Tests.Logging`, `--Scanner:Logging:Format` changed to `--Enqueuer:Logging:Format`, citing Issue 4 decision 4, plus `[TestCase(null, "Test", true)]`, the environment every `ProgramTests` run uses. Two more, each setting `DOTNET_ENVIRONMENT` to `Development` and restoring it in `finally`: `IsJsonBeforeTheHost_WithoutAnEnvironmentArgument_UsesDotnetEnvironment` (`new string[0]` gives false) and `IsJsonBeforeTheHost_WithAnEnvironmentArgument_PrefersItToDotnetEnvironment` (`--environment Test` gives true), as the design's Testing table asks.

`tests/Enqueuer.Tests/Configuration/LoggingOptionsTests.cs`, in `QueueOptionsTests`' style (a `DataAnnotationValidateOptions<LoggingOptions>(null)` field):
- `Validate_WithFormatNotSet_Succeeds`: `new LoggingOptions()`, `result.Succeeded` true.
- `Validate_WithTextOrJsonInAnyCase_Succeeds`, `[TestCase("Text")] [TestCase("json")] [TestCase("JSON")]`.
- `Validate_WithAnyOtherFormat_FailsNamingTheSetting`, `[TestCase("Xml")] [TestCase("Jsonl")]`: `result.Failed` true and `result.FailureMessage` contains `Enqueuer:Logging:Format must be Text or Json`.

`tests/Enqueuer.Tests/ProgramTests.cs`, which runs as `Test`, so now gets JSON:

- `FatalLines` becomes `LogLines.Read(output).Where(line => LogLines.Level(line) == "Fatal").ToArray()`.
- The four one-line failure tests (refused PAT, cannot list, queue missing, send failed) keep their exit code, one-Fatal-line and no-`   at ` asserts, and replace the `Does.Contain` on the line with: `LogLines.Message(failures[0])` starts with the line's first words (such as `Azure DevOps refused the token, enqueuer stopped: `); `LogLines.Value(failures[0], "Problem")` equals the text after them that the old assert held (such as `Azure DevOps refused the token in Enqueuer:AzureDevOps:Pat...`, compared with `Does.StartWith` where the old assert stopped short); and `failures[0].TryGetProperty("@x", out _)` is false. In the refused-PAT test, `LogLines.Message(failures[0])` starting `Azure DevOps refused the token` is the scanner's alert search (design, Searches).
- `RunAsync_WithValidSettings_RunsTheOrchestrator`: instead of the three `Does.Contain`, a line with `Value(line, "EnvironmentName") == "Test"` and `Value(line, "QueueName") == "scan-test"`, and a line with `Value(line, "ListedCount") == "3"` and `Value(line, "OrganizationUrl") == "https://dev.azure.com/BlacklightInternalsMo"`.
- `RunAsync_WithARepositoryToScan_SendsItAndReturnsZero`: instead of `Does.Contain("Sent 1 ...")`, a line with `Value(line, "SentCount") == "1"`.
- `BadSettings` gains `new TestCaseData(With("--Enqueuer:Logging:Format", "Xml"), "Enqueuer:Logging:Format must be Text or Json").SetArgDisplayNames("wrong Format")`. Its settings fail at start-up, so `Main` never reaches Azure DevOps.
- New `RunAsync_OutsideDevelopment_WritesEveryLineAsJson`: `OneToScan` and a `FakeQueueClient`; `LogLines.Read(output)` has at least 3 lines, each with `@t` and `@m`.
- New `RunAsync_QueuedLine_HasTheCorrelationIdAndRepositoryAtTheTopLevel`: same run; the line with a `CorrelationId` has `ProjectName` `Kudos Wall`, `RepositoryName` `victim-repo`, and a `CorrelationId` `Guid.TryParse` accepts.
- New `RunAsync_InDevelopment_WritesText`: `With("--environment", "Development")` and the default sample listing; no line starts with `{`, and a line contains ` INF] Enqueuer.EnqueueOrchestrator: Enqueuer starting in Development`.
- New `RunAsync_WithFormatTextOutsideDevelopment_WritesText`: `With("--Enqueuer:Logging:Format", "Text")`; the same checks with `Enqueuer starting in Test`.
- Review Focus 1, new `RunAsync_NamesThatJsonEscapes_ReadBackExactly`: a listing of one repository `O'Brien-Ölçek` in project `Café & Co`, default branch `refs/heads/main`, and a `FakeQueueClient`; the `Queued` line's `ProjectName` is `Café & Co` and `RepositoryName` `O'Brien-Ölçek`.
- Review Focus 2, `RunAsync_WhateverAzureDevOpsAnswers_NeverPrintsThePat`: `leaked` is also true when any of `LogLines.Read(output).SelectMany(LogLines.Strings)` contains the PAT or its Base64 form.
- Review Focus 3, new `Main_WithFormatTextInTheEnvironment_WritesTheStartUpLinesAsText`: sets `Enqueuer__Logging__Format` to `Text`, restoring it in `finally` as `Main_WithoutPat_IgnoresAPatInTheEnvironment` does; `Main(ArgsWithoutQueueName)` writes one line, containing ` FTL] ` and not starting with `{`.
- Review Focus 4, new `RunAsync_InDevelopmentWhenThePatIsRefused_BothLoggersWriteText`: `With("--environment", "Development")`, `Respond(HttpStatusCode.Unauthorized, "{}")`; exit 1; every non-empty line starts with `[`; one contains ` INF] ` (the host's logger) and one ` FTL] ` (the start-up logger). The JSON half is `RunAsync_OutsideDevelopment_WritesEveryLineAsJson` with the refused-PAT test.

- [ ] **Step 5: Run the tests to see them fail**

Run: `dotnet test Enqueuer.sln -p:CollectCoverage=false`
Expected: build errors for `LoggingOptions` and `ConsoleLogFormat`.

- [ ] **Step 6: Write `LoggingOptions` and `ConsoleLogFormat`**

As in Interfaces, each with the scanner's comments, `Scanner:` read as `Enqueuer:`, citing Issue 4 decision 4 and ENQ-ADR-002.

- [ ] **Step 7: Send both loggers through `WriteToConsole`**

In `Program.RunAsync`, the start-up logger is `WriteToConsole(new LoggerConfiguration().Enrich.WithProperty("SourceContext", typeof(Program).FullName).WriteTo, ConsoleLogFormat.IsJsonBeforeTheHost(args)).CreateLogger()`. In `CreateBuilder`, `bool json = ConsoleLogFormat.IsJson(builder.Configuration[ConsoleLogFormat.SettingName], builder.Environment.EnvironmentName)`, and the `AddSerilog` chain ends `.WriteTo, json)` inside `WriteToConsole(...)`, its overrides and `Enrich.FromLogContext()` unchanged. Register `LoggingOptions` after the logger, as the other groups are: `AddOptions<LoggingOptions>().Bind(builder.Configuration.GetSection(LoggingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart()`.

Run: `dotnet test Enqueuer.sln --tl:off`
Expected: every test passes, 0 warnings, line coverage of `Enqueuer` at least 80%.

- [ ] **Step 8: ENQ-ADR-002**

`docs/adr/ENQ-ADR-002-json-log-lines-outside-development.md` from the design's ENQ-ADR-002 section, in ENQ-ADR-001's layout (title, status, date, source, then `## Context`, `## Decision`, `## Consequences`). `docs/adr/README.md`: its row in the table, and "The next new ADR is **ENQ-ADR-003**".

- [ ] **Step 9: README**

- **Configuration:** a row `Enqueuer:Logging:Format` | No | (not set) | `Text` or `Json`, in any case. Not set: text in Development, one JSON object per line everywhere else (ENQ-ADR-002). Any other value stops the enqueuer with exit code 2.
- **What it logs:** after the example, one short paragraph: the example is Development's text; every other environment writes each line as one JSON object (`@t` the time, `@m` the message, `@l` the level, left out for Information, then every named value, such as `CorrelationId`); `--Enqueuer:Logging:Format Json` shows it locally.

- [ ] **Step 10: Manual checks, sending nothing**

A queue name that does not exist makes each run list Azure DevOps for real and stop at the first send, so nothing lands on `scan-nightly`. Azurite must be running (port 10001); if not, ask the user. With `Enqueuer__AzureDevOps__Pat` read in, save each run's output to the workspace:

1. `dotnet run --project src/Enqueuer -- --Enqueuer:Logging:Format Json --Enqueuer:Queue:Name scan-missing-zz9q1`. Expected: every line one JSON object; the `Queued` line is absent (the first send failed); the last line has `"@l":"Fatal"` and an `@m` starting `Queue missing, enqueuer stopped`; `$LASTEXITCODE` 2.
2. The same without `--Enqueuer:Logging:Format`. Expected: text, as today (`dotnet run` is Development); `$LASTEXITCODE` 2.

- [ ] **Step 11: Review, commit, set #112 to Done**

Stop for the user's review of the diff and the two runs. Once approved:

```bash
git add Directory.Packages.props src/Enqueuer tests/Enqueuer.Tests docs/adr README.md
git commit -m "This commit writes the enqueuer's log lines as JSON outside Development, as the scanner does: a new Enqueuer:Logging:Format setting, Text or Json in any case, checked at start-up with exit 2 for any other value; ConsoleLogFormat, copied from RepoScanner, choosing for both the start-up logger and the host's logger, text in Development and JSON elsewhere; RenderedCompactJsonFormatter from Serilog.Formatting.Compact 3.0.0; ENQ-ADR-002 adopting RepoScanner ADR-028; and ProgramTests reading the JSON back through LogLines, including names JSON escapes, the PAT searched for unescaped, and both loggers agreeing #112"
```

---

### Task 2: The run summary (#113)

**Files:**
- Modify: `Directory.Packages.props`, `tests/Enqueuer.Tests/Enqueuer.Tests.csproj`, `src/Enqueuer/EnqueueOrchestrator.cs`, `src/Enqueuer/Program.cs`, `README.md`
- Create: `src/Enqueuer/EnqueueRunStatus.cs`
- Test: `tests/Enqueuer.Tests/EnqueueOrchestratorTests.cs`, `tests/Enqueuer.Tests/ProgramTests.cs` (modified)

**Interfaces:**
- Consumes: from Task 1, `LogLines` and `ProgramTests.FatalLines` returning `JsonElement[]`. From Issue 3, `FakeQueueClient.FailSend(int attempt, Exception exception)`, `ScanQueueNotFoundException`, `ScanQueueUnavailableException`; from Issue 2, `FakeAzureDevOps.Respond`, `AzureDevOpsAuthenticationException`, `AzureDevOpsUnavailableException`, `SkipReason`.
- Produces, for Task 3:
  - `public enum EnqueueRunStatus { Completed, Failed, Stopped }` in namespace `Enqueuer`.
  - `EnqueueOrchestrator`'s constructor gains `TimeProvider timeProvider` as its last parameter; `Program` registers `TimeProvider.System`.
  - The summary line, exactly as in Global Constraints, as the orchestrator's last line in every run it starts; the `Listed ...` and `Sent ...` lines are gone.

- [ ] **Step 1: Set #113 to Doing**

- [ ] **Step 2: Add the test package**

`Directory.Packages.props`, Testing group: `Microsoft.Extensions.TimeProvider.Testing` `10.10.0`, commented `<!-- FakeTimeProvider: the run summary's exact duration (Issue 4 design, decision 11). Added 2026-10-09 at RepoScanner's version. -->`. `tests/Enqueuer.Tests/Enqueuer.Tests.csproj`: a `PackageReference`, commented `<!-- FakeTimeProvider: time that moves only when a test says -->`.

Run: `dotnet build Enqueuer.sln` then `dotnet list package --vulnerable --include-transitive`
Expected: 0 warnings; no vulnerable packages.

- [ ] **Step 3: Write the orchestrator's failing tests**

`EnqueueOrchestratorTests.SetUp` passes `_time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1500) }` as the constructor's last argument: each `GetUtcNow()` moves the clock 1.5 seconds, so a run that reads it at its start and at its summary lasts exactly 1500 ms.

A helper, used by every summary test:

```csharp
// Exactly one summary per run, and it is the last line (design, The summary line)
private FakeLogRecord TheSummary()
{
    IReadOnlyList<FakeLogRecord> records = _logger.Collector.GetSnapshot();
    Assert.That(records.Count(record => record.GetStructuredStateValue("EnqueueRunStatus") != null), Is.EqualTo(1), "summary lines");
    FakeLogRecord last = records[records.Count - 1];
    Assert.That(last.GetStructuredStateValue("EnqueueRunStatus"), Is.Not.Null, "the summary is not the last line: " + last.Message);
    return last;
}
```

`FakeLogger` keeps a dictionary as one entry per pair, so `SkippedByReason` reads as `["[Disabled, 1]","[InMaintenance, 0]","[Empty, 0]"]`, as the scanner's `ScannerStatuses` does.

New tests, each asserting on `TheSummary()`:

| Test | Arrange | Assert |
|---|---|---|
| `RunAsync_WhenEverythingIsSent_EndsWithACompletedSummary` | `ThreeToScanAndOneDisabled` | Information; `EnqueueRunStatus` `Completed`; `EnqueueDurationMs` `1500`; `ListedCount` `4`; `OrganizationUrl`; `ToScanCount` `3`; `SkippedRepositoryCount` `1`; `SkippedByReason` `["[Disabled, 1]","[InMaintenance, 0]","[Empty, 0]"]`; `SentCount` `3`; `QueueName` `scan-test`; `NotSentCount` `0`; `ErrorMessage` null; `Message` starts `Enqueue run Completed in 1500 ms: 4 listed in https://dev.azure.com/BlacklightInternalsMo, 3 to queue, 1 skipped ` and ends `, 3 sent to scan-test, 0 not sent` |
| `RunAsync_WithEverySkipReason_CountsEachReason` | the default sample listing | `Completed`; `SkippedRepositoryCount` `3`; `SkippedByReason` `["[Disabled, 1]","[InMaintenance, 1]","[Empty, 1]"]`; `ToScanCount` `0`; `SentCount` `0`; `_queue.Attempts` 0 |
| `RunAsync_NothingListed_EndsWithACompletedSummaryAfterTheWarning` | `{ "count": 0, "value": [] }` | Information; `Completed`; `ListedCount` `0`; the record before it is the "listed no repositories" Warning (decision 8) |
| `RunAsync_WhenAzureDevOpsRefusesThePat_SummaryIsFailedAndTheExceptionGoesUp` | `Respond(HttpStatusCode.Unauthorized, "{}")` | `Assert.ThrowsAsync<AzureDevOpsAuthenticationException>`; Warning; `Failed`; `ListedCount` `0`; `ErrorMessage` equals the thrown exception's `Message`; `Message` ends `. Error: ` plus it |
| `RunAsync_WhenAzureDevOpsIsUnavailable_SummaryIsFailed` | `Respond(HttpStatusCode.NotFound, "<html>404</html>")` | `Assert.ThrowsAsync<AzureDevOpsUnavailableException>`; Warning; `Failed` |
| `RunAsync_WhenTheQueueIsMissing_SummaryIsFailedWithNothingSent` | `ThreeToScanAndOneDisabled`; `FailSend(1, new RequestFailedException(404, "The specified queue does not exist.", "QueueNotFound", null))` | `ScanQueueNotFoundException`; `Failed`; `SentCount` `0`; `NotSentCount` `3`; `ErrorMessage` `The queue scan-test does not exist. Create it, or correct Enqueuer:Queue:Name.` |
| `RunAsync_WhenTheSecondSendFails_SummaryCountsOneSentAndTwoNotSent` | `ThreeToScanAndOneDisabled`; `FailSend(2, new RequestFailedException(503, "busy", "ServerBusy", null))` | `ScanQueueUnavailableException`; `Failed`; `SentCount` `1`; `NotSentCount` `2` |
| `RunAsync_WhenASendTimesOutWithoutBeingStopped_SummaryIsFailed` (decision 12) | `ThreeToScanAndOneDisabled`; `FailSend(1, new OperationCanceledException("The operation was cancelled because it exceeded the configured timeout."))`; `CancellationToken.None` | `ScanQueueUnavailableException`; `Failed`, not `Stopped` |
| `RunAsync_WhenTheJobIsStopped_SummaryIsStopped` | a `CancellationTokenSource` cancelled before the run | `Assert.CatchAsync<OperationCanceledException>` (the HTTP client throws a subclass); Warning; `Stopped`; `ListedCount` `0` |
| `RunAsync_WhenABugIsThrown_SummaryIsFailedAndTheBugGoesUpUnchanged` (decision 13) | `ThreeToScanAndOneDisabled`; `InvalidOperationException bug = new InvalidOperationException("Simulated bug")`; `FailSend(1, bug)` | the caught exception `Is.SameAs(bug)`; `Failed`; `ErrorMessage` `Simulated bug` |
| `RunAsync_NoLongerLogsTheListedOrSentLines` (decision 7) | `ThreeToScanAndOneDisabled` | no record's `Message` starts `Listed ` or `Sent ` |

Existing tests:
- `RunAsync_LogsTheTotals` and `RunAsync_EndsWithHowManyWereSent` are removed: the first two rows replace them.
- `RunAsync_NothingToScan_SendsNothingAndSaysSentZero` keeps `_queue.Attempts` zero and asserts `TheSummary()`'s `SentCount` `0` instead of the `Sent 0` message.
- `RunAsync_WhenTheSecondSendFails_StopsBeforeTheThird`: its last assert becomes "only the summary has a `SentCount`, and it is `1`".
- `RunAsync_LogsHowManyRepositoriesWereListedAndWhere` passes unchanged: it now finds the summary's `ListedCount` and `OrganizationUrl`.

- [ ] **Step 4: Write `Program`'s failing tests**

In `ProgramTests`:
- `RunAsync_WithValidSettings_RunsTheOrchestrator` and `RunAsync_WithARepositoryToScan_SendsItAndReturnsZero` find their `ListedCount` `3` and `SentCount` `1` on the line whose `EnqueueRunStatus` is `Completed`.
- New `RunAsync_WhenASendFails_TheSummaryComesJustBeforeTheFatalLine`: `OneToScan`, `FailSend(1, new RequestFailedException(403, "denied", "AuthorizationPermissionMismatch", null))`; of `LogLines.Read(output)`, the last line is `Fatal`, and the one before it is `Warning` with `EnqueueRunStatus` `Failed`, `SentCount` `0`, `NotSentCount` `1`, and `Value(line, "SkippedByReason")` `{"Disabled":0,"InMaintenance":0,"Empty":0}` (an object in JSON, decision 14).
- Review Focus 5, new `RunAsync_WhenABugsMessageHasALineBreak_EveryLineIsStillOneJsonObject`: `OneToScan`, `FailSend(1, new InvalidOperationException("first line\nsecond line"))`; exit 1; `LogLines.Read(output)` succeeds (it fails the test on any line that is not one JSON object); the summary's `ErrorMessage` is `first line\nsecond line`; the last line is the `Fatal` `Enqueuer failed`, with `@x`.

- [ ] **Step 5: Run the tests to see them fail**

Run: `dotnet test Enqueuer.sln -p:CollectCoverage=false`
Expected: build errors for `EnqueueRunStatus` and the constructor's new parameter.

- [ ] **Step 6: Write `EnqueueRunStatus`, register the clock, and log the summary**

- `src/Enqueuer/EnqueueRunStatus.cs`: the enum, one comment per value (`Stopped`: the job was asked to stop, Ctrl+C or SIGTERM, decision 12).
- `Program.CreateBuilder`: `builder.Services.AddSingleton(TimeProvider.System);` before the orchestrator, commented for decision 11.
- `EnqueueOrchestrator`: the constructor takes and keeps `TimeProvider timeProvider`. `RunAsync` follows the design's "How `RunAsync` keeps its counts":
  - `DateTimeOffset started = _timeProvider.GetUtcNow()` before the start line; `EnqueueDurationMs` is `(long)(_timeProvider.GetUtcNow() - started).TotalMilliseconds`, read when the summary is logged.
  - The counts live in a private nested `sealed class RunCounts`, created before the `try` so the `catch` can log them: `ListedCount`, `ToScanCount`, `SkippedRepositoryCount` and `SentCount`, all 0 until set, and `SkippedByReason`, a `Dictionary<string, int>` filled with every `Enum.GetValues<SkipReason>()` name at 0 in the enum's order (decision 14). After the listing, the first three come from `repositories.Count`, `selection.ToScan.Count` and `selection.Skipped.Count`, and each skipped repository adds 1 to its reason. `NotSentCount` is `ToScanCount - SentCount`, worked out when the summary is logged.
  - The body after the start line is in `try`. Its end logs `Completed`. `catch (Exception exception)` logs `Stopped` when `exception is OperationCanceledException && cancellationToken.IsCancellationRequested`, else `Failed`, with `exception.Message`, then `throw;`.
  - Two `const string`s, `SummaryTemplate` and `FailedSummaryTemplate = SummaryTemplate + ". Error: {ErrorMessage}"`; `LogInformation` with the first when `Completed`, `LogWarning` with the second otherwise, arguments in the template's order.
  - The `Listed ...` and `Sent ...` calls are removed; the `Skipped`, `Queued` and "listed no repositories" lines stay.

Run: `dotnet test Enqueuer.sln --tl:off`
Expected: every test passes, 0 warnings, line coverage of `Enqueuer` at least 80%.

- [ ] **Step 7: Manual check, sending nothing**

As Task 1, Step 10, run 2: `dotnet run --project src/Enqueuer -- --Enqueuer:Queue:Name scan-missing-zz9q1`, with the PAT read in. Expected, as text: the start line, any `Skipped` lines, then `Enqueue run Failed in <n> ms: 5 listed in https://dev.azure.com/BlacklightInternalsMo, 5 to queue, 0 skipped {"Disabled":0,"InMaintenance":0,"Empty":0}, 0 sent to scan-missing-zz9q1, 5 not sent. Error: The queue scan-missing-zz9q1 does not exist. Create it, or correct Enqueuer:Queue:Name.` at `WRN`, then the `FTL` `Queue missing` line; `$LASTEXITCODE` 2. The counts are whatever BlacklightInternalsMo lists that day. Save the output to the workspace.

- [ ] **Step 8: README**

- **What it logs:** the summary replaces the totals and `Sent` lines in the prose; the example becomes Step 7's real output, introduced as a run against a misspelt queue on its date, followed by one line on what a completed run's summary says (Task 3 adds a real one in JSON). One sentence: the summary is the last line the enqueuer itself logs, Information when `Completed`, a warning ending `Error: ...` when `Failed` or `Stopped`, with `Program`'s one line after it on a failure.
- **Known limitations:** on a failure the summary comes just before the Fatal line; a run whose settings are wrong has no summary; when the listing fails the summary says `0 listed`, so read it with its status and error.

- [ ] **Step 9: Review, commit, set #113 to Done**

Stop for the user's review. Once approved:

```bash
git add Directory.Packages.props src/Enqueuer tests/Enqueuer.Tests README.md
git commit -m "This commit ends every enqueue run that gets past start-up with one summary line, logged last by the orchestrator: Completed at Information, or Failed or Stopped at Warning with the error, before Program's one line as before; it gives the duration from an injected TimeProvider, how many were listed in which organisation, to queue, skipped by each reason, sent to which queue and not sent, under names that do not clash with the scanner's, and replaces the Listed and Sent lines; tested for every outcome, with FakeTimeProvider for the duration #113"
```

---

### Task 3: The correlation ID across both apps (#121)

**Files:**
- Modify: `tests/Enqueuer.Tests/ProgramTests.cs`, `README.md`
- Modify: `docs/design/2026-10-09-observability-and-run-summary.md` (#82, its own commit, only if a step ran differently)
- Create: `.superpowers/sdd/2026-10-09-observability-and-run-summary/e2e-results.md`, the record for the pull request (git-ignored, not committed)

RepoScanner is only run, never changed.

**Interfaces:**
- Consumes: Tasks 1 and 2: `--Enqueuer:Logging:Format Json`, `LogLines`, the `Queued` line's `CorrelationId`, the summary. RepoScanner's scanner (`dotnet run --project ..\RepoScanner\src\RepoScanner.Scanner`) with `--Scanner:Logging:Format Json`, whose every line about a request carries its `CorrelationId`, `ProjectName` and `RepositoryName` (RepoScanner Issue 10, #62), ending with `Stopped taking messages from {QueueName}: {Reason}. {ScannedCount} scanned, ...`. RepoScanner's database as in #111.
- Produces: the README's **Logs** section and its updated **Scan what it queued** and **What it logs**; `e2e-results.md`.

- [ ] **Step 1: Set #121 to Doing**

- [ ] **Step 2: The join test**

New `ProgramTests.RunAsync_EachQueuedLine_CarriesTheCorrelationIdOfTheMessageItSent`: a listing of two repositories to scan (`victim-repo` in `Kudos Wall`, `WebShop` in `Retail`, each with a default branch) and a `FakeQueueClient`. From `LogLines.Read(output)`, the lines with a `CorrelationId`, as `Guid`s, equal the sent messages' `correlationId`s as `Guid`s, in order; and each line's `RepositoryName` is its message's `repositoryName`. It tests code that exists, so it passes at once.

Run: `dotnet test Enqueuer.sln -p:CollectCoverage=false --filter FullyQualifiedName~EachQueuedLine`
Expected: PASS. Then check it can fail: in `EnqueueOrchestrator`, log `Guid.NewGuid()` in place of `request.CorrelationId` on the `Queued` line, run it again, expect FAIL, and undo the change. Save both runs to the workspace.

- [ ] **Step 3: Check what the run needs, changing nothing**

| Check | Expected | If not |
|---|---|---|
| Something listens on port 10001 | Azurite, the user's own process | Ask the user to start it |
| `az storage queue list --connection-string "UseDevelopmentStorage=true" --query "[].name" -o tsv` | `scan-nightly` and `scan-nightly-poison` among them | Create them as the README's Run section says |
| The three user variables (length only) | All set | Ask the user |
| `sqlcmd -S .\SQLEXPRESS -E -C -d RepoScanner -h -1 -W -b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM ScanRuns"` | A number, exit 0 | Ask the user |
| `git -C ..\RepoScanner status -sb` | On `develop`, no changes, with `src/RepoScanner.Scanner/Logging/ConsoleLogFormat.cs` | Ask the user before running another branch's scanner |

- [ ] **Step 4: The queue is empty**

Run, from bash: `az storage message peek --queue-name scan-nightly --num-messages 32 --connection-string "UseDevelopmentStorage=true" --query "length(@)" -o tsv`
Expected: `0`. Anything else is the user's: stop and ask.

- [ ] **Step 5: Build both apps**

Run: `dotnet build Enqueuer.sln` and `dotnet build ..\RepoScanner\RepoScanner.sln`
Expected: 0 warnings, 0 errors, so `dotnet run --no-build` prints only the apps' own lines.

- [ ] **Step 6: Run the enqueuer as JSON**

Run, with the PAT read in: `dotnet run --no-build --project src/Enqueuer -- --Enqueuer:Logging:Format Json | Tee-Object -FilePath <workspace>\enqueuer.log`
Expected: every line JSON; one `Queued` line per repository to scan; the summary `Completed` with `NotSentCount` 0; `$LASTEXITCODE` 0.

- [ ] **Step 7: Run the scanner as JSON**

Run from `..\RepoScanner`, with its PAT and connection string read in: `dotnet run --no-build --project src/RepoScanner.Scanner -- --Scanner:Logging:Format Json | Tee-Object -FilePath <workspace>\scanner.log`. Allow up to 15 minutes.
Expected: `Stopped taking messages from scan-nightly: queue empty. <n> scanned, 0 failed, 0 skipped, 0 poisoned`, with `<n>` the number of `Queued` lines; `$LASTEXITCODE` 0. A failed scan or a poisoned message is debugged with superpowers:systematic-debugging before going on; nothing in RepoScanner is changed without asking.

- [ ] **Step 8: One correlation ID, both apps (criterion 2)**

Run, with the first `Queued` line's ID:

```powershell
Get-Content <workspace>\enqueuer.log, <workspace>\scanner.log | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object CorrelationId -eq '<id>' | Select-Object '@t', SourceContext, '@m'
```

Expected: the enqueuer's `Queued` line first (`Enqueuer.EnqueueOrchestrator`), then the scanner's lines for that scan: `Message ...`, `Cloned ...`, one per scanner, its findings, and its `Scan run ...` summary. Then, for every `Queued` ID, the same filter counted: at least 3 scanner lines each.

- [ ] **Step 9: The queues afterwards, and a failed run as JSON**

Run: Step 4's count on `scan-nightly` and `scan-nightly-poison`. Expected: `0` and `0`.
Run: `dotnet run --no-build --project src/Enqueuer -- --Enqueuer:Logging:Format Json --Enqueuer:Queue:Name scan-missing-zz9q1`. Expected: the summary line with `"@l":"Warning"`, `Failed`, `SentCount` 0 and `NotSentCount` the number to queue; then the `Fatal` line starting `Queue missing`; `$LASTEXITCODE` 2.

- [ ] **Step 10: Write the results for the pull request**

`e2e-results.md` in the workspace, under `End to end, <date> (#121)`: for each of Steps 2 to 9, the command and what it printed, the scanner's log cut to the filtered lines of Step 8 and its last line. Before saving, check that none of the three variables' values is in the file.

- [ ] **Step 11: README**

- **What it logs:** Step 6's `Completed` summary as one JSON line, shown across several as the design does, with what `@t`, `@m` and `@l` are.
- **Scan what it queued:** the enqueuer and the scanner run with `--...:Logging:Format Json` and `Tee-Object`, and Step 8's filter, as the design's End to end steps 2 to 5, with what Step 8 actually printed, cut to a few lines.
- **Logs**, a new section after it: the three Kusto searches from the design's Searches, under a line saying they are for Log Analytics once the apps are deployed and have not been run, as there is no Azure in this release (RepoScanner ADR-029); and that the scanner's rejected-PAT alert search also matches the enqueuer's line.
- **Known limitations:** the searches are not yet run against Log Analytics.

- [ ] **Step 12: The design, under #82, if anything ran differently**

Each change in `docs/design/2026-10-09-observability-and-run-summary.md` is marked *after #121* with its date, as Issue 3's design was after #111. Commit it on its own:

```bash
git add docs/design/2026-10-09-observability-and-run-summary.md
git commit -m "This commit brings Issue 4's design up to date after #112 to #121: <what changed> #82"
```

- [ ] **Step 13: Build and test**

Run: `dotnet build Enqueuer.sln` then `dotnet test Enqueuer.sln --tl:off`
Expected: 0 warnings; Task 2's count plus one, all passing; coverage at least 80%.

- [ ] **Step 14: Review, commit, set #121 to Done**

Stop for the user's review of the README and `e2e-results.md`. Once approved:

```bash
git add tests/Enqueuer.Tests/ProgramTests.cs README.md
git commit -m "This commit proves the correlation ID joins the enqueuer's log to the scanner's: a test that each Queued line carries the correlation ID of the message it sent, and, run by hand on <date>, both apps writing JSON locally with one PowerShell filter finding a request's Queued line and every scanner line for its scan; the README gains the Logs section with the Log Analytics searches, not yet run as there is no Azure in this release #121"
```

The commit message's last part is written from what Step 8 actually showed.
