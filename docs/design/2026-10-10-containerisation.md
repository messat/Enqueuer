# Issue 5: Containerisation (design)

- **Status:** Draft, for review
- **Date:** 2026-10-10
- **Work item:** Issue 5, #83 (tasks #114 and #115)
- **Branch:** `feature/83-containerisation`

## Purpose

The enqueuer runs today with `dotnet run` on the developer's machine. In Azure it will run as a scheduled Container Apps Job, from an image, as the scanner does (RepoScanner ADR-001). This Issue builds that image and proves it locally the way Azure will run it: the enqueuer alone in a container, configured only by environment variables, listing BlacklightInternalsMo's repositories and putting the scan requests on `scan-nightly` in Azurite, outside the container, as the storage account is outside the Container Apps environment in Azure.

It follows the scanner's Issue 9 (RepoScanner `docs/design/2026-10-01-containerisation-and-local-docker-run.md`), without git, which the enqueuer does not need, and without SQL Server, which it never touches.

What happens for one local run:

```
[docker build] -> [enqueuer:v1] -> [docker run --env-file .env] -> [Azure DevOps: list] -> [Azurite: scan-nightly] -> exit 0
```

1. `docker build -t enqueuer:v1 .` compiles the enqueuer with the .NET SDK, then copies only the published app into a .NET runtime image.
2. `docker run --rm --env-file .env enqueuer:v1` starts the enqueuer as the user `app`, with every setting from `.env`, in the Production environment.
3. The enqueuer lists the organisation's repositories with the PAT, and sends one scan request per repository to scan to `scan-nightly` in Azurite, through `host.docker.internal:10001`.
4. It logs its summary, exits 0, and so does the container. `--rm` deletes the stopped container.

The requests stay on `scan-nightly` for the scanner, which can be run next, in its own container or with `dotnet run` (README: Scan what it queued). Nothing starts the enqueuer at 02:00 locally: in Azure that is the job's schedule, in Issue 6 (out of scope, RepoScanner ADR-029). Locally it is started by hand.

## Success criteria

