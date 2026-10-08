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

One line per skipped repository with its reason, then the totals. A repository is skipped when it is disabled, in maintenance, or empty (no default branch); forks are queued like any other. Then one `Queued` line per scan request sent, with its correlation ID, and how many were sent. For example, against BlacklightInternalsMo and Azurite on 2026-10-08:

```
[19:15:08 INF] Enqueuer.EnqueueOrchestrator: Enqueuer starting in Development for queue scan-nightly
[19:15:08 INF] Enqueuer.EnqueueOrchestrator: Listed 5 repositories in https://dev.azure.com/BlacklightInternalsMo: 5 to queue, 0 skipped
[19:15:09 INF] Enqueuer.EnqueueOrchestrator: Queued Kudos Wall/victim-repo with correlation 7b7ebe86-b7ac-487f-984b-29ea19d88dee
[19:15:09 INF] Enqueuer.EnqueueOrchestrator: Queued Kudos Wall/Power Platform ALM with correlation 7840f525-4e40-4546-8a9f-12397f0bb03e
[19:15:09 INF] Enqueuer.EnqueueOrchestrator: Queued Kudos Wall/Kudos-Wall with correlation b8a56615-9cf5-43e9-8900-3ee01db3a1d1
[19:15:09 INF] Enqueuer.EnqueueOrchestrator: Queued Kudos Wall/ProblemApp with correlation aaa0a43c-8285-49c1-9bca-45238e767e87
[19:15:09 INF] Enqueuer.EnqueueOrchestrator: Queued Kudos Wall/DynamicsWorkbench with correlation ac333c1c-4e88-44d4-828b-512bb77abd26
[19:15:09 INF] Enqueuer.EnqueueOrchestrator: Sent 5 scan requests to scan-nightly
```

A skipped repository looks like `Skipped Kudos Wall/ProblemApp: Disabled`. If Azure DevOps lists none at all, a warning says to check what the PAT's owner can see. The PAT is never logged.

The scanner keeps each correlation ID on the scan run it makes from that request, so `SELECT Status FROM ScanRuns WHERE CorrelationId = '<id>'` in its database finds it. To see the requests before the scanner takes them, peek at the queue, which changes nothing:

```
az storage message peek --queue-name scan-nightly --num-messages 32 --connection-string "UseDevelopmentStorage=true"
```

## Configuration

Settings are read from appsettings files, then environment variables, then the command line. Each group is checked when the app starts, so a missing or wrong setting stops it with one line and exit code 2.

| Setting | Required | Development value | Notes |
|---|---|---|---|
| `Enqueuer:Queue:Name` | Yes | `scan-nightly` | 3 to 63 lowercase letters, numbers and hyphens |
| `Enqueuer:Queue:ConnectionString` | One of these two | `UseDevelopmentStorage=true` | Local only (Azurite) |
| `Enqueuer:Queue:ServiceUri` | One of these two | | Azure only, with the job's managed identity (RepoScanner ADR-007). An absolute `https` URL, such as `https://<account>.queue.core.windows.net` |
| `Enqueuer:AzureDevOps:OrganizationUrl` | Yes | `https://dev.azure.com/BlacklightInternalsMo` | An absolute `https` URL with no user name, query string or fragment. The organisation the scanner clones from |
| `Enqueuer:AzureDevOps:Pat` | Yes | | Locally the user environment variable `Enqueuer__AzureDevOps__Pat` (see Run); in Azure a Key Vault secret reference. Never in an appsettings file. No space, line break or quote mark at either end |

As environment variables, `:` becomes `__`, for example `Enqueuer__Queue__Name`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Every listed repository was queued, or there was nothing to queue |
| 1 | Azure DevOps refused the PAT, Azure DevOps or the queue could not be reached, a send failed, or something else went wrong |
| 2 | A setting is missing or invalid, so the enqueuer did not start, or the queue is missing |

A refused PAT, whether wrong, expired or revoked, stops the enqueuer within seconds with one line and exit code 1. The scanner logs the same first words, so one alert covers both. With a wrong PAT on 2026-10-08:

```
[14:09:52 FTL] Enqueuer.Program: Azure DevOps refused the token, enqueuer stopped: Azure DevOps refused the token in Enqueuer:AzureDevOps:Pat for https://dev.azure.com/BlacklightInternalsMo. Check that it has not expired or been revoked, and that it has Code (Read).
```

Any other failure to list the repositories, after up to 3 retries over at most 2 minutes, is one line starting `Azure DevOps could not list the repositories, enqueuer stopped:`, also with exit code 1.

A missing queue is found on the first send, and stops the enqueuer with one line and exit code 2. The scanner logs the same first words. With a misspelt queue name on 2026-10-08:

```
[19:15:22 FTL] Enqueuer.Program: Queue missing, enqueuer stopped: The queue scan-missing-zz9q1 does not exist. Create it, or correct Enqueuer:Queue:Name.
```

A send that still fails after the storage SDK's own retries, 5 of them, stops the enqueuer at that request with one line and exit code 1. The requests already sent stay on the queue, and the rest are not tried. The line gives Azure's status and error code, such as `Azure answered 403 (AuthorizationPermissionMismatch).`, or, when the queue cannot be reached at all, Azure's own summary of the retries. With Azurite not running, it comes after about 40 seconds and starts:

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
- The enqueuer sends Azure DevOps' repository names unchanged. A name the scanner would refuse, possible only if it was made through the API around Azure DevOps' naming rules, gets that one request moved to `scan-nightly-poison` by the scanner, with its warning.
- The enqueuer's copy of the scan request is tied to the scanner's only by the contract tests on each side, which read the same example message (ENQ-ADR-001).
