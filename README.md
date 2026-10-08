# Enqueuer

The scheduled job that lists every repository in the Azure DevOps organisation and puts one scan request per repository on `scan-nightly`, for RepoScanner's scanner to pick up. The decisions behind it are in [docs/adr](docs/adr/README.md).

## Build and test

Needs the .NET 10 SDK: 10.0.401, or a later feature band, as `global.json` sets.

```
dotnet build Enqueuer.sln
dotnet test Enqueuer.sln
```

`dotnet test` fails when line coverage of the `Enqueuer` project drops below 80%. This release has no pipeline (RepoScanner ADR-029), so run it before completing a pull request. In an interactive terminal, add `--tl:off` to see the coverage table.

## Run

```
dotnet run --project src/Enqueuer
```

`dotnet run` uses the `Development` environment (`Properties/launchSettings.json`), so the settings come from `appsettings.Development.json`. The enqueuer runs once and exits; it never waits.

## Configuration

Settings are read from appsettings files, then environment variables, then the command line. Each group is checked when the app starts, so a missing or wrong setting stops it with one line and exit code 2.

| Setting | Required | Development value | Notes |
|---|---|---|---|
| `Enqueuer:Queue:Name` | Yes | `scan-nightly` | 3 to 63 lowercase letters, numbers and hyphens |
| `Enqueuer:Queue:ConnectionString` | One of these two | `UseDevelopmentStorage=true` | Local only (Azurite) |
| `Enqueuer:Queue:ServiceUri` | One of these two | | Azure only, with the job's managed identity (RepoScanner ADR-007) |

As environment variables, `:` becomes `__`, for example `Enqueuer__Queue__Name`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Every listed repository was queued, or there was nothing to queue |
| 1 | Azure DevOps or the queue could not be reached, a send failed, or something else went wrong |
| 2 | A setting is missing or invalid, or the queue is missing, so the enqueuer did not start |
