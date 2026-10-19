# Issue 6: Scheduled job and deployment (design)

- **Status:** Approved by Muhammad, 2026-10-19, who asked to go straight to the design, so the decisions below were the designer's calls, approved as written rather than agreed point by point in a discussion, except those marked **(Muhammad)**, which are his
- **Date:** 2026-10-19
- **Work item:** Issue 6, #84 (tasks #116 to #119)
- **Branch:** `feature/84-scheduled-job-and-deployment`

Muhammad asked to build the three apps' pipelines one at a time, starting with this one. Decisions marked **(Muhammad)** are his, made while the dev environment was set up between 2026-10-16 and 2026-10-19.

## Purpose

The enqueuer has only run on the developer's machine (RepoScanner ADR-029). This Issue gives the Enqueuer repository its own pipeline, which builds, tests and deploys the enqueuer to the dev environment whenever a change is merged into `develop`, and creates the scheduled job that fills `scan-nightly` at 02:00 UTC. It is the first of the three apps' pipelines **(Muhammad)**; the scanner's (Issue 12, #72) and the dashboard's (Issue 7, #99) follow the same pattern.

What happens when a change is merged into `develop`:

1. The Enqueuer repository's pipeline starts, and only that pipeline.
2. **Build:** restore, build and test on a Microsoft-hosted Linux agent. The test run fails the pipeline if line coverage drops below 80%, the same gate as `dotnet test` locally.
3. **Image:** the Dockerfile from Issue 5 is built, tagged `enqueuer:<build ID>` and pushed to the shared registry, `enqreposcannerdev`.
4. **Deploy:** the pipeline looks for `job-enqueuer`. If it is missing, it creates it with every setting; if it exists, it points it at the new image and puts every setting back as the pipeline says.
5. The run shows the tests, the coverage and a deployment to the `reposcanner-dev` environment. The next 02:00 UTC run uses the new image.

Rolling back is running the Deploy stage of an earlier pipeline run again, which deploys that run's image tag.

## What it deploys into

Created by hand from PowerShell with the Azure CLI between 2026-10-16 and 2026-10-19 **(Muhammad)**, in the Blacklight Pay-As-You-Go subscription that Steven Wilson approved. The pipeline never creates these; it only deploys into them.

| What | Name | Used for |
|---|---|---|
| Resource group | `RepoAnalysis-dev`, UK West | Everything below |
| Container registry | `enqreposcannerdev` | The enqueuer's images; the scanner's later |
| Container Apps environment | `code-reposcanner-dev`, logging to `log-reposcanner-dev` | Where `job-enqueuer` runs |
| Storage queue | `scan-nightly` in `strreposcannerdev` | Where the enqueuer sends |
| Key Vault | `kv-reposcanner-dev`, secret `azure-devops-pat` | The PAT: Code (Read) on BlacklightInternalsMo **(Muhammad)** |
| Runtime identity | `id-scanner` | What the job runs as. Already holds AcrPull, Key Vault Secrets User and Storage Queue Data Message Sender on `scan-nightly`, assigned on 2026-10-17 |
| Pipeline identity | `id-pipeline` | What the pipeline signs in as (decision 7) |
| Service connection | `sc-reposcanner-dev` in Lumina | Workload Identity Federation as `id-pipeline`, verified on 2026-10-18 |

## Success criteria

1. **#84** The job runs at 02:00 UTC and `scan-nightly` fills with one message per repository.
2. **#84** The job definition is changed only by the pipeline.
3. **#84, #116** No password, connection secret or PAT is stored in Azure DevOps.
4. **#116** The pipeline builds and tests the enqueuer, and pushes an image tagged with the build ID to the shared registry.
5. **#117** The job has a schedule trigger `0 2 * * *` (UTC), a replica retry limit of 1 and a short replica timeout, runs as `id-scanner`, and reads the PAT as a Key Vault reference.
6. `dotnet test` runs on every pull request into `develop` and `main`, and fails below 80% line coverage (RepoScanner ADR-020, as for #72).

#119's last check, one Log Analytics query returning the enqueuer's line and the scanner's lines for one correlation ID, needs the scanner's jobs, which arrive with Issue 12. It is checked then.

## Decisions

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | Which pipeline first | This one, then the scanner's, then the dashboard's **(Muhammad)** | The smallest of the three: no database, no migrations, one job. It proves the registry, the service connection and the job deployment before the scanner adds Migrate |
| 2 | Nightly messages before the scanner is deployed | Accepted. `scan-nightly` holds each night's messages until `job-scanner-nightly` exists. A message lives 7 days, the queue's default | When the scanner arrives it scans each repository once and drops the older requests for it as already done (#76). Messages older than 7 days expire unread |
| 3 | Who creates the shared resources | Done by hand, once, with the Azure CLI **(Muhammad)**. The pipeline deploys only the image and `job-enqueuer` | #84 says nothing is applied to Azure by hand. What changes from one release to the next, the image and the job, is changed only by the pipeline; what is created once is not |
| 4 | #118's role | Assigned by hand on 2026-10-17, with the scanner's roles. The pipeline assigns no roles | #118 says the pipeline grants it. `id-pipeline` cannot grant roles, and should not be able to |
| 5 | Agent | Microsoft-hosted `ubuntu-latest` for every stage | The image is built on Linux, and the tests then run on the same OS as the container. The organisation's free tier is one hosted parallel job and 1,800 minutes a month. The tests have only run on Windows so far: any that assume Windows are fixed when the first run finds them |
| 6 | When it runs | On every merge into `develop`, except a change only to `docs/**` or `*.md`. On every pull request into `develop` or `main`, through a build validation policy, the Build stage only. `main` deploys nothing | Documentation needs no deployment. The policy is the one RepoScanner ADR-020 waits for. There is no production environment yet |
| 7 | How the pipeline signs in | `sc-reposcanner-dev`, as `id-pipeline`, a user-assigned managed identity with one role per thing it does: Reader and Container Apps Jobs Contributor on `RepoAnalysis-dev`, AcrPush on the registry, Managed Identity Operator on `id-scanner` (set up 2026-10-18) | Workload Identity Federation, so Azure DevOps holds no secret (RepoScanner ADR-007). None of the roles is a privileged administrator role. The automatic connection tried first had Contributor on the whole resource group and was deleted |
| 8 | Build | `dotnet restore`, `dotnet build --configuration Release`, `dotnet test --configuration Release`, then the test results and coverage report published to the run | The coverage gate is already in `Directory.Build.props`, so CI runs the same command as the developer |
| 9 | Image | `az acr login`, `docker build` with the repository's Dockerfile, `docker push enqreposcannerdev.azurecr.io/enqueuer:$(Build.BuildId)`, in an Azure CLI task signed in through `sc-reposcanner-dev`. No `latest` tag | RepoScanner ADR-005. One service connection does everything: the Docker task would need a second, Docker-registry connection |
| 10 | Deploy | `deploy/Deploy-EnqueuerJob.ps1`, run by an Azure CLI task in PowerShell: `az containerapp job show`; if the job is missing, `create` with every setting; if it exists, `update` the image, environment variables, resources, timeout, retries and schedule, and set its registry, identity and Key Vault secret reference again | Muhammad's call that the pipeline creates the job if it is missing (2026-10-18). CLI commands in PowerShell, as the environment was built, rather than a Container Apps YAML file. Every setting lives in the script, so running it twice changes nothing but the image tag |
| 11 | Job settings | As in Job settings below | #84, #117, RepoScanner ADR-001 and ADR-014 |
| 12 | Names and settings | In the pipeline's `variables:` block and the script, in git. No variable groups and no secret variables | Every change is reviewed in a pull request, and nothing secret is needed: the PAT stays in Key Vault |
| 13 | Deployment history | The Deploy stage is a deployment job to an Azure DevOps environment, `reposcanner-dev`, with no approval check | The environment lists what was deployed when, by which run. The scanner's and the dashboard's pipelines will deploy to it too |
| 14 | When the message format changes | The scanner's pipeline runs first, then this one, by the order of merging (agreed 2026-10-02) | The scanner must read the new format before the enqueuer sends it |
| 15 | `scripts/test-container.ps1` | Not run in the pipeline | It needs Azurite on the machine. The first deployment and the manual run in Testing check the image in Azure instead |
| 16 | Minutes | Estimated: 8 per merge, 4 per pull request. Measured on the first runs and recorded in the README | Within the free 1,800 a month, with the other two pipelines |
| 17 | ADRs | A dated amendment to ENQ-ADR-001, which says it will get one "if the pipelines come back": this repository now has its own pipeline and a build validation policy. RepoScanner's ADR-030, replacing ADR-029, comes with the scanner's pipeline | Each repository records its own change. ADR-029 belongs to RepoScanner and is replaced there |

## Job settings

| Setting | `job-enqueuer` |
|---|---|
| Image | `enqreposcannerdev.azurecr.io/enqueuer:<build ID>`, pulled as `id-scanner` |
| Trigger | Schedule, `0 2 * * *`, UTC: 02:00 in winter, 03:00 in British Summer Time |
| Replica timeout | 600 seconds: listing retries for at most 2 minutes, and a run normally takes seconds |
| Retries | 1: a transient failure is retried once, and the scanner drops the duplicates a retry sends (#76) |
| CPU and memory | 0.25 vCPU, 0.5 GiB |
| Identity | `id-scanner` (RepoScanner ADR-014) |
| Secret | `azure-devops-pat`, a Key Vault reference naming the secret without a version, read as `id-scanner`, so a renewed PAT is picked up without a deployment |

| Environment variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | `id-scanner`'s client ID, so `DefaultAzureCredential` uses it |
| `Enqueuer__Queue__Name` | `scan-nightly` |
| `Enqueuer__Queue__ServiceUri` | `https://strreposcannerdev.queue.core.windows.net` |
| `Enqueuer__AzureDevOps__OrganizationUrl` | `https://dev.azure.com/BlacklightInternalsMo` **(Muhammad)** |
| `Enqueuer__AzureDevOps__Pat` | `secretref:azure-devops-pat` |

Every other setting keeps its Production default, and `DOTNET_ENVIRONMENT` is not set, as in the container from Issue 5.

## The pipeline, in outline

A sketch for the discussion, not the file:

```yaml
trigger:
  branches: { include: [develop] }
  paths: { exclude: [docs/*, '*.md'] }

pool: { vmImage: ubuntu-latest }

variables:
  serviceConnection: sc-reposcanner-dev
  resourceGroup: RepoAnalysis-dev
  registry: enqreposcannerdev
  image: enqueuer

stages:
- stage: Build    # restore, build, test with the coverage gate, publish results and coverage
- stage: Image    # only for a merge into develop: az acr login, docker build, docker push enqueuer:$(Build.BuildId)
- stage: Deploy   # only for a merge into develop: deployment job to environment reposcanner-dev,
                  # running deploy/Deploy-EnqueuerJob.ps1 -Image enqreposcannerdev.azurecr.io/enqueuer:$(Build.BuildId)
```

## Project layout

```
azure-pipelines.yml                       the pipeline                                    #116, #117
deploy/Deploy-EnqueuerJob.ps1             create or update job-enqueuer                   #117
docs/adr/ENQ-ADR-001-...md                dated amendment                                 #116
docs/adr/README.md                        ENQ-ADR-001 marked amended                      #116
docs/design/2026-10-19-scheduled-job-and-deployment.md   this document                    #84
README.md                                 Deployment; Known limitations                   #116, #117, #119
```

No application code changes.

## Left to the builder

- **The exact task versions** (Azure CLI, publishing test results and coverage).
- **The `containerapp` CLI extension:** installed or upgraded at the start of the deploy script, so the agent's copy does not matter.
- **Caching NuGet packages** between runs, if it saves minutes.

## Testing

No application code changes, so `dotnet test` is unchanged. The pipeline is checked by running it:

| Task | Check |
|---|---|
| #116 | The pull request's run passes Build on Linux; any test that assumes Windows is fixed in its own commit. A pull request that drops coverage below 80% fails, shown once and abandoned |
| #116 | The first run on `develop` pushes `enqueuer:<build ID>`, listed in the registry, and no `latest` |
| #117 | The first run creates `job-enqueuer` with the settings above, read back with `az containerapp job show`. A second run with no change leaves everything but the image tag the same |
| #117 | One execution started by hand (`az containerapp job start`) exits 0, and `scan-nightly` holds one message per BlacklightInternalsMo repository, peeked with the storage account's key. Its JSON lines are in `ContainerAppConsoleLogs`, and the PAT is in none of them |
| #119 | The first 02:00 UTC run does the same. The correlation ID query waits for the scanner (Issue 12) |
| #84 | No secret variable, variable group or secure file exists in Lumina |

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (RepoScanner ADR-020). The board change needs Muhammad's go-ahead.

1. **#84** This design document, approved.
2. **The board:** `out-of-scope` removed from #84 and its four tasks; #118 noted as done by hand on 2026-10-17.
3. **#116** `azure-pipelines.yml` with the Build stage, registered in Lumina as `Enqueuer`, run on this branch's pull request; Linux test failures fixed.
4. **#116** The Image stage. **#117** `deploy/Deploy-EnqueuerJob.ps1` and the Deploy stage. Merged into `develop`; permission to use `sc-reposcanner-dev` and the `reposcanner-dev` environment given on the first run; the job created; the manual execution checked.
5. **#116** The build validation policy on `develop` and `main`, and the ENQ-ADR-001 amendment.
6. **#116, #117** The README's Deployment section, with the measured minutes.
7. **#119** The first night checked, as far as it can be without the scanner.

## Not in Issue 6

| Item | Where it is |
|---|---|
| The scanner's pipeline, its jobs and ADR-030 | Scanner Issue 12 (#72), next |
| The dashboard's pipeline | Dashboard Issue 7 (#99) |
| The correlation ID query across the enqueuer and the scanner | #119, once Issue 12 is deployed |
| Alerts on a rejected PAT | Scanner Issue 12: the alert covers the enqueuer too, since both log the same first words |
| A production environment | Not on the board |

## Known limitations

- **One hosted parallel job:** runs, including the other two pipelines' later, wait for each other.
- **`ubuntu-latest` moves:** a new image version can change a tool under the pipeline.
- **Shared resources are not in the pipeline:** a change to one is made by hand and recorded in the README.
- **Until the scanner is deployed,** each night's messages wait on `scan-nightly` and expire after 7 days.
- **The PAT** expires in Azure DevOps and is renewed by hand in Key Vault; the job picks up the new version within about 30 minutes, without a deployment.
