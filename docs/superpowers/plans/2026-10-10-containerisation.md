# Issue 5: Containerisation, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The enqueuer ships as an image that, run locally with only a `.env`, lists BlacklightInternalsMo's repositories, queues the scan requests to `scan-nightly` in Azurite and exits 0. Task 1 (#114) builds the image; Task 2 (#115) gives it its settings and proves it with `scripts/test-container.ps1`.

**Architecture:** Task 1 copies the scanner's Issue 9 Dockerfile and `.dockerignore`, without git and with one project, and keeps `appsettings.Development.json` out of the published app so the container runs as Production. Task 2 adds `.env.example` and the git-ignored `.env`, and a PowerShell script, modelled line by line on RepoScanner's `scripts/test-container.ps1`, whose 13 checks prove the image, exit 2 for a bad setting, and exit 0 for a real run whose log, summary and queue agree. No C# changes.

**Tech Stack:** Docker Desktop 29.8 (Linux engine), `mcr.microsoft.com/dotnet/sdk:10.0` (SDK 10.0.401) and `runtime:10.0` (runtime 10.0.12), `busybox:1.37`, Windows PowerShell 5.1, the `az` CLI, Azurite on 127.0.0.1:10001.

**Spec:** [docs/design/2026-10-10-containerisation.md](../../design/2026-10-10-containerisation.md), committed as `590ba7e` (#83). Read its Decisions, The image, Running it and The end-to-end check before starting; this plan argues from it. The pattern for both tasks is RepoScanner's `Dockerfile`, `.dockerignore`, `.env.example` and `scripts/test-container.ps1`, read from `..\RepoScanner`, never changed.

**Scope:** #114 (Task 1) and #115 (Task 2). After Task 2, the whole branch gets one fresh review, then superpowers:finishing-a-development-branch.

## Global Constraints

- Branch: `feature/83-containerisation` (already created from `develop`, with the design committed).
- **Commits:** one per board task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with the task number, such as `#114`. It never mentions Claude, Opus or any AI tool: no `Co-Authored-By` line. Push and open a pull request only when the user asks.
- **Board:** set the task to Doing when it starts and to Done when its commit is made: `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id <114|115> --state <Doing|Done>`.
- **Exact values:**
  - Image `enqueuer:<Tag>`, `-Tag` default `v1`. Bases `mcr.microsoft.com/dotnet/sdk:10.0 AS build` and `mcr.microsoft.com/dotnet/runtime:10.0 AS final`. `USER $APP_UID`. `ENTRYPOINT ["dotnet", "Enqueuer.dll"]`.
  - `.env` names, in this order: `Enqueuer__Queue__Name`, `Enqueuer__Queue__ConnectionString`, `Enqueuer__AzureDevOps__OrganizationUrl`, `Enqueuer__AzureDevOps__Pat`. Queue `scan-nightly`. Organisation `https://dev.azure.com/BlacklightInternalsMo`.
  - Container connection string: `DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=<azurite-account-key>;QueueEndpoint=http://host.docker.internal:10001/devstoreaccount1;` (placeholder as written in `.env.example`). The script's own connection string, from the host: `UseDevelopmentStorage=true`.
  - Log properties the script reads (Issue 4): `@l`, `@m`, `CorrelationId`, `EnqueueRunStatus`, `SentCount`, `NotSentCount`. Message fields: `schemaVersion` (1), `correlationId`.
- **Secrets:** never print the PAT or Azurite's key, in a command, its output, a log, the results file or a commit. Copy them file to file or variable to file. This session's shell predates the user variables, so read them with `[Environment]::GetEnvironmentVariable("<name>", "User")`.
- **Files Docker reads are UTF-8 without a BOM, LF or CRLF.** Write `.env` with `[System.IO.File]::WriteAllLines(<path>, <lines>, (New-Object System.Text.UTF8Encoding $false))`, never `>`, `Out-File` or `Set-Content`.
- **Script conventions (copied from the scanner's script):** comment-based help with `.SYNOPSIS`, `.DESCRIPTION` listing the checks, `.PARAMETER` and `.EXAMPLE`; `[CmdletBinding()]`; `$ErrorActionPreference = 'Stop'`; root from `Split-Path $PSScriptRoot -Parent`; `Write-Pass`, `Stop-WithFailure`, `Invoke-Quiet`, `Invoke-Shown`, `Invoke-Captured` as in the scanner's script; each check numbered in a comment, its reason in a comment where not obvious, citing the Issue 5 design by decision number; runs under Windows PowerShell 5.1.
- Workspace for logs and results, git-ignored: `.superpowers/sdd/2026-10-10-containerisation/`.
- No em dash (U+2014) in any file. British English.
- `dotnet build Enqueuer.sln` has zero warnings and `dotnet test Enqueuer.sln` passes with the 80% line coverage gate at the end of each task.

## Review Focus

Inputs the spec implies but no check names, most likely to bite first. Each has its step in Task 2.

1. **Docker Desktop in Windows containers mode:** `docker build` fails on a Linux base with an unclear manifest error. Check 1 must fail, saying to switch to Linux containers, when `docker version --format '{{.Server.Os}}'` is not `linux`.
2. **`.env` saved as UTF-16, with a BOM, or with CRLF line endings,** as Windows editors and PowerShell 5.1 write it. Docker cannot read UTF-16 but copes with a UTF-8 BOM and CRLF, while .NET's `ReadAllLines` reads all of them, so check 3 would pass a file Docker misreads. Check 3 must fail a UTF-16 file (first bytes `FF FE` or `FE FF`), saying to save it as UTF-8, and pass a UTF-8 file with a BOM and CRLF, which the container must then read correctly.
3. **Values containing `=` and `;`:** the connection string has several of each. Check 3 must split each line at its first `=` only, and `-e` in check 12 must override only the queue name.
4. **The PAT hidden from a plain search by JSON escaping:** a `+` or `/` in a secret may be written `\u002B` in a JSON line. The PAT check must search the raw output and every string value of every parsed line.
5. **Started from another folder,** as `.\scripts\test-container.ps1` from the root or by full path from `C:\`. The repository path has spaces (`C:\Code Security Analysis EPA\Enqueuer`), so every path passed to `docker` and .NET must be quoted or passed as an argument, and resolved to a full path.

---

### Task 1: The image (#114)

**Files:**
- Create: `Dockerfile`, `.dockerignore`
- Modify: `src/Enqueuer/Enqueuer.csproj`, `README.md`
- Create: `.superpowers/sdd/2026-10-10-containerisation/task1.md`, the record for the pull request (git-ignored, not committed)

**Interfaces:**
- Consumes: `src/Enqueuer/Enqueuer.csproj` (one project, no project references); `ExitCodes` (0, 1, 2); the start-up logger writing JSON outside Development (ENQ-ADR-002).
- Produces, for Task 2: an image built by `docker build -t enqueuer:v1 "<root>"`, whose `/app` holds the published app without `appsettings.Development.json`, running as user 1654, with no SDK and no git; its exit code is the enqueuer's.

- [ ] **Step 1: Set #114 to Doing**

- [ ] **Step 2: Show the Development settings reach the publish folder today**

Run: `dotnet publish src/Enqueuer -c Release -o .superpowers/sdd/2026-10-10-containerisation/publish-before` then `Test-Path .superpowers/sdd/2026-10-10-containerisation/publish-before/appsettings.Development.json`
Expected: `True`.

- [ ] **Step 3: Leave it out of the publish folder**

In `Enqueuer.csproj`, after the existing `<None Update="appsettings*.json" CopyToOutputDirectory="PreserveNewest" />`, add `<None Update="appsettings.Development.json" CopyToPublishDirectory="Never" />`, with the comment updated to say the image runs as Production with environment variables only (design, decision 12).

- [ ] **Step 4: Check it is gone from publish and still in the build output**

Run: Step 2's publish to `publish-after`, then `Test-Path` there, then `dotnet build Enqueuer.sln` and `Test-Path src/Enqueuer/bin/Debug/net10.0/appsettings.Development.json`
Expected: `False`, 0 warnings, `True`.

- [ ] **Step 5: `.dockerignore`**

The scanner's file, byte for byte (design, decision 14).

- [ ] **Step 6: `Dockerfile`**

As the design's The image section, with its comments. Nothing else: no `apt-get`, no `HEALTHCHECK`, no `EXPOSE`.

- [ ] **Step 7: Build it**

Run: `docker build -t enqueuer:v1 .`
Expected: exit 0; `dotnet restore` and `dotnet publish` with 0 warnings.

- [ ] **Step 8: Check the image by hand**

| Run | Expected |
|---|---|
| `docker run --rm --entrypoint id enqueuer:v1 -u` | `1654` |
| `docker run --rm --entrypoint dotnet enqueuer:v1 --list-sdks` | nothing, exit 0 |
| `docker run --rm --entrypoint sh enqueuer:v1 -c 'command -v git'` | nothing, exit 1 |
| `docker run --rm --entrypoint sh enqueuer:v1 -c 'ls /app'` | `Enqueuer.dll`, no `appsettings.Development.json` |
| `docker run --rm enqueuer:v1` (no settings) | one JSON line, `"@l":"Fatal"`, `@m` starting `Invalid configuration`; `$LASTEXITCODE` 2 |
| `docker image ls enqueuer:v1 --format '{{.Size}}'` | recorded in `task1.md` |

The last run proves decision 9 before any `.env` exists: the app's exit code 2 is the container's.

- [ ] **Step 9: Check the restore step is reused**

Add a blank line to the end of `src/Enqueuer/ExitCodes.cs`, run Step 7 again, then undo the change.
Expected: the `COPY src/Enqueuer/Enqueuer.csproj` and `RUN dotnet restore` steps say `CACHED`; `dotnet publish` runs again.

- [ ] **Step 10: README**

A new **Run in a container** section after **Run**, for now with: what the image is (two stages, runtime 10.0, no git, user `app`, Production, JSON logs), `docker build -t enqueuer:v1 .` and `docker run --rm --env-file .env enqueuer:v1` (`.env` is described by Task 2), and that the tag is `v1` by hand and never pushed (RepoScanner ADR-029; ADR-005 amended). Task 2 adds the rest.

- [ ] **Step 11: Build, test, record**

Run: `dotnet build Enqueuer.sln` then `dotnet test Enqueuer.sln --tl:off`
Expected: 0 warnings; every test passes, the same count as on `develop`; coverage at least 80%. Write Steps 2, 4, 7 (its last lines), 8 and 9 into `task1.md`.

- [ ] **Step 12: Review, commit, set #114 to Done**

Stop for the user's review. Once approved:

```bash
git add Dockerfile .dockerignore src/Enqueuer/Enqueuer.csproj README.md
git commit -m "This commit adds the enqueuer's image: a two-stage Dockerfile, sdk:10.0 to restore and publish and runtime:10.0 to run, with no git, as the user app (1654), starting dotnet Enqueuer.dll in exec form so its exit code is the container's; the scanner's .dockerignore, letting in only global.json, the shared build files, .editorconfig and src/; and appsettings.Development.json left out of the published app, so the container runs as Production with environment variables only; built as enqueuer:v1, it exits 2 with one JSON Fatal line when given no settings #114"
```

---

### Task 2: Settings and the end-to-end check (#115)

**Files:**
- Create: `.env.example`, `scripts/test-container.ps1`
- Create: `.env` (git-ignored, never committed)
- Modify: `README.md`
- Modify: `docs/design/2026-10-10-containerisation.md` (#83, its own commit, only if a step ran differently)
- Create: `.superpowers/sdd/2026-10-10-containerisation/e2e-results.md`, the record for the pull request (git-ignored, not committed)

**Interfaces:**
- Consumes: Task 1's image and Dockerfile; the enqueuer's log lines as listed under Exact values; RepoScanner's `.env`, for its `Scanner__Queue__ConnectionString` value, already in the container form.
- Produces: `scripts/test-container.ps1 [-Tag <string> = 'v1'] [-EnvFile <string> = '<root>\.env']`, exit 0 when all 13 checks pass and 1 at the first failure, printing `PASS <check>` or `FAIL <check>: <reason>` and `<n> of 13 checks passed`.

- [ ] **Step 1: Set #115 to Doing**

- [ ] **Step 2: `.env.example`**

The four names in order, with the values from Exact values: `<azurite-account-key>` and `<pat>` as the only placeholders. Header comments as the scanner's: what reads it (`docker run --env-file .env`, README: Run in a container), copy it to `.env`, one `NAME=value` per line with no quotes.

- [ ] **Step 3: `.env`, with no value printed**

Lines 1 and 3 as in `.env.example`. Line 2: `Enqueuer__Queue__ConnectionString=` followed by the value of the `Scanner__Queue__ConnectionString` line in `..\RepoScanner\.env`. Line 4: `Enqueuer__AzureDevOps__Pat=` followed by the user variable `Enqueuer__AzureDevOps__Pat`. Written as Global Constraints says. Then check, printing lengths and booleans only: 4 lines, line 2 contains `host.docker.internal:10001`, line 4's value is the same length as the user variable, and `git status --short` does not list `.env`.

- [ ] **Step 4: The script's checks 1 to 3**

`scripts/test-container.ps1` with its help, parameters and helpers, and:
- **1 `Docker is running`:** `docker` found and `docker version --format '{{.Server.Version}}'` exit 0, else `Start Docker Desktop and try again`; then `{{.Server.Os}}` is `linux`, else `Docker Desktop is using Windows containers: switch to Linux containers` (Review Focus 1).
- **2 `The Azure CLI is installed`:** `az` found, else a pointer to installing it.
- **3 `.env is complete`:** the scanner's loop and messages, with: the file resolved to a full path; a UTF-16 file failed first, from its first two bytes (Review Focus 2); each line split at its first `=` (Review Focus 3); the four names required; `Enqueuer__Queue__Name` must be `scan-nightly`; `Enqueuer__Queue__ConnectionString` must not be `UseDevelopmentStorage=true` (design, decision 13: it points at the container itself); the PAT and the queue name kept in variables for later checks.

Run, from the root with the good `.env`: `.\scripts\test-container.ps1`, with the script stopping after check 3 for now (a temporary `exit 0`).
Expected: three `PASS` lines.

- [ ] **Step 5: Check 3 fails where it should**

Each with a copy of `.env` in the workspace, passed with `-EnvFile`, written by script, never printed:

| Copy | Expected |
|---|---|
| `.env.example` as it is | `FAIL .env is complete: line <n>, Enqueuer__Queue__ConnectionString, still has a placeholder...` |
| The PAT line removed | `... Enqueuer__AzureDevOps__Pat is missing (see .env.example)` |
| The queue name in quotes | `... is in quotes...` |
| The connection string `UseDevelopmentStorage=true` | the decision 13 message |
| Saved as UTF-16 (`[System.Text.Encoding]::Unicode`) | the UTF-16 message |
| Saved as UTF-8 with a BOM and CRLF | `PASS`, and keep the copy for Step 9 |

No output line may contain the PAT: check each run's output with `-notmatch [regex]::Escape($pat)`, printing only the result.

- [ ] **Step 6: Checks 4 to 11**

As the design's table, following the scanner's checks 4 to 9 and 11:
- **4 `Image builds`**, `PASS Image builds (enqueuer:v1)`; **5** prints the user number; **6**; **7 `No git in the image`**, `sh -c 'command -v git'` must exit non-zero with no output, else `git is in the image: the final stage must not install it (design, decision 6)`; **8**, pointing to `CopyToPublishDirectory="Never"` in `Enqueuer.csproj`; **9**, the scanner's `FROM` parser unchanged; **10 `Azurite reachable from a container`**, the scanner's check 11.
- **11 `The queue exists and is empty`:** `az storage queue exists --name <queue> --connection-string UseDevelopmentStorage=true --query exists -o tsv` is `true`, else `create it (README: Run)`; `az storage message peek ... --num-messages 1 --query "length(@)" -o tsv` is `0`, else `<queue> has messages that are not this run's: let the scanner take them, or remove them, and run again (design, decision 2)`.

Run the script, still stopping after check 11.
Expected: 11 `PASS` lines.

- [ ] **Step 7: Checks 12 and 13**

- **A helper, `Assert-PatAbsent`** taking a check name and the output lines: fails with `the PAT is in the enqueuer's output` if any raw line, or any string value of any line parsed as JSON, contains the PAT (Review Focus 4). Never prints the match.
- **A helper, `ConvertFrom-LogLines`** taking a check name and the output lines: each non-blank line through `ConvertFrom-Json`, failing with `line <n> of the enqueuer's log is not JSON (design, decision 18: check .env does not set Enqueuer__Logging__Format)`.
- **12 `A bad setting exits 2`:** `Invoke-Captured docker run --rm --env-file <env> -e Enqueuer__Queue__Name=Bad <image>`; exit 2; all lines JSON; exactly one with `@l` `Fatal`, its `@m` starting `Invalid configuration`; the PAT absent; check 11's peek still `0`.
- **13 `The container queues and exits 0`:** `Invoke-Captured docker run --rm --env-file <env> <image>`, nothing forced; exit 0, else `the enqueuer exited <n>: see its log above (README: Exit codes)`; all lines JSON; the PAT absent; the last line with `EnqueueRunStatus` is `Completed` with `NotSentCount` 0; the `Queued` lines are those whose `@m` starts `Queued ` and that have `CorrelationId`; fail if `SentCount` is over 32 (`peek reads at most 32 messages`); `az storage message peek ... --num-messages 32 -o json`, each `content` through `ConvertFrom-Json`; the number of `Queued` lines, `SentCount` and messages are equal; every message has `schemaVersion` 1 and a `correlationId` that, as a `[guid]`, is one `Queued` line's `CorrelationId`, and no two messages share one.
- The last line: `All 13 checks passed: enqueuer:v1 queued <n> scan requests to <queue> and exited 0. They are left there for the scanner (README: Scan what it queued)`, in green, then `exit 0`.

Remove the temporary `exit 0`.

- [ ] **Step 8: Run it whole**

Run, from the root: `.\scripts\test-container.ps1 *>&1 | Tee-Object -FilePath .superpowers/sdd/2026-10-10-containerisation/run-1.log`
Expected: 13 `PASS` lines, `5` requests queued (BlacklightInternalsMo on 2026-10-10), `$LASTEXITCODE` 0. A failure is debugged with superpowers:systematic-debugging before going on.

- [ ] **Step 9: Run it from another folder, with the BOM and CRLF copy**

The 5 requests from Step 8 are still on `scan-nightly`, so first run, from `..\RepoScanner`: `dotnet run --project src/RepoScanner.Scanner` with its variables read in, until `queue empty`. Then, from `C:\`: `& "<root>\scripts\test-container.ps1" -EnvFile "<workspace>\<Step 5's BOM and CRLF copy>"` (Review Focus 2 and 5).
Expected: 13 `PASS` lines again; check 13's message count proves the container read the BOM file's values.

- [ ] **Step 10: The failure paths the design names**

| Condition | Expected |
|---|---|
| Messages left by Step 9 on `scan-nightly` | `FAIL The queue exists and is empty: ...`, 10 of 13 passed. Then the scanner takes them, as in Step 9 |
| Azurite stopped (ask the user to stop it, and to start it again afterwards) | `FAIL Azurite reachable from a container: ...`, 9 of 13 passed |
| A `FROM` tagged `latest` (a temporary edit to `Dockerfile`, undone afterwards) | `FAIL Base images are tagged: ...` |

Save each output to the workspace.

- [ ] **Step 11: Write the results for the pull request**

`e2e-results.md` in the workspace, under `End to end, <date> (#115)`: Step 8's output whole, then Steps 5, 9 and 10, each as the command and what it printed. Before saving, check the PAT and Azurite's key are not in the file.

- [ ] **Step 12: README**

- **Run in a container**, completed: the one-off `.env` set-up (copy `.env.example`, Azurite's key from the Azurite documentation or RepoScanner's `.env`, the PAT copied from the user variable with a one-line command that never shows it, saved as UTF-8); `docker run --rm --env-file .env enqueuer:v1` and its exit codes; reading the log with `--name enqueuer-run` instead of `--rm`, Docker Desktop's Logs tab or `docker logs enqueuer-run`, and `docker rm enqueuer-run` before the next run (design, decision 21); `scripts/test-container.ps1`, what it needs and its 13 checks in one line each, and that it leaves its requests on `scan-nightly`.
- **Run**, step 1: the PAT is in `.env` too; update all three when it is renewed.
- **Scan what it queued**, step 3: the enqueuer may be run in its container instead.
- **Known limitations:** the design's.

- [ ] **Step 13: The design, under #83, if anything ran differently**

Each change in `docs/design/2026-10-10-containerisation.md` is marked *after #115* with its date. Commit it on its own:

```bash
git add docs/design/2026-10-10-containerisation.md
git commit -m "This commit brings Issue 5's design up to date after #114 and #115: <what changed> #83"
```

- [ ] **Step 14: Build and test**

Run: `dotnet build Enqueuer.sln` then `dotnet test Enqueuer.sln --tl:off`
Expected: 0 warnings; Task 1's count, all passing; coverage at least 80%.

- [ ] **Step 15: Review, commit, set #115 to Done**

Stop for the user's review of the script, the README and `e2e-results.md`. Once approved:

```bash
git add .env.example scripts/test-container.ps1 README.md
git commit -m "This commit proves the enqueuer's container end to end: .env.example with the four settings and placeholders, the git-ignored .env reaching Azurite through host.docker.internal; and scripts/test-container.ps1, whose 13 checks, run on <date>, built enqueuer:v1, found it non-root with no SDK, no git and no Development settings, saw a bad setting exit 2 and a real run queue <n> requests to scan-nightly and exit 0, its JSON log, summary and the messages agreeing by correlation ID, with the PAT in neither run's output; the README gains Run in a container #115"
```

`git status --short` must not list `.env` before the commit. The message's counts are written from what Step 8 actually printed.
