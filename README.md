# Enqueuer

The scheduled job that lists every repository in the Azure DevOps organisation and puts one scan request per repository on `scan-nightly`, for RepoScanner's scanner to pick up. The decisions behind it are in [docs/adr](docs/adr/README.md), and each Issue's design is in [docs/design](docs/design).

## Build and test

Needs the .NET 10 SDK: 10.0.401, or a later feature band, as `global.json` sets.

```
dotnet build Enqueuer.sln
dotnet test Enqueuer.sln
```

`dotnet test` fails when line coverage of the `Enqueuer` project drops below 80%. This release has no pipeline (RepoScanner ADR-029), so run it before completing a pull request. In an interactive terminal, add `--tl:off` to see the coverage table. The tests need no network and no PAT: a fake stands in for Azure DevOps.

## Run

The enqueuer lists the repositories in Azure DevOps, so it needs a network connection and a personal access token (PAT). It uses the scanner's PAT: one token, Code (Read) only, in two user environment variables (Issue 2 design, decision 3).

1. If `Scanner__AzureDevOps__Pat` is already set, copy it, so the token never shows on screen or in the terminal's history:

   ```powershell
   [Environment]::SetEnvironmentVariable("Enqueuer__AzureDevOps__Pat", [Environment]::GetEnvironmentVariable("Scanner__AzureDevOps__Pat", "User"), "User")
   ```

   Otherwise, in BlacklightInternalsMo open **User settings**, then **Personal access tokens**, then **New Token**: organisation **BlacklightInternalsMo**, scope **Code: Read**, a short expiry. Then set it in both variables, typing it where it is not shown:

   ```powershell
   $t = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR((Read-Host "New PAT" -AsSecureString))); "Scanner__AzureDevOps__Pat","Enqueuer__AzureDevOps__Pat" | ForEach-Object { [Environment]::SetEnvironmentVariable($_, $t, "User") }
   ```

2. Open a new terminal, or restart Visual Studio or VS Code, so it sees the variable.
3. Run it:

   ```
   dotnet run --project src/Enqueuer
   ```

`dotnet run` uses the `Development` environment (`Properties/launchSettings.json`), so the other settings come from `appsettings.Development.json`. The enqueuer runs once and exits; it never waits.

When the PAT is renewed, update both variables. The PAT never goes in an appsettings file.

## What it logs

One line per skipped repository with its reason, then the totals. A repository is skipped when it is disabled, in maintenance, or empty (no default branch); forks are queued like any other. For example, against BlacklightInternalsMo on 2026-10-08:

```
[14:02:45 INF] Enqueuer.EnqueueOrchestrator: Enqueuer starting in Development for queue scan-nightly
[14:02:46 INF] Enqueuer.EnqueueOrchestrator: Listed 5 repositories in https://dev.azure.com/BlacklightInternalsMo: 5 to queue, 0 skipped
[14:02:46 INF] Enqueuer.EnqueueOrchestrator: Nothing sent: sending scan requests arrives in Issue 3
```

A skipped repository looks like `Skipped Kudos Wall/ProblemApp: Disabled`. If Azure DevOps lists none at all, a warning says to check what the PAT's owner can see. The PAT is never logged.

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

## Known limitations

- The PAT acts as the person who created it. A repository they cannot see is not listed, and nothing says so unless no repository at all is listed.
- Hidden repositories are left out, as the Azure DevOps API does by default.
- A refused PAT is recognised by sending `X-TFS-FedAuthRedirect: Suppress`, whose effect was checked by hand on 2026-10-08. If Azure DevOps stopped honouring it, its sign-in page (a `203` or a redirect) is still treated as refused.
- The listing is a snapshot. A repository created after it is queued the next night.
- One organisation per enqueuer deployment, as for the scanner.
