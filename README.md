# Enqueuer

The scheduled job that lists every repository in the Azure DevOps organisation and puts one scan request per repository on `scan-nightly`, for RepoScanner's scanner to pick up. The decisions behind it are in [docs/adr](docs/adr/README.md), and each Issue's design is in [docs/design](docs/design).

## Build and test

Needs the .NET 10 SDK: 10.0.401, or a later feature band, as `global.json` sets.

```
dotnet build Enqueuer.sln
dotnet test Enqueuer.sln
```

`dotnet test` fails when line coverage of the `Enqueuer` project drops below 80%. This release has no pipeline (RepoScanner ADR-029), so run it before completing a pull request. In an interactive terminal, add `--tl:off` to see the coverage table. The tests need no network, no PAT and no Azurite: fakes stand in for Azure DevOps and the queue.

## Run

The enqueuer lists the repositories in Azure DevOps, so it needs a network connection and a personal access token (PAT). It uses the scanner's PAT: one token, Code (Read) only, in two user environment variables (Issue 2 design, decision 3). It puts the scan requests on `scan-nightly` in Azurite, the local storage emulator, which the scanner reads from too.

1. If `Scanner__AzureDevOps__Pat` is already set, copy it, so the token never shows on screen or in the terminal's history:

   ```powershell
   [Environment]::SetEnvironmentVariable("Enqueuer__AzureDevOps__Pat", [Environment]::GetEnvironmentVariable("Scanner__AzureDevOps__Pat", "User"), "User")
   ```

   Otherwise, in BlacklightInternalsMo open **User settings**, then **Personal access tokens**, then **New Token**: organisation **BlacklightInternalsMo**, scope **Code: Read**, a short expiry. Then set it in both variables, typing it where it is not shown:

   ```powershell
   $t = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR((Read-Host "New PAT" -AsSecureString))); "Scanner__AzureDevOps__Pat","Enqueuer__AzureDevOps__Pat" | ForEach-Object { [Environment]::SetEnvironmentVariable($_, $t, "User") }
   ```

2. Open a new terminal, or restart Visual Studio or VS Code, so it sees the variable.
3. Start Azurite in a terminal of its own, as for the scanner. `--location` keeps the queues and messages between restarts:

   ```
   npm install -g azurite
   azurite --location c:\azurite_data
   ```

   Then create the queue, and the scanner's poison queue, once. The enqueuer never creates them: in Azure its identity may only add messages (Issue 3 design, decision 9). Creating one that exists already does nothing:

   ```powershell
   "scan-nightly", "scan-nightly-poison" | ForEach-Object { az storage queue create --name $_ --connection-string "UseDevelopmentStorage=true" --only-show-errors --output none }
   ```

   RepoScanner's `scripts/enqueue-local.ps1` creates them too, but adds a message as well.

4. Run it:

   ```
   dotnet run --project src/Enqueuer
   ```

`dotnet run` uses the `Development` environment (`Properties/launchSettings.json`), so the other settings come from `appsettings.Development.json`. The enqueuer runs once and exits; it never waits.

When the PAT is renewed, update both variables. The PAT never goes in an appsettings file.

## What it logs

The enqueuer logs one line per skipped repository, with its reason, then one `Queued` line per scan request sent, with its correlation ID, and ends the run with one summary line saying how it went. A repository is skipped when it is disabled, in maintenance, or empty (no default branch); forks are queued like any other.

A run that gets past start-up always ends with the summary, however it ended, and it is the last line of the run itself. A run whose settings are wrong stops before the run starts and has none. The summary is `Information` when the run `Completed`, and a warning ending `Error: ...` when it `Failed` or was `Stopped`; on a failure `Program`'s one `FTL` line comes after it. It gives the run's duration, how many repositories were listed and where, how many are to queue and how many were skipped, by reason, and how many were sent to the queue and how many were not. For example, a run against a misspelt queue name on 2026-10-10, with BlacklightInternalsMo's 5 repositories listed and Azurite running, but none sent:

```
[17:25:57 INF] Enqueuer.EnqueueOrchestrator: Enqueuer starting in Development for queue scan-missing-zz9q1
[17:25:57 WRN] Enqueuer.EnqueueOrchestrator: Enqueue run Failed in 330 ms: 5 listed in https://dev.azure.com/BlacklightInternalsMo, 5 to queue, 0 skipped {"Disabled": 0, "InMaintenance": 0, "Empty": 0}, 0 sent to scan-missing-zz9q1, 5 not sent. Error: The queue scan-missing-zz9q1 does not exist. Create it, or correct Enqueuer:Queue:Name.
[17:25:57 FTL] Enqueuer.Program: Queue missing, enqueuer stopped: The queue scan-missing-zz9q1 does not exist. Create it, or correct Enqueuer:Queue:Name.
```

