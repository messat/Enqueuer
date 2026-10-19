# Issue 6: Scheduled job and deployment, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A merge into `develop` builds, tests and deploys the enqueuer to the dev environment with no step done by hand, and `job-enqueuer-dev` fills `scan-nightly` at 02:00 UTC. Task 1 (#116) is the pipeline's Build and Image stages; Task 2 (#117) the script that creates or updates `job-enqueuer-dev` and the Deploy stage, proved by the first run on `develop`; Task 3 (#116) the build validation policy, the ADR amendment and the README; Task 4 (#119) the first night.

**Architecture:** One `azure-pipelines.yml` at the repository root with three stages, Build, Image and Deploy, on a Microsoft-hosted `ubuntu-latest` agent. Image and Deploy run only for `develop` and never for a pull request. Image and Deploy sign in through the service connection `sc-reposcanner-dev` as `id-pipeline`; Deploy runs `deploy/Deploy-EnqueuerJob.ps1`, which looks for the job and creates or updates it with every setting. No C# changes.

**Tech Stack:** Azure Pipelines YAML, Azure CLI with the `containerapp` extension, PowerShell 7 on the agent and Windows PowerShell 5.1 locally, Docker on the agent, .NET SDK 10.0.401 from `global.json`, coverlet's existing 80% gate.

