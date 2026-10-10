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

When the PAT is renewed, update both variables, and `.env` if you run the container (see Run in a container). The PAT never goes in an appsettings file.

## Run in a container

In Azure the enqueuer runs from an image, as the scanner does. The image is built in two stages: the .NET 10 SDK compiles and publishes the app, and only the published app is copied into the .NET 10 runtime image, with no SDK and no git, as the enqueuer never clones. It runs as the image's own non-root user, `app` (1654), in the `Production` environment: `appsettings.Development.json` is left out of the published app, so every setting comes from environment variables, as in Azure, and the log is one JSON object per line (ENQ-ADR-002). The design is in [docs/design/2026-10-10-containerisation.md](docs/design/2026-10-10-containerisation.md).

Needs Docker Desktop, using Linux containers. Build it from the repository root:

```
docker build -t enqueuer:v1 .
```

The tag is `v1`, set by hand: this release has no pipeline to tag images with a build ID, and the image is never pushed to a registry (RepoScanner ADR-029; RepoScanner ADR-005, amended 2026-10-04). No image here is tagged `latest`.

### Its settings: .env

The container reads every setting from `.env` at the repository root, which git ignores. `dotnet run` does not read it: it keeps `appsettings.Development.json` and the user environment variable. Once:

1. Copy `.env.example` to `.env`.
2. Replace `<azurite-account-key>` with Azurite's well-known account key, from Microsoft's Azurite documentation. Or copy the whole connection string from the `Scanner__Queue__ConnectionString` line of RepoScanner's `.env`, which is already in this form. It names Azurite as `host.docker.internal:10001`: `UseDevelopmentStorage=true` does not work here, as in the container it points at the container itself.
3. Copy the PAT into it from the user variable, so it never shows on screen:

   ```powershell
   $f = (Resolve-Path .env).ProviderPath; $pat = [Environment]::GetEnvironmentVariable("Enqueuer__AzureDevOps__Pat", "User"); $lines = [IO.File]::ReadAllLines($f) | ForEach-Object { if ($_.StartsWith("Enqueuer__AzureDevOps__Pat=")) { "Enqueuer__AzureDevOps__Pat=$pat" } else { $_ } }; [IO.File]::WriteAllLines($f, $lines, (New-Object Text.UTF8Encoding $false))
   ```

Each line is `NAME=value`, with no quotes and no spaces around the value: Docker keeps both as part of the value. Save it as UTF-8. Docker cannot read UTF-16, which `>` and `Out-File` write in Windows PowerShell 5.1.

### Running it

```
docker run --rm --env-file .env enqueuer:v1
```

It lists the repositories, queues the scan requests to `scan-nightly` in Azurite, prints its log, and exits. The exit code is the enqueuer's (see Exit codes). `--rm` deletes the container when it stops, and its log with it. To keep the log, for Docker Desktop's **Containers**, then **Logs**, or for `docker logs enqueuer-run`, name the container instead:

```
docker run --name enqueuer-run --env-file .env enqueuer:v1
```

Remove it with `docker rm enqueuer-run` before running it again under the same name.

### Check it end to end

```
.\scripts\test-container.ps1
```

Needs Docker Desktop, Azurite, the Azure CLI, a network connection to Azure DevOps, a complete `.env`, and `scan-nightly` empty. It stops at the first check that fails and exits 1, or exits 0 when all 13 pass. It never prints a value from `.env`:

1. Docker is running, with Linux containers.
2. The Azure CLI is installed.
3. `.env` is complete: every setting there, none empty, quoted, padded, still a `<...>` placeholder or `UseDevelopmentStorage=true`, the queue `scan-nightly`, and the file not UTF-16.
4. The image builds, as `enqueuer:v1` (`-Tag` changes the tag).
5. It runs as a non-root user.
6. It has no .NET SDK.
7. It has no git.
8. It has no `appsettings.Development.json`.
9. Every `FROM` in the Dockerfile names a tag, and none is `latest`.
10. A container reaches Azurite on `host.docker.internal:10001`.
11. `scan-nightly` exists and is empty.
12. A run with a bad queue name exits 2 with one `Invalid configuration` Fatal line, and queues nothing.
13. A real run exits 0. Every log line is JSON, the summary is `Completed` with `0 not sent`, and the `Queued` lines, the summary's sent count and the messages on the queue agree, by correlation ID. Neither run's output holds the PAT.

It leaves check 13's requests on `scan-nightly` for the scanner (see Scan what it queued), so the next run stops at check 11 until the scanner has taken them. On 2026-10-10 all 13 checks passed, queuing BlacklightInternalsMo's 5 repositories.

## What it logs