From Issue 5 (#83):

1. The container queues messages to Azurite and exits 0.
2. The image is built from the runtime base, not the SDK.
3. No `:latest` tag is referenced.

#83's reason, "it proves the exit contract inside a container", is met by `scripts/test-container.ps1` showing exit 0 for a good run and exit 2 for a bad setting from inside the container (decision 16).

## Decisions

Decisions 1 to 4 were agreed in the design discussion on 2026-10-10, with decisions 5 to 15 as presented there. Decisions 16 to 24 are proposed here, for review.

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | Base images | `mcr.microsoft.com/dotnet/sdk:10.0` to build, `mcr.microsoft.com/dotnet/runtime:10.0` to run. Tagged `10.0`, never `latest`, an exact patch or a digest | The scanner's bases (Issue 9, decision 5), so the two images share their cached layers and a rebuild picks up Microsoft's newest .NET 10 patch without moving to .NET 11. On 2026-10-10 `sdk:10.0` held SDK 10.0.401, the version `global.json` names, and `runtime:10.0` held runtime 10.0.12. The chiseled runtime image, smaller and with no shell, was turned down: the image checks and any debugging need `sh`, and it would be the one image here unlike the others |
| 2 | Which queue `test-container.ps1` sends to | The one `.env` names, which must be `scan-nightly`. The script refuses to start if the queue is not empty, and leaves its messages there at the end | It tests exactly what a real local run does, and the requests it leaves are the ones the scanner then scans (README: Scan what it queued). A throwaway queue per test run was turned down: it would not test `.env`'s queue. Clearing the messages at the end was turned down: the script could not be sure it deleted only its own |
| 3 | Which exit codes the script proves | 0, from the real run, and 2, from a run with `-e Enqueuer__Queue__Name=Bad` | Together they show the app's exit code is the container's (decision 9). The exit-2 run stops at the settings check, so it uses no network and no PAT. Exit 1 from a wrong PAT was turned down: one more call to Azure DevOps per test run, with a 401 in the organisation's audit log, for a path `ProgramTests` already covers |
| 4 | docker-compose | None. `docker build` and `docker run` | Azurite stays on the developer's machine, so Compose would hold one service, the enqueuer, and be no more than a saved `docker run` command. The scanner decided the same (Issue 9, decision 1; RepoScanner ADR-017 amended 2026-10-01). Issue 2's design listed "docker-compose passing `Enqueuer__AzureDevOps__Pat` to the container" under Issue 5: `.env` does that instead (decision 10) |
| 5 | Two stages | The SDK stage restores and publishes; the final stage holds the runtime and the published app only | The enqueuer never runs a `dotnet` command but its own. The SDK image is several times bigger, and every tool in it is something more to patch |
| 6 | Git | Not installed | #83: the enqueuer lists repositories over HTTP and never clones. The script checks it is absent (check 7) |
| 7 | User | `USER $APP_UID`: the user `app`, number 1654, which Microsoft's .NET images include | Containers run as root unless told otherwise. The number, not the name, lets a platform check the user is not root. The enqueuer writes no files, so `app` needs nowhere writable |
| 8 | Build speed | Copy the shared build files and `src/Enqueuer/Enqueuer.csproj`, restore, then copy `src/` and publish | Docker reuses the restore step until a project file or package version changes, so a code-only change does not download the packages again |
| 9 | How the app starts | `ENTRYPOINT ["dotnet", "Enqueuer.dll"]`, in exec form | The app is the container's first process, so its exit code is the container's exit code, and `docker stop` or Azure's SIGTERM reaches it directly, which the host already handles (Issue 4, decision 12: `Stopped`) |
| 10 | Where the container's settings live | All in the git-ignored `.env` at the repository root, passed with `docker run --env-file .env`. `dotnet run` keeps reading `appsettings.Development.json` and the user environment variable | The scanner's rule (Issue 9, decision 2): one place per way of running, so nothing is passed on the command line. They cannot share one place: the queue connection string differs (decision 13) |
| 11 | The PAT | In `.env` as well as the user environment variables `Enqueuer__AzureDevOps__Pat` and `Scanner__AzureDevOps__Pat`. Copied into `.env` from the user variable without being printed. When it is renewed, all are updated | Keeps decision 10 with no exception, as the scanner does (Issue 9, decision 3). A `.env` left with an old PAT fails loudly: exit 1 with `Azure DevOps refused the token` |
| 12 | Environment | Production, the default when `DOTNET_ENVIRONMENT` is not set, as in Azure. `appsettings.Development.json` is left out of the published app | Proves the image needs nothing but environment variables, which is how Azure configures the job, and that no Development setting can reach the image by accident. Production writes JSON log lines (ENQ-ADR-002), which the script reads |
| 13 | Reaching Azurite | `host.docker.internal:10001`, in the full connection string with Azurite's account `devstoreaccount1` | Docker Desktop's name for the machine the container runs on. It passes the traffic to the machine's own 127.0.0.1, where Azurite listens, so Azurite needs no `--queueHost 0.0.0.0` (checked for the scanner on 2026-10-01). `UseDevelopmentStorage=true` points at 127.0.0.1, which in the container is the container itself |
| 14 | What reaches the build | `.dockerignore` leaves out everything except `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig` and `src/`, and leaves out `bin/` and `obj/` under `src/` | Listing what goes in, rather than what stays out, means `.env` and any secret file added later can never reach an image. `.editorconfig` goes in because the build enforces its style rules as errors. The scanner's file, unchanged |
| 15 | The local image tag | `enqueuer:v1`, never pushed to a registry | There is no pipeline in this release, so no build ID to tag with (RepoScanner ADR-029; ADR-005 amended 2026-10-04, which already covers the scanner's `reposcanner:v1` tagged by hand). A version that names what was built, never `latest` |
| 16 | The end-to-end check | `scripts/test-container.ps1`, written like the scanner's: numbered checks, stopping at the first that fails with exit 1, exit 0 when all pass, never printing a value from `.env`. *After #115, 2026-10-10: unlike the scanner's script, it shows the enqueuer's log only after searching it for the PAT, so even a leak is never printed* | One repeatable command, as the scanner's. An xUnit test driving Docker would make every `dotnet test` need Docker Desktop and Azurite and take minutes |
| 17 | How the script reads the queue | From the host, with `az` and `UseDevelopmentStorage=true` | The host cannot rely on resolving `host.docker.internal`. Both strings name the same Azurite: `.env`'s through Docker Desktop, the host's directly |
| 18 | How the script checks the log | It runs the container with `.env` only, not forcing JSON, and parses every line with `ConvertFrom-Json` | Proves the image's Production default is JSON (ENQ-ADR-002). The scanner's script forces `Scanner__Logging__Format=Json`, which would hide a broken default |
| 19 | Matching the log to the queue | The `Queued` lines, the summary's `SentCount` and the messages peeked from the queue must agree in number, and each message's `correlationId` must be on one `Queued` line | Shows the container sent what it says it sent, and that the correlation ID that joins the two apps' logs (Issue 4, #121) survives the container |
| 20 | The PAT in the log | The script checks the PAT's value appears nowhere in either run's output, without printing it | The PAT is the one secret the enqueuer holds. `ProgramTests` checks the same with a fake PAT; this checks the real one, in the real image |
| 21 | Reading the container's log (Issue 4, Not in Issue 4) | `docker run` prints it in the terminal. To keep it for Docker Desktop's Logs tab or `docker logs`, run with `--name enqueuer-run` instead of `--rm`, and `docker rm enqueuer-run` before the next run. The README says both | `--rm` deletes the container and its log when it stops. The same pattern as the scanner's `reposcanner-scan` |
| 22 | New packages | None | |
| 23 | New ADRs | None | Decisions 1, 4 and 15 follow RepoScanner ADR-005, ADR-017 and ADR-029, and the scanner's Issue 9. ENQ-ADR-001 already covers copying the scanner's files and not keeping them in step |
| 24 | Where this document lives | `docs/design/` | Same as Issues 2 to 4 |

## Project layout

```
Dockerfile                    the two-stage image                               #114
.dockerignore                 only global.json, the shared build files,
                              .editorconfig and src/ go in                      #114
src/Enqueuer/Enqueuer.csproj  appsettings.Development.json left out of the
                              published app                                     #114

.env.example                  the container's settings, with placeholders      #115
.env                          git-ignored, never committed                      #115
scripts/test-container.ps1    the end-to-end check                              #115
README.md                                                                       #114, #115
```

No C# changes. `.gitignore` already ignores `.env`, and not `.env.example`.

## The image (#114)

```dockerfile
# Stage 1: compile with the .NET SDK. Nothing from this stage ships except the published app.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# The project file and shared build files first, so the restore step is reused until one of them changes
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Enqueuer/Enqueuer.csproj src/Enqueuer/
RUN dotnet restore src/Enqueuer/Enqueuer.csproj

COPY src/ src/
RUN dotnet publish src/Enqueuer/Enqueuer.csproj --configuration Release --no-restore --output /app -p:UseAppHost=false

# Stage 2: what ships. The .NET runtime and the published app; no git, as the enqueuer never clones.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# The image's own non-root user, app (1654)
USER $APP_UID
ENTRYPOINT ["dotnet", "Enqueuer.dll"]
```

- `-p:UseAppHost=false` skips the native `Enqueuer` launcher, which `dotnet Enqueuer.dll` does not need.
- `global.json` is copied, so the build fails if `sdk:10.0` ever holds an SDK older than the one the developer uses, rather than quietly building with it.

`.dockerignore`, the scanner's:

```
# Everything stays out of the build except what the Dockerfile copies
*
!global.json
!Directory.Build.props
!Directory.Packages.props
!.editorconfig
!src/
src/**/bin/
src/**/obj/
```

`Enqueuer.csproj` keeps `appsettings.Development.json` out of the published app, while `dotnet run` and the tests still get it next to the built app:

```xml
<None Update="appsettings*.json" CopyToOutputDirectory="PreserveNewest" />
<None Update="appsettings.Development.json" CopyToPublishDirectory="Never" />
```

## Running it (#115)

```bash
docker build -t enqueuer:v1 .
docker run --rm --env-file .env enqueuer:v1
```

The exit code is the enqueuer's, as in the README: 0 when every listed repository was queued or there was nothing to queue, 1 when the enqueuer failed (including a refused PAT), 2 when a setting is wrong or the queue is missing. The log is one JSON object per line.

### .env

Git-ignored, at the repository root. Each line is `NAME=value`, with no quotes: Docker takes everything after the first `=` as the value, quotes included.

| Setting | Value |
|---|---|
| `Enqueuer__Queue__Name` | `scan-nightly` |
| `Enqueuer__Queue__ConnectionString` | `DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=<Azurite's account key>;QueueEndpoint=http://host.docker.internal:10001/devstoreaccount1;` |
| `Enqueuer__AzureDevOps__OrganizationUrl` | `https://dev.azure.com/BlacklightInternalsMo` |
| `Enqueuer__AzureDevOps__Pat` | The same PAT as the user environment variable |

- Azurite's account key is the well-known one Microsoft publishes in the Azurite documentation, the same as in the scanner's `.env`.
- `Enqueuer__Logging__Format` is not set, so the container writes JSON, as in Azure. A line in `.env` changes it, and the script then fails at check 13, saying so.
- #115 writes `.env` with the PAT copied from the user variable `Enqueuer__AzureDevOps__Pat`, never printed.

### .env.example

Committed, with the same four lines and `<...>` placeholders for Azurite's account key and the PAT, and a header saying to copy it to `.env`. RepoScanner's secret scanner treats `<NAME>` as a placeholder, so a self-scan reports nothing in it. The real Azurite key is not written in it: it is public, but SEC002 would still report it.

## The end-to-end check (#115)

`scripts/test-container.ps1`, run from anywhere with Docker Desktop and Azurite running. It finds the repository root from its own folder. Parameters: `-Tag` (`v1` by default) and `-EnvFile` (`.env` at the root by default).

```
[1-3 tools, .env] -> [4-9 the image] -> [10-11 Azurite, the queue] -> [12 exit 2] -> [13 exit 0, the log, the queue]
```

| # | Check | Passes when |
|---|---|---|
| 1 | Docker is running | `docker version` answers, and its server is `linux`. *After #115, 2026-10-10: the Linux check was added, as Docker Desktop in Windows containers mode would fail check 4 with a manifest error that does not say why* |
| 2 | The Azure CLI is installed | `az` is found |
| 3 | `.env` is complete | Every line not blank or a comment is `NAME=value`; no value is empty, quoted, padded with spaces or still a `<...>` placeholder; all four settings are there; `Enqueuer__Queue__Name` is `scan-nightly`; `Enqueuer__Queue__ConnectionString` is not `UseDevelopmentStorage=true` (decision 13). Problems are named by line number and setting, never by value. *After #115, 2026-10-10: the file must not be UTF-16, which .NET reads but docker refuses (`invalid utf8 bytes at line 1`); a UTF-8 file with a byte order mark and Windows line endings passes, and docker reads its values cleanly (both checked by hand)* |
| 4 | The image builds | `docker build -t enqueuer:v1` exits 0 |
| 5 | Runs as a non-root user | `id -u` inside is not 0 |
| 6 | No .NET SDK in the image | `dotnet --list-sdks` inside lists nothing |
| 7 | No git in the image | `command -v git` inside finds nothing |
| 8 | No `appsettings.Development.json` in the image | `/app/appsettings.Development.json` does not exist inside |
| 9 | Base images are tagged | Every `FROM` in the Dockerfile that is not an earlier stage names a tag, and none is `latest` |
| 10 | Azurite is reachable from a container | `busybox:1.37 nc -z -w 5 host.docker.internal 10001` connects |
| 11 | The queue exists and is empty | `az storage queue exists` is true, and `az storage message peek` returns nothing. A missing queue points to README: Run; a queue with messages stops the script, as they are not this run's (decision 2) |
| 12 | A bad setting exits 2 | `docker run --rm --env-file .env -e Enqueuer__Queue__Name=Bad` exits 2; every line is JSON; one line is `Fatal` with `@m` starting `Invalid configuration`; the PAT is not in the output; the queue is still empty |
| 13 | The container queues and exits 0 | `docker run --rm --env-file .env` exits 0; every line is JSON; the last summary line has `EnqueueRunStatus` `Completed` and `NotSentCount` 0; the `Queued` lines, `SentCount` and the messages peeked from the queue agree in number; each message has `schemaVersion` 1 and a `correlationId` that is on one `Queued` line; the PAT is not in the output. *After the final review, 2026-10-10: `SentCount` must be at least 1, as a run that sent nothing makes every count agree at 0 and proves nothing about the container reaching the queue* |

Check 13 ends by saying how many requests it left on `scan-nightly`, for the scanner to scan next. `-e` wins over `--env-file`, so check 12 changes only the queue name.

## README

- **Run in a container**, a new section after Run: the one-off `.env` set-up, the PAT copied without being printed, build, run, reading the log (decision 21), and `test-container.ps1` with what it checks.
- **Run:** step 1 says the PAT is in `.env` too, and to update all three when it is renewed.
- **Scan what it queued:** the enqueuer may be run in its container for step 3.
- **Known limitations:** those below.

## Testing

`dotnet test` is unchanged: no C# changes, and it still needs no Docker, no network, no Azurite and no PAT. The csproj change keeps `appsettings.Development.json` in the build output, so the tests still find it.

| Task | Check |
|---|---|
| #114 | `dotnet publish src/Enqueuer` locally leaves `appsettings.Development.json` out of the publish folder. `docker build -t enqueuer:v1 .` builds; a second build after changing only a `.cs` file reuses the restore step. `dotnet test` still passes with the coverage gate |
| #115 | `test-container.ps1` passes all 13 checks against BlacklightInternalsMo and Azurite; what it printed goes in the pull request. Then each failure path is tried once by hand: Azurite stopped (check 10), a message left on the queue (check 11), a `<...>` left in `.env` (check 3) |

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ENQ-ADR-001):

1. **#83** This design document, then the implementation plan.
2. **#114** `Dockerfile`, `.dockerignore`, the `Enqueuer.csproj` change, and the README's build and run commands.
3. **#115** `.env.example`, `.env`, `scripts/test-container.ps1`, the rest of the README, and the script's output in the pull request.

## Not in Issue 5

| Item | Where it is on the board |
|---|---|
| Pushing the image to the container registry, tagged with the build ID | Issue 6 (#84), out of scope in this release (RepoScanner ADR-029) |
| The job's 02:00 schedule, `id-scanner`'s Message Sender role, `AZURE_CLIENT_ID` and `ServiceUri` | Issue 6 (#84), out of scope |
| A vulnerability scan of the image | Not on the board |

## Known limitations

- Nothing runs the container on a schedule locally: it is started by hand.
- `.env` holds its own copy of the PAT. A renewed PAT must be copied there too, or the container exits 1 with `Azure DevOps refused the token`.
- `enqueuer:v1` names what was built, not an exact build: a rebuild picks up Microsoft's newest .NET 10 patch under the same tag. The scanner's `reposcanner:v1` is the same.
- `az storage message peek` reads at most 32 messages, so check 13 fails, saying why, if more than 32 requests are sent. BlacklightInternalsMo has 5 repositories.
- Peeking does not see messages another reader has taken. If the scanner runs while the script does, check 13's count fails: run them one after the other.
- Check 13 leaves its requests on `scan-nightly`, so the next scanner run scans them (decision 2). The next run of the script stops at check 11 until they are scanned or removed.