**Spec:** [docs/design/2026-10-19-scheduled-job-and-deployment.md](../../design/2026-10-19-scheduled-job-and-deployment.md), committed as `bb19515` (#84). Read its Decisions, Job settings, The pipeline in outline and Testing before starting; this plan argues from it.

**Scope:** #116 (Tasks 1 and 3), #117 (Task 2), #119 (Task 4). #118 is done (by hand on 2026-10-17). After Task 2 the branch gets one fresh review and its pull request; Tasks 3 and 4 follow on a second branch of the same name.

## Global Constraints

- Branch: `feature/84-scheduled-job-and-deployment` (already created from `develop`, with the design committed).
- **Commits:** one per task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with the board task number, such as `#116`. No `Co-Authored-By` line, and no mention of Claude, Opus or any AI tool. Push, open a pull request, register the pipeline or change any Azure DevOps setting only when the user says so.
- **Board:** set the task to Doing when it starts and Done when its commit is made: `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id <116|117|119> --state <Doing|Done>`.
- **Azure:** the user runs anything that changes Azure; Claude runs only read-only `az ... show`, `list` and `-WhatIf`. The job is never created or changed by hand: only by the pipeline (#84).
- **Exact values** (design, Job settings):
  - Service connection `sc-reposcanner-dev`; environment `reposcanner-dev`; resource group `RepoAnalysis-dev`; Container Apps environment `code-reposcanner-dev`; registry `enqreposcannerdev`; image `enqueuer`, tag `$(Build.BuildId)`, never `latest`.
  - Job `job-enqueuer-dev`: Schedule, cron `0 2 * * *`; replica timeout `600`; replica retry limit `1`; parallelism `1`; completion count `1`; CPU `0.25`; memory `0.5Gi`; identity `id-scanner`, also its registry identity; secret `azure-devops-pat` as `keyvaultref:https://kv-reposcanner-dev.vault.azure.net/secrets/azure-devops-pat,identityref:<id-scanner's resource ID>`.
  - Environment variables, in this order: `AZURE_CLIENT_ID=<id-scanner's client ID>`, `Enqueuer__Queue__Name=scan-nightly`, `Enqueuer__Queue__ServiceUri=https://strreposcannerdev.queue.core.windows.net`, `Enqueuer__AzureDevOps__OrganizationUrl=https://dev.azure.com/BlacklightInternalsMo`, `Enqueuer__AzureDevOps__Pat=secretref:azure-devops-pat`. No `DOTNET_ENVIRONMENT`.
- **Secrets:** nothing secret is in the pipeline, the script or Azure DevOps. Never read the PAT, from Key Vault or anywhere else, to check anything.
- **The deploy condition**, used by Image and Deploy and nowhere else: `and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/develop'), ne(variables['Build.Reason'], 'PullRequest'))`.
- **Script conventions** (as `scripts/test-container.ps1`): comment-based help with `.SYNOPSIS`, `.DESCRIPTION`, `.PARAMETER` and `.EXAMPLE`; `$ErrorActionPreference = 'Stop'`; comments citing the Issue 6 design by decision number; runs under PowerShell 7 and Windows PowerShell 5.1, so no `??`, ternary or `&&`.
- Workspace for logs and records, git-ignored: `.superpowers/sdd/2026-10-19-scheduled-job-and-deployment/`.
- No em dash (U+2014) in any file. British English.
- `dotnet build Enqueuer.sln` has zero warnings and `dotnet test Enqueuer.sln` passes with the 80% line coverage gate at the end of each task.

## Review Focus

Inputs the spec implies but no step of the happy path exercises, most likely to bite first. Each has its step in the task named.

1. **`az containerapp job show` failing for a reason other than a missing job,** such as an expired sign-in or a missing Reader role: the script must stop with that error, not try to create a job that exists. Task 2, Step 3.
2. **A pull request, a manual run of another branch, or a documentation-only merge** must never push an image or deploy. Task 1, Step 6.
3. **Tests that assume Windows** (path separators, line endings, case-insensitive file names) failing on the Linux agent. Task 1, Step 2, before anything is pushed.
4. **A second run with no change** must take the update path and leave every setting as it was, not fail with "already exists". Task 2, Step 8.
5. **The PAT appearing in a log or in the job's definition:** the job must hold only a Key Vault reference and `secretref:`. Task 2, Step 9.

---

### Task 1: Build and Image stages (#116)

**Files:**
- Create: `azure-pipelines.yml`
- Create: `.superpowers/sdd/2026-10-19-scheduled-job-and-deployment/task1.md`, the record for the pull request (git-ignored)

**Interfaces:**
- Consumes: `Enqueuer.sln`; `global.json` (SDK 10.0.401); the coverage gate and `CoverletOutput` in `Directory.Build.props` (report at `tests/Enqueuer.Tests/TestResults/coverage/coverage.cobertura.xml`); the `Dockerfile` from Issue 5.
- Produces, for Task 2: top-level `variables` `serviceConnection: sc-reposcanner-dev`, `registry: enqreposcannerdev`, `image: enqueuer`; stage names `Build` and `Image`; the image `enqreposcannerdev.azurecr.io/enqueuer:$(Build.BuildId)` pushed by every `develop` run.

- [ ] **Step 1: Set #116 to Doing**

- [ ] **Step 2: Run the tests on Linux before anything is pushed**

Export the committed tree and run the tests in the SDK image, so no Linux `obj` folder lands in the working copy:

```powershell
$ws = '.superpowers/sdd/2026-10-19-scheduled-job-and-deployment'
git archive --format=zip -o "$ws/linux-src.zip" HEAD
Expand-Archive "$ws/linux-src.zip" "$ws/linux-src" -Force
docker run --rm -v "${PWD}/$ws/linux-src:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test Enqueuer.sln --configuration Release
```

Expected: every test passes and the gate reports at least 80% line coverage. Any test that fails only on Linux is fixed in its own commit ending `#116`, after the user has reviewed it, before Step 3.

- [ ] **Step 3: Write `azure-pipelines.yml` with the trigger, pool, variables and Build stage**

- `trigger`: `branches: include: [develop]`; `paths: exclude: [docs, '**/*.md']`. No `pr:` section: pull requests are run by the build validation policy (Task 3).
- `pool: vmImage: ubuntu-latest`.
- `variables`: the three from Interfaces.
- Stage `Build`, one job, steps in order:
  1. `UseDotNet@2` with `packageType: sdk`, `useGlobalJson: true`.
  2. `dotnet restore Enqueuer.sln`.
  3. `dotnet build Enqueuer.sln --configuration Release --no-restore`.
  4. `dotnet test Enqueuer.sln --configuration Release --no-build --logger trx --results-directory $(Agent.TempDirectory)/TestResults`.
  5. `PublishTestResults@2`: `testResultsFormat: VSTest`, `testResultsFiles: '$(Agent.TempDirectory)/TestResults/*.trx'`, `testRunTitle: Enqueuer`, `condition: succeededOrFailed()`.
  6. `PublishCodeCoverageResults@2`: `summaryFileLocation: '$(Build.SourcesDirectory)/tests/Enqueuer.Tests/TestResults/coverage/coverage.cobertura.xml'`, `condition: succeededOrFailed()`.

Comments at the top cite the design (decisions 5, 6 and 8) and say why there is no `pr:` section.

- [ ] **Step 4: Add the Image stage**

Stage `Image`, `dependsOn: Build`, the deploy condition from Global Constraints. One job, one `AzureCLI@2` step: `azureSubscription: ${{ variables.serviceConnection }}`, `scriptType: pscore`, `scriptLocation: inlineScript`. The inline script starts `$ErrorActionPreference = 'Stop'` and `$PSNativeCommandUseErrorActionPreference = $true`, so a failed command fails the step, then runs `az acr login --name $(registry)`, `docker build --tag $(registry).azurecr.io/$(image):$(Build.BuildId) .` and `docker push` of the same tag. No `latest` anywhere (decision 9).

- [ ] **Step 5: Show the user the YAML and ask to push and register**

Ask the user to approve: pushing the branch, and registering the pipeline in Lumina as `Enqueuer` from `azure-pipelines.yml` on this branch (Pipelines, New pipeline, Azure Repos Git, `Enqueuer`, Existing Azure Pipelines YAML file), without running it. Wait for the answer.

- [ ] **Step 6: Run the pipeline by hand on this branch**

The user runs it from the Pipelines page, choosing branch `feature/84-scheduled-job-and-deployment`.
Expected: Build succeeds; its summary shows the test results and a coverage figure of at least 80%; Image is **Skipped** (Review Focus 2). Record the run number, the minutes taken and both results in `task1.md`.

- [ ] **Step 7: Commit, after the user has reviewed the diff**

```powershell
git add azure-pipelines.yml
git commit -m "This commit adds the Enqueuer repository's pipeline, Build and Image stages: ... #116"
```

The message describes what was built and what Step 6 showed, in the house's one-sentence style.

---

### Task 2: Deploy job-enqueuer-dev (#117)

**Files:**
- Create: `deploy/Deploy-EnqueuerJob.ps1`
- Modify: `azure-pipelines.yml` (the Deploy stage)
- Create: `.superpowers/sdd/2026-10-19-scheduled-job-and-deployment/task2.md` (git-ignored)

**Interfaces:**
- Consumes: Task 1's variables and stage `Image`.
- Produces: `deploy/Deploy-EnqueuerJob.ps1`, `[CmdletBinding(SupportsShouldProcess)]`, parameters:
  - `-Image` (string, mandatory): the full image reference.
  - With defaults from Global Constraints: `-ResourceGroup 'RepoAnalysis-dev'`, `-Environment 'code-reposcanner-dev'`, `-JobName 'job-enqueuer-dev'`, `-Registry 'enqreposcannerdev'`, `-Identity 'id-scanner'`, `-KeyVault 'kv-reposcanner-dev'`, `-SecretName 'azure-devops-pat'`, `-StorageAccount 'strreposcannerdev'`, `-QueueName 'scan-nightly'`, `-OrganizationUrl 'https://dev.azure.com/BlacklightInternalsMo'`.
  - Exit 0 after a create or an update; a terminating error, so a failed step, on any failed `az` call.

- [ ] **Step 1: Set #117 to Doing**

- [ ] **Step 2: Write the script's help, parameters and an `Invoke-Az` helper**

`Invoke-Az` runs `az` with the given arguments plus `--only-show-errors`, returns its standard output, and throws with the command's name and its error text when `$LASTEXITCODE` is not 0. Under `-WhatIf` the `containerapp` extension step (`az extension add --name containerapp --upgrade --yes`) is skipped through `ShouldProcess`; the read-only lookups always run.

- [ ] **Step 3: Write the lookups and the exists check**

- `id-scanner`'s resource ID and client ID from one `az identity show`.
- The secret reference, built from `-KeyVault`, `-SecretName` and the identity's resource ID, exactly as Global Constraints writes it; the five environment variables, in their order.
- Exists: `az containerapp job show` returning 0 means it exists. A non-zero exit whose error text matches `ResourceNotFound|was not found|could not be found` means it is missing. **Any other failure throws** (Review Focus 1).

- [ ] **Step 4: Write the create path**

Inside `if ($PSCmdlet.ShouldProcess($JobName, 'Create'))`: `az containerapp job create` with `--trigger-type Schedule`, the cron, timeout, retry limit, parallelism, completion count, image, CPU, memory, `--mi-user-assigned`, `--registry-server <registry>.azurecr.io`, `--registry-identity` (both the identity's resource ID), `--secrets` and `--env-vars`, all from Global Constraints.

- [ ] **Step 5: Write the update path**

Inside `if ($PSCmdlet.ShouldProcess($JobName, 'Update'))`, in this order, so the new image is pulled with its identity and secret in place: `az containerapp job identity assign --user-assigned`; `az containerapp job registry set --server --identity`; `az containerapp job secret set --secrets`; then `az containerapp job update` with the image, cron, timeout, retry limit, CPU, memory and `--replace-env-vars` with the five variables. Then print a one-line summary from `az containerapp job show`: image, cron, timeout, retry limit and the identity's name.

- [ ] **Step 6: Check the script locally, read-only**

Run, as the user, signed in with `az login`:

```powershell
powershell -ExecutionPolicy Bypass -File deploy/Deploy-EnqueuerJob.ps1 -Image enqreposcannerdev.azurecr.io/enqueuer:0 -WhatIf
```

Expected: `What if: Performing the operation "Create" on target "job-enqueuer-dev".`, exit 0, and `az containerapp job list --resource-group RepoAnalysis-dev` still empty. Then run it with `-ResourceGroup does-not-exist -WhatIf`. Expected: it stops with the `az` error and does not reach the Create line (Review Focus 1).

- [ ] **Step 7: Add the Deploy stage**

Stage `Deploy`, `dependsOn: Image`, the deploy condition. One `deployment` job, `environment: reposcanner-dev`, `strategy: runOnce: deploy: steps:` `- checkout: self`, then `AzureCLI@2` with the same service connection, `scriptType: pscore`, `scriptLocation: scriptPath`, `scriptPath: deploy/Deploy-EnqueuerJob.ps1`, `arguments: -Image $(registry).azurecr.io/$(image):$(Build.BuildId)`. Commit Task 2 as in Task 1, Step 7, ending `#117`, after the user's review.

- [ ] **Step 8: The first run on `develop`, after the user merges the pull request**

Before merging, the user creates the environment `reposcanner-dev` (Pipelines, Environments, New environment, None). After the whole-branch review, the user pushes, opens the pull request and merges it. On the first run the user permits the pipeline to use `sc-reposcanner-dev` and `reposcanner-dev`.
Expected: Build, Image and Deploy succeed; the registry lists `enqueuer:<build ID>` and no `latest`; `az containerapp job show --name job-enqueuer-dev --resource-group RepoAnalysis-dev` shows every value in Global Constraints. Then the user re-runs the run's Deploy stage. Expected: the Update path, and `az containerapp job show` identical to the first run's (Review Focus 4). Record both in `task2.md`.

- [ ] **Step 9: One execution started by hand, and the secret check**

The user runs `az containerapp job start --name job-enqueuer-dev --resource-group RepoAnalysis-dev` (starting an execution does not change the job's definition).
Expected:
- `az containerapp job execution list` shows it `Succeeded`.
- `az storage message peek --queue-name scan-nightly --account-name strreposcannerdev --auth-mode key --num-messages 32` shows one message per BlacklightInternalsMo repository, each with `schemaVersion` 1.
- `ContainerAppConsoleLogs` holds the run's JSON lines, ending with its summary.
- `az containerapp job secret list` shows `azure-devops-pat` with a `keyVaultUrl` and no value, and the environment variable is `secretRef: azure-devops-pat` (Review Focus 5).
- The user, who knows the PAT, searches the run's log lines and the pipeline run's logs for it and finds nothing; Claude never sees it.
- Lumina has no variable group, secure file or secret variable: `az pipelines variable-group list` is empty and the `Enqueuer` pipeline's Variables page has none (#84).

Set #117 to Done.

---

### Task 3: Build validation policy, ADR amendment and README (#116)

On `feature/84-scheduled-job-and-deployment`, created again from `develop` after the first pull request is merged.

**Files:**
- Modify: `docs/adr/ENQ-ADR-001-own-repository-and-own-copy-of-the-scan-request.md`, `docs/adr/README.md`, `README.md`

**Interfaces:**
- Consumes: the registered pipeline `Enqueuer` and its first runs' minutes from `task1.md` and `task2.md`.

- [ ] **Step 1: The build validation policy, with the user's go-ahead**

The user adds it, or approves Claude adding it with `az repos policy build create`, on `develop` and on `main`: pipeline `Enqueuer`, required, expiring immediately when `develop` or `main` is updated, display name `Enqueuer build`.
Expected: a pull request into `develop` starts a run whose Image and Deploy stages are Skipped.

- [ ] **Step 2: Show the coverage gate fails a pull request**

A throwaway branch removes one test file so line coverage drops below 80%, opened as a pull request. Expected: the policy's run fails in Build. Record it in `task3.md`, then abandon the pull request and delete the branch.

- [ ] **Step 3: Amend ENQ-ADR-001 and the ADR index**

Add under `## Amendments`: *`<date> (Issue 6, #84):` this repository now has its own pipeline, `azure-pipelines.yml`, which builds and tests every pull request into `develop` and `main` through a build validation policy, and on every merge into `develop` pushes `enqueuer:<build ID>` to `enqreposcannerdev` and creates or updates `job-enqueuer-dev` in the dev environment. The 80% line coverage gate runs in the pipeline, not only by hand. The decision is otherwise unchanged.* The index marks ENQ-ADR-001 `Accepted, amended <date> (Issue 6)`.

- [ ] **Step 4: The README**

- A new section, **Deployment**, after Run in a container: what a merge into `develop` does (the design's Purpose, five lines); the job's settings table; how to roll back (re-run an earlier run's Deploy stage); how to start one execution by hand and peek `scan-nightly`; that the job is never changed by hand; the measured minutes per run.
- **Build and test:** pull requests are built and tested by the pipeline.
- **Known limitations:** the design's five.

Commit, after the user's review, ending `#116`. Set #116 to Done.

---

### Task 4: The first night (#119)

- [ ] **Step 1: After the first 02:00 UTC following Task 2**

Expected: `az containerapp job execution list --name job-enqueuer-dev --resource-group RepoAnalysis-dev` shows a scheduled execution started at 02:00 UTC that `Succeeded`, and `scan-nightly` gained one message per repository, peeked as in Task 2, Step 9.

- [ ] **Step 2: Record it, and leave #119 open**

Add the date and result to the README's Deployment section, committed with the next change or on its own ending `#119`, after the user's review. #119's correlation ID query needs the scanner's jobs (scanner Issue 12, #72), so #119 stays Doing with a comment saying so.