The enqueuer logs one line per skipped repository, with its reason, then one `Queued` line per scan request sent, with its correlation ID, and ends the run with one summary line saying how it went. A repository is skipped when it is disabled, in maintenance, or empty (no default branch); forks are queued like any other.

A run that gets past start-up always ends with the summary, however it ended, and it is the last line of the run itself. A run stopped by the start-up settings check (a setting missing or invalid: one `FTL` line starting `Invalid configuration`, exit code 2) stops before the run starts and has none. A wrong queue name or PAT passes that check and is found during the run, so it has a summary, as the example below shows. The summary is `Information` when the run `Completed`, and a warning ending `Error: ...` when it `Failed` or was `Stopped`; on a failure `Program`'s one `FTL` line comes after it. It gives the run's duration, how many repositories were listed and where, how many are to queue and how many were skipped, by reason, and how many were sent to the queue and how many were not. For example, a run against a misspelt queue name on 2026-10-10, with BlacklightInternalsMo's 5 repositories listed and Azurite running, but none sent:

```
[17:25:57 INF] Enqueuer.EnqueueOrchestrator: Enqueuer starting in Development for queue scan-missing-zz9q1
[17:25:57 WRN] Enqueuer.EnqueueOrchestrator: Enqueue run Failed in 330 ms: 5 listed in https://dev.azure.com/BlacklightInternalsMo, 5 to queue, 0 skipped {"Disabled": 0, "InMaintenance": 0, "Empty": 0}, 0 sent to scan-missing-zz9q1, 5 not sent. Error: The queue scan-missing-zz9q1 does not exist. Create it, or correct Enqueuer:Queue:Name.
[17:25:57 FTL] Enqueuer.Program: Queue missing, enqueuer stopped: The queue scan-missing-zz9q1 does not exist. Create it, or correct Enqueuer:Queue:Name.
```

A completed run's summary is at `INF`, says `Completed`, and ends `N sent to scan-nightly, 0 not sent`, with N the number to queue.

The example is Development's text. Every other environment writes each line as one JSON object: `@t` is the time, `@m` the message and `@l` the level, left out for Information, then every named value as a property of its own, such as `CorrelationId`, so a search can filter on it (ENQ-ADR-002). `dotnet run --project src/Enqueuer -- --Enqueuer:Logging:Format Json` shows it locally. A completed run's summary that way, on 2026-10-10 with BlacklightInternalsMo's 5 repositories, shown across several lines (the real line is one):

```json
{"@t":"2026-10-10T16:48:43.8463178Z",
 "@m":"Enqueue run Completed in 644 ms: 5 listed in \"https://dev.azure.com/BlacklightInternalsMo\", 5 to queue, 0 skipped [(\"Disabled\": 0), (\"InMaintenance\": 0), (\"Empty\": 0)], 5 sent to \"scan-nightly\", 0 not sent",
 "@i":"2d117eb5",
 "EnqueueRunStatus":"Completed","EnqueueDurationMs":644,"ListedCount":5,
 "OrganizationUrl":"https://dev.azure.com/BlacklightInternalsMo","ToScanCount":5,
 "SkippedRepositoryCount":0,"SkippedByReason":{"Disabled":0,"InMaintenance":0,"Empty":0},
 "SentCount":5,"QueueName":"scan-nightly","NotSentCount":0,
 "SourceContext":"Enqueuer.EnqueueOrchestrator"}
```

There is no `@l` because the level is Information, and `@i` is a short ID for the message template. `@m` puts quotes round text values and writes the skip counts its own way, so a search reads the properties, such as `SkippedByReason.Empty`, not `@m`. A `Failed` or `Stopped` summary has `"@l":"Warning"` and an `ErrorMessage`.

A skipped repository looks like `Skipped Kudos Wall/ProblemApp: Disabled`. If Azure DevOps lists none at all, a warning says to check what the PAT's owner can see. The PAT is never logged.

## Scan what it queued