A completed run's summary is at `INF`, says `Completed`, and ends `N sent to scan-nightly, 0 not sent`, with N the number to queue.

The example is Development's text. Every other environment writes each line as one JSON object: `@t` is the time, `@m` the message and `@l` the level, left out for Information, then every named value as a property of its own, such as `CorrelationId`, so a search can filter on it (ENQ-ADR-002). `dotnet run --project src/Enqueuer -- --Enqueuer:Logging:Format Json` shows it locally.

A skipped repository looks like `Skipped Kudos Wall/ProblemApp: Disabled`. If Azure DevOps lists none at all, a warning says to check what the PAT's owner can see. The PAT is never logged.

## Scan what it queued

To see RepoScanner's scanner take a run's requests, run it locally after the enqueuer, as on 2026-10-08 (#111). It needs the scanner's one-off setup from RepoScanner's README: **Scan from the queue locally**, for its PAT, and **Database and migrations**, for SQL Server Express, the `RepoScanner` database and `Scanner__Database__ConnectionString`.

1. Check the queue is empty, so the scanner scans only this run's requests. Peeking changes nothing:

   ```
   az storage message peek --queue-name scan-nightly --num-messages 32 --connection-string "UseDevelopmentStorage=true"
   ```

   If it is not empty, those requests are not this run's: decide what to do with them first, as the scanner would scan them too.

2. Run the enqueuer (`dotnet run --project src/Enqueuer`) and keep the correlation IDs from its `Queued` lines.
3. Peek again: one message per `Queued` line, each plain JSON beginning `{"schemaVersion":1,`.
4. Run the scanner, from RepoScanner's folder: `dotnet run --project src/RepoScanner.Scanner`. It clones and scans each repository in turn, and ends with one line counting them, such as `Stopped taking messages from scan-nightly: queue empty. 5 scanned, 0 failed, 0 skipped, 0 poisoned`: one scanned for each `Queued` line.
5. Find each request's scan run by its correlation ID:

   ```
   sqlcmd -S .\SQLEXPRESS -E -C -d RepoScanner -h -1 -W -s "|" -Q "SET NOCOUNT ON; SELECT s.CorrelationId, r.Name, r.AzureDevOpsId, s.TriggerType, s.Status FROM ScanRuns s JOIN Repositories r ON r.Id = s.RepositoryId WHERE s.CorrelationId IN ('<id>', '<id>')"
   ```

   One row per request, `Nightly` because it came from `scan-nightly`, under the repository's real Azure DevOps ID. On 2026-10-08, for the first two requests of that day's run (SQL Server prints GUIDs in capitals):

   ```
   A0A59D91-CF9F-46AE-BF2B-02D5E9E5F299|victim-repo|A94E21F6-EDD0-4FA7-BE1E-100495DE6249|Nightly|Completed
   D7182A28-1CAC-4FEC-AA12-F3875D44C062|Power Platform ALM|0BBCB27B-B2B1-47EB-9553-1AA8D23DE74E|Nightly|Completed
   ```

The same correlation ID is on every scanner log line about that request, so it also finds the scan in the scanner's log.

## Configuration

Settings are read from appsettings files, then environment variables, then the command line. Each group is checked when the app starts, so a missing or wrong setting stops it with one line and exit code 2.

| Setting | Required | Development value | Notes |
|---|---|---|---|
| `Enqueuer:Queue:Name` | Yes | `scan-nightly` | 3 to 63 lowercase letters, numbers and hyphens |
| `Enqueuer:Queue:ConnectionString` | One of these two | `UseDevelopmentStorage=true` | Local only (Azurite) |
| `Enqueuer:Queue:ServiceUri` | One of these two | | Azure only, with the job's managed identity (RepoScanner ADR-007). An absolute `https` URL, such as `https://<account>.queue.core.windows.net` |
| `Enqueuer:AzureDevOps:OrganizationUrl` | Yes | `https://dev.azure.com/BlacklightInternalsMo` | An absolute `https` URL with no user name, query string or fragment. The organisation the scanner clones from |
| `Enqueuer:AzureDevOps:Pat` | Yes | | Locally the user environment variable `Enqueuer__AzureDevOps__Pat` (see Run); in Azure a Key Vault secret reference. Never in an appsettings file. No space, line break or quote mark at either end |
| `Enqueuer:Logging:Format` | No | (not set) | `Text` or `Json`, in any case. Not set: text in Development, one JSON object per line everywhere else (ENQ-ADR-002). Any other value stops the enqueuer with exit code 2 |

As environment variables, `:` becomes `__`, for example `Enqueuer__Queue__Name`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Every listed repository was queued, or there was nothing to queue |
| 1 | Azure DevOps refused the PAT, Azure DevOps or the queue could not be reached, a send failed, or something else went wrong |
| 2 | A setting is missing or invalid, so the enqueuer did not start, or the queue is missing |

A run that fails once it has started logs its summary at `WRN` first (see What it logs), then `Program`'s one `FTL` line, which gives the reason and sets the exit code. The examples below are those `FTL` lines. A run whose settings are wrong has no summary, only its one `FTL` line.

A refused PAT, whether wrong, expired or revoked, stops the enqueuer within seconds with its summary, one `FTL` line and exit code 1. The scanner logs the same first words, so one alert covers both. With a wrong PAT on 2026-10-08:

```
[14:09:52 FTL] Enqueuer.Program: Azure DevOps refused the token, enqueuer stopped: Azure DevOps refused the token in Enqueuer:AzureDevOps:Pat for https://dev.azure.com/BlacklightInternalsMo. Check that it has not expired or been revoked, and that it has Code (Read).
```

Any other failure to list the repositories, after up to 3 retries over at most 2 minutes, is one `FTL` line starting `Azure DevOps could not list the repositories, enqueuer stopped:`, after the summary, also with exit code 1.

A missing queue is found on the first send, and stops the enqueuer with its summary, one `FTL` line and exit code 2. The scanner logs the same first words. With a misspelt queue name on 2026-10-08:

```
[19:15:22 FTL] Enqueuer.Program: Queue missing, enqueuer stopped: The queue scan-missing-zz9q1 does not exist. Create it, or correct Enqueuer:Queue:Name.
```

A send that still fails after the storage SDK's own retries, 5 of them, stops the enqueuer at that request with its summary, one `FTL` line and exit code 1. The requests already sent stay on the queue, and the rest are not tried; the summary counts them as not sent. The line gives Azure's status and error code, such as `Azure answered 403 (AuthorizationPermissionMismatch).`, or, when the queue cannot be reached at all, Azure's own summary of the retries. With Azurite not running, it comes after about 40 seconds and starts:

```
[19:16:13 FTL] Enqueuer.Program: Scan requests could not be sent, enqueuer stopped: The scan request for Kudos Wall/victim-repo could not be sent to scan-nightly: Retry failed after 6 tries. Retry settings can be adjusted in ClientOptions.Retry or by configuring a custom retry policy in ClientOptions.RetryPolicy. (No connection could be made because the target machine actively refused it. ...
```

The reason in brackets is repeated once for each try.

## Known limitations

- The PAT acts as the person who created it. A repository they cannot see is not listed, and nothing says so unless no repository at all is listed.
- Hidden repositories are left out, as the Azure DevOps API does by default.
- A refused PAT is recognised by sending `X-TFS-FedAuthRedirect: Suppress`, whose effect was checked by hand on 2026-10-08. If Azure DevOps stopped honouring it, its sign-in page (a `203` or a redirect) is still treated as refused.
- The listing is a snapshot. A repository created after it is queued the next night.
- One organisation per enqueuer deployment, as for the scanner.
- A missing queue is found only when there is something to send, after Azure DevOps has been called. With no repository to scan, the enqueuer exits 0 without touching the queue.
- After a failed send, the requests already sent stay on the queue, and a re-run sends every repository again. Until RepoScanner Issue 13 (#76) drops requests already satisfied, those repositories are scanned twice, which is harmless: findings are matched on fingerprint.
- One send that keeps timing out takes about 10 minutes across its 6 attempts of up to 100 seconds each.
- A missing queue gives exit code 2 locally. In Azure a misspelt queue name may be answered `403`, not `404`, because the job's identity may only add messages to `scan-nightly`, so it would exit 1 instead. This is still to be checked when the job is deployed (Issue 6).
- The enqueuer sends Azure DevOps' repository names unchanged. A name the scanner would refuse, possible only if it was made through the API around Azure DevOps' naming rules, gets that one request moved to `scan-nightly-poison` by the scanner, with its warning.
- The enqueuer's copy of the scan request is tied to the scanner's only by the contract tests on each side, which read the same example message (ENQ-ADR-001).
- On a failure the summary comes just before the Fatal line, not after it: the orchestrator logs the summary, then `Program` logs the Fatal line when the exception reaches it.
- A run whose settings are wrong has no summary. It stops before the run starts, with one Fatal line.
- When the listing fails, the summary says `0 listed`. That means nothing was listed, not that Azure DevOps listed nothing, so read it with its status and error.