To see RepoScanner's scanner take a run's requests, run both apps locally writing JSON, the enqueuer first, as on 2026-10-10 (#121; by hand on 2026-10-08 for #111). It needs the scanner's one-off setup from RepoScanner's README: **Scan from the queue locally**, for its PAT, and **Database and migrations**, for SQL Server Express, the `RepoScanner` database and `Scanner__Database__ConnectionString`. The commands are PowerShell.

1. Check the queue is empty, so the scanner scans only this run's requests. Peeking changes nothing:

   ```
   az storage message peek --queue-name scan-nightly --num-messages 32 --connection-string "UseDevelopmentStorage=true"
   ```

   If it is not empty, those requests are not this run's: decide what to do with them first, as the scanner would scan them too.

2. Build both apps, so `dotnet run --no-build` does not build again. It still prints its own first line, `Using launch settings from ...`, which is not JSON: step 6's filter skips it.

   ```
   dotnet build Enqueuer.sln
   dotnet build ..\RepoScanner\RepoScanner.sln
   ```

3. Run the enqueuer writing JSON, keeping its output in a file as well as on screen, and keep the correlation IDs from its `Queued` lines:

   ```
   dotnet run --no-build --project src/Enqueuer -- --Enqueuer:Logging:Format Json | Tee-Object -FilePath $env:TEMP\enqueuer.log
   ```

   Every line the enqueuer writes is JSON: a `Skipped` line for each repository it skips, a `Queued` line for each it queues, then the summary `Completed` with `0 not sent`, and `$LASTEXITCODE` is 0. On 2026-10-10 the first request's line was:

   ```json
   {"@t":"2026-10-10T16:48:43.8112874Z","@m":"Queued \"Kudos Wall\"/\"victim-repo\" with correlation fe5f59fd-988d-43e3-928b-4ba02a906783","@i":"ce054e06","ProjectName":"Kudos Wall","RepositoryName":"victim-repo","CorrelationId":"fe5f59fd-988d-43e3-928b-4ba02a906783","SourceContext":"Enqueuer.EnqueueOrchestrator"}
   ```

   The second, for `Kudos Wall`/`Power Platform ALM`, had `fcc9d735-3747-4413-958a-fede964b9035`. The rest of this section follows those two.

   The enqueuer can run in its container for this step instead (see Run in a container). It writes JSON without being told, as in Azure: `docker run --rm --env-file .env enqueuer:v1 | Tee-Object -FilePath $env:TEMP\enqueuer.log`.

4. Peek again: one message per `Queued` line, each plain JSON beginning `{"schemaVersion":1,`.
5. Run the scanner the same way, from RepoScanner's folder:

   ```
   dotnet run --no-build --project src/RepoScanner.Scanner -- --Scanner:Logging:Format Json | Tee-Object -FilePath $env:TEMP\scanner.log
   ```

   It clones and scans each repository in turn, and ends with one line counting them: its `@m` starts `Stopped taking messages from "scan-nightly": "queue empty". 5 scanned, 0 failed, 0 skipped,` and goes on with its other counts, one scanned for each `Queued` line. The 5 took 26 seconds on 2026-10-10, and `$LASTEXITCODE` was 0.
6. Find one correlation ID in both logs, with the ID from a `Queued` line in place of `<id>`:

   ```powershell
   Get-Content $env:TEMP\enqueuer.log, $env:TEMP\scanner.log | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object CorrelationId -eq '<id>' | Select-Object '@t', SourceContext, '@m'
   ```

   The enqueuer's `Queued` line comes first, then the scanner's lines for that scan: `Message ...`, `Cloned ...`, `Scanning ...`, the file walker's and each of the three scanners' lines, a line for each new finding, and `Scan run ... Completed`. Each line is read on its own with `ConvertFrom-Json`, and `StartsWith('{')` skips `dotnet run`'s first line. For `fe5f59fd-988d-43e3-928b-4ba02a906783` on 2026-10-10 it printed 28 lines, 1 from the enqueuer and 27 from the scanner, here cut to the first three and the last (the table cuts each message to the window's width; add `| Format-List` to read them whole):

   ```
   @t                           SourceContext                                       @m
   --                           -------------                                       --
   2026-10-10T16:48:43.8112874Z Enqueuer.EnqueueOrchestrator                        Queued "Kudos Wall"/"victim-repo" wi...
   2026-10-10T16:49:08.8028020Z RepoScanner.Scanner.ScanOrchestrator                Message "7757b36a-8990-4f76-a8cb-9da...
   2026-10-10T16:49:10.4900515Z RepoScanner.Scanner.Sources.AzureDevOpsCloneSource  Cloned "Kudos Wall"/"victim-repo" "m...
   ...
   2026-10-10T16:49:12.5429142Z RepoScanner.Scanner.RepositoryScanRunner            Scan run 1 Completed (Nightly) for "...
   ```

   Every ID from step 3 finds its own: each of the 5 found 1 enqueuer line and between 13 and 27 scanner lines.

7. Find each request's scan run by its correlation ID:

   ```
   sqlcmd -S .\SQLEXPRESS -E -C -d RepoScanner -h -1 -W -s "|" -Q "SET NOCOUNT ON; SELECT s.CorrelationId, r.Name, r.AzureDevOpsId, s.TriggerType, s.Status FROM ScanRuns s JOIN Repositories r ON r.Id = s.RepositoryId WHERE s.CorrelationId IN ('<id>', '<id>')"
   ```

   One row per request, `Nightly` because it came from `scan-nightly`, under the repository's real Azure DevOps ID. On 2026-10-10, for the first two requests of that run, the two IDs above (SQL Server prints GUIDs in capitals):

   ```
   FE5F59FD-988D-43E3-928B-4BA02A906783|victim-repo|A94E21F6-EDD0-4FA7-BE1E-100495DE6249|Nightly|Completed
   FCC9D735-3747-4413-958A-FEDE964B9035|Power Platform ALM|0BBCB27B-B2B1-47EB-9553-1AA8D23DE74E|Nightly|Completed
   ```

The same correlation ID is on every scanner log line about that request, as step 6 showed, and in `ScanRuns.CorrelationId`. Once the apps run in Azure, the same search is a Log Analytics query: see Logs.

## Logs

The searches below are for Log Analytics, once the apps are deployed to Azure Container Apps. They have not been run: there is no Azure in this release (RepoScanner ADR-029), and the local filter above shows only that the property names match, not that Container Apps keeps each line whole in the `Log` column. Container Apps puts each line in the `Log` column of `ContainerAppConsoleLogs` as text, so a search first unpacks it with `parse_json`, as in RepoScanner's README (Logs). The ID in the first is the one from Scan what it queued.

One request, from the enqueuer to the end of its scan:

```kusto
ContainerAppConsoleLogs
| extend e = parse_json(Log)
| where tostring(e.CorrelationId) == "fe5f59fd-988d-43e3-928b-4ba02a906783"
| project Time = todatetime(e["@t"]), Source = tostring(e.SourceContext), Message = tostring(e["@m"])
| order by Time asc
```

Enqueue runs that started but did not complete, in the last week:

```kusto
ContainerAppConsoleLogs
| where TimeGenerated > ago(7d)
| extend e = parse_json(Log)
| where isnotempty(tostring(e.EnqueueRunStatus)) and tostring(e.EnqueueRunStatus) != "Completed"
| project TimeGenerated, Status = tostring(e.EnqueueRunStatus), Sent = toint(e.SentCount), NotSent = toint(e.NotSentCount), Error = tostring(e.ErrorMessage)
```

Nights that queued nothing, in the last week:

```kusto
ContainerAppConsoleLogs
| where TimeGenerated > ago(7d)
| extend e = parse_json(Log)
| where tostring(e.EnqueueRunStatus) == "Completed" and toint(e.SentCount) == 0
| project TimeGenerated, Listed = toint(e.ListedCount), ToQueue = toint(e.ToScanCount), Skipped = toint(e.SkippedRepositoryCount)
```

A completed run that sent nothing is still `Completed` at `Information`, so no warning marks it: this search finds it.

RepoScanner's rejected-PAT alert search also matches the enqueuer's line, since both write a `Fatal` line starting `Azure DevOps refused the token` and tests on each side pin it:

```kusto
ContainerAppConsoleLogs
| extend e = parse_json(Log)
| where tostring(e["@l"]) == "Fatal" and tostring(e["@m"]) startswith "Azure DevOps refused the token"
| project TimeGenerated, ContainerGroupName, Message = tostring(e["@m"])
```

A run stopped by the start-up settings check never starts, so it has no summary and the search for runs that started but did not complete cannot find it: it logs only its `Fatal` line, whose `@m` starts `Invalid configuration`, so look for that too.

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

A run that fails once it has started logs its summary at `WRN` first (see What it logs), then `Program`'s one `FTL` line, which gives the reason and sets the exit code. The examples below are those `FTL` lines. A run stopped by the start-up settings check (`Invalid configuration`, a setting missing or invalid) has no summary, only its one `FTL` line.

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
- A run stopped by the start-up settings check (`Invalid configuration`, exit code 2) has no summary. It stops before the run starts, with one Fatal line.
- When the listing fails, the summary says `0 listed`. That means nothing was listed, not that Azure DevOps listed nothing, so read it with its status and error.
- The searches in Logs have not been run against Log Analytics: there is none in this release (RepoScanner ADR-029). The local filter shows the property names match, not that Container Apps keeps each line whole in `Log`. For Issue 6 to check.
- Nothing runs the container on a schedule locally: it is started by hand. The 02:00 schedule is the Azure job's, in Issue 6.
- `.env` holds its own copy of the PAT. A renewed PAT must be copied there too, or the container exits 1 with `Azure DevOps refused the token`.
- `enqueuer:v1` names what was built, not an exact build: a rebuild picks up Microsoft's newest .NET 10 patch under the same tag, as the scanner's `reposcanner:v1` does.
- `test-container.ps1` peeks at most 32 messages, so check 13 fails, saying why, if more than 32 requests are sent.
- Peeking does not see messages another reader has taken. If the scanner runs while `test-container.ps1` does, check 13's count fails: run them one after the other.
