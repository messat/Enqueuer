# Issue 2: Repository listing from Azure DevOps (design)

- **Status:** Draft, for review
- **Date:** 2026-10-08
- **Work item:** Issue 2, #80 (tasks #106 to #108)
- **Branch:** `feature/80-repository-listing`

## Purpose

The enqueuer's only input is the organisation's list of repositories. Today `EnqueueOrchestrator` logs that there is nothing to queue and exits. This Issue makes it ask Azure DevOps for every repository in every project, with one call, and decide which ones a scan request should go to. Sending the requests is Issue 3, so after this Issue the enqueuer lists and chooses, logs what it chose, and still sends nothing.

It is the first time the enqueuer talks to anything outside the machine, so it is also the first time it needs the PAT, and the first time a setting can be right but refused.

What happens in one run:

1. Settings are checked at start-up, now including `Enqueuer:AzureDevOps:OrganizationUrl` and `Enqueuer:AzureDevOps:Pat`.
2. `GET https://dev.azure.com/BlacklightInternalsMo/_apis/git/repositories?api-version=7.1` is sent, with the PAT in the `Authorization` header.
3. Each repository is kept, or skipped with its reason in the log: disabled, in maintenance, or empty.
4. `refs/heads/` is taken off each kept repository's default branch.
5. One line gives the totals, and the enqueuer exits 0.
6. If Azure DevOps refuses the PAT, or cannot list the repositories, one line says why and the enqueuer exits 1.

## Success criteria

From Issue 2 (#80):

1. Every repository in every project is listed with one call.
2. Empty, disabled and in-maintenance repositories are skipped with a reason in the log.
3. `defaultBranch` has no `refs/heads/` prefix.
4. The PAT never appears in a log or an exception message.

## Decisions

Decisions 3, 9, 14 and 18, and checking the skips by tests only (Testing), were agreed in the design discussion on 2026-10-08. The rest are proposed here, for review. Decisions 6, 7 and 11 rest on checks against BlacklightInternalsMo on 2026-10-08, described under each.

| # | Question | Decision | Reason |
|---|---|---|---|
| 1 | The call | One `GET {OrganizationUrl}/_apis/git/repositories?api-version=7.1`, with no project in the path | With no project it returns every repository in every project, so there is no loop over projects (#80). The 7.1 reference has no paging parameters, so one response holds them all. On 2026-10-08 it listed BlacklightInternalsMo's 5 repositories, all in `Kudos Wall` |
| 2 | Which organisation | `Enqueuer:AzureDevOps:OrganizationUrl`. In `Development`, `https://dev.azure.com/BlacklightInternalsMo` | The organisation the scanner clones from (#106). A PAT belongs to one organisation, and the scan request has no organisation field (RepoScanner Issue 8, decision 5). Not a secret, so it goes in `appsettings.Development.json` |
| 3 | Where the PAT is kept locally | The user environment variable `Enqueuer__AzureDevOps__Pat`, holding the same token as the scanner's `Scanner__AzureDevOps__Pat` | Every enqueuer setting starts `Enqueuer:`, as `Enqueuer:Queue` does. Reading `Scanner__AzureDevOps__Pat` would tie the enqueuer to the scanner's setting names, which ENQ-ADR-001 keeps apart. A user environment variable and not dotnet user-secrets, for the scanner's reasons: no project set-up, and docker-compose can pass it to the container (Issue 5). The cost is one token in two variables, so renewing it means updating both |
| 4 | Where the PAT is kept in Azure | The Key Vault secret the scanner jobs use, given to the enqueuer job as `Enqueuer__AzureDevOps__Pat` | One PAT with Code (Read) only, shared by the enqueuer and both scanner jobs (RepoScanner ADR-007; RepoScanner Issue 8, decision 4). Listing repositories needs `vso.code`, as cloning does. Set up in Issue 6 |
| 5 | How the PAT is sent | `Authorization: Basic <Base64 of ":" + PAT>`, set on the request. Never in the URL, a log line or an exception message. `AzureDevOpsOptions.ToString` prints `Pat = ***` | Azure DevOps' documented way to send a PAT. Criterion 4. A C# record's `ToString` prints every property |
| 6 | Redirects | Every request sends `X-TFS-FedAuthRedirect: Suppress`, and the client does not follow redirects | Checked on 2026-10-08 with a made-up token. Without the header, Azure DevOps answers `302` to a sign-in page. `HttpClient` follows redirects by default and lands on that page with `203 Non-Authoritative Information`, a success code, carrying HTML. With the header, it answers `401` with JSON (`TF400813: ... not authorized to access this resource`). Not following redirects also means the request never leaves the organisation's address |
| 7 | A refused PAT | A `401` or `403`, or a `203` or any redirect, throws `AzureDevOpsAuthenticationException`. `Program` logs `Azure DevOps refused the token, enqueuer stopped: {Problem}` as one line and exits 1. Nothing is queued | The scanner logs the same first words, and its Issue 10 alert watches them, so one alert catches both jobs when the shared PAT expires (#108). A `203` or redirect means the sign-in page from decision 6, so it is treated as refused should Azure DevOps ever stop honouring the header. Only a made-up token was tried: an expired or revoked token, and one without Code (Read), are expected to give the same `401` or a `403`, but were not checked |
| 8 | Any other failure | A network error, a timeout, any other status (such as `404` for an organisation that does not exist, checked on 2026-10-08), or a body that is not a repository list, throws `AzureDevOpsUnavailableException`. `Program` logs `Azure DevOps could not list the repositories, enqueuer stopped: {Problem}` as one line and exits 1 | Exit 1 means Azure DevOps could not be reached (Issue 1). The message names the address and the status, never the response body, as the scanner's nuget.org client does (RepoScanner Issue 5): a body is not ours to print |
| 9 | Retries and timeouts | The standard resilience handler, as the scanner's nuget.org client: 3 retries with exponential backoff and jitter for 5xx, 408, 429, network errors and timeouts; 30 seconds per attempt; 2 minutes in total | One brief outage at 02:00 would otherwise cost the whole night's scans. The same settings as RepoScanner Issue 5, decision 11. `401` and `403` are not retried, so a refused PAT fails in seconds |
| 10 | Which repositories are skipped | Disabled (`isDisabled`), then in maintenance (`isInMaintenance`), then empty (no `defaultBranch`: missing, null or blank). The first that applies is the reason. One log line each | #80. Azure DevOps refuses to clone a disabled repository. An empty repository has no default branch, and the scanner would fail its clone, retry it and poison the message every night (agreed 2026-10-04, scanner Issue 11) |
| 11 | Why not `size` for "empty" | `size` is not read | On 2026-10-08 `victim-repo` listed `size: 0`, though the scanner cloned commit `b653342` from it on 2026-10-01. A missing `defaultBranch` is the only sign of an empty repository |
| 12 | Forks | Queued like any other repository. `isFork` is not read | So a secret committed only in a fork is still found, accepting that a fork repeats its parent's findings under its own name (agreed 2026-10-01). `isFork` was missing from every repository in the 2026-10-08 listing anyway |
| 13 | The default branch | `refs/heads/` is removed from the start, once, matching case exactly | The scanner refuses a branch starting `refs/` (RepoScanner Issue 7 design, Message format). The default branch is sent, not its latest commit: the scanner records the commit it cloned (#107) |
| 14 | No repositories listed | A warning, `Azure DevOps listed no repositories in {OrganizationUrl}. Check that the PAT's owner can see its projects`, then exit 0 | A PAT whose owner has lost access to every project lists nothing rather than being refused. It is not an error the job can fix, but it must not pass silently |
| 15 | Logging | One Information line per skipped repository, then one line of totals. Never the PAT. `System.Net.Http.HttpClient` and `Polly` log from Warning upwards | The HTTP client logs every request at Information, and Polly every attempt, as RepoScanner found. Issue 4 adds the run summary and correlation IDs |
| 16 | Tests | The HTTP client's primary handler is replaced by a fake, so `dotnet test` needs no network and no PAT | #106 |
| 17 | New packages | `Microsoft.Extensions.Http.Resilience` 10.10.0, the version in RepoScanner's `Directory.Packages.props`. It brings `Microsoft.Extensions.Http` with it | Decision 9. `System.Text.Json` and `Convert.ToBase64String` are part of .NET |
| 18 | New ADRs | None | RepoScanner ADR-007 and ADR-014, and its Issue 8 amendment to ADR-017, already decide the PAT, its scope and where it is kept. This design applies them under the enqueuer's own setting names |
| 19 | Where this document lives | `docs/design/`, a new folder | As RepoScanner keeps its Issue designs |

## Project layout

```
src/Enqueuer/
  Configuration/   AzureDevOpsOptions                                     #106
  AzureDevOps/     AzureDevOpsRepository, AzureDevOpsRepositoryClient     #106
                   AzureDevOpsAuthenticationException,
                   AzureDevOpsUnavailableException                        #108
  Repositories/    RepositorySelector, RepositorySelection,
                   RepositoryToScan, SkippedRepository, SkipReason        #107
  EnqueueOrchestrator.cs    lists, selects and logs                       #106, #107
  ExitCodes.cs              1 also means the PAT was refused              #108
  Program.cs                registrations; log level overrides;
                            the two one-line failures                     #106, #108
  appsettings.Development.json   Enqueuer:AzureDevOps:OrganizationUrl     #106
  Enqueuer.csproj                + Microsoft.Extensions.Http.Resilience   #106

Directory.Packages.props    + Microsoft.Extensions.Http.Resilience 10.10.0   #106

tests/Enqueuer.Tests/
  Configuration/   AzureDevOpsOptionsTests                                #106
  AzureDevOps/     AzureDevOpsRepositoryClientTests,
                   FakeAzureDevOps (test helper)                          #106, #108
  Repositories/    RepositorySelectorTests                                #107
  EnqueueOrchestratorTests.cs, ProgramTests.cs   updated                  #106 to #108

README.md                                                                 #108
```

Classes are `sealed` or `static` and follow RepoScanner ADR-021, as in Issue 1. `AzureDevOpsRepositoryClient` and `EnqueueOrchestrator` are singletons: the job runs once.

## Settings (#106)

```csharp
namespace Enqueuer.Configuration
{
    // Where the repositories are listed from, bound from the "Enqueuer:AzureDevOps" section and validated when the
    // host starts (RepoScanner ADR-022)
    public sealed record AzureDevOpsOptions : IValidatableObject
    {
        public const string SectionName = "Enqueuer:AzureDevOps";

        // https://dev.azure.com/<organisation>, the one the scanner clones from
        public string OrganizationUrl { get; init; } = string.Empty;

        // The scanner's personal access token, Code (Read) only. ToString shows it as ***.
        public string Pat { get; init; } = string.Empty;
    }
}
```

| Setting | Rule | When it is wrong |
|---|---|---|
| `OrganizationUrl` | Required. An absolute `https` URL with no user name or password, query string or fragment in it. A trailing `/` is ignored | `Enqueuer:AzureDevOps:OrganizationUrl must be an https URL such as https://dev.azure.com/<organisation>, with no user name, query string or fragment in it` (query string and fragment added after the branch review) |
| `Pat` | Required, with no space, line break or quote mark at either end (added after the branch review: one copied with the token would read as an expired PAT) | `Enqueuer:AzureDevOps:Pat is required. Locally, set the Enqueuer__AzureDevOps__Pat user environment variable to the scanner's personal access token, which has Code (Read).` A failure message never includes the value |

The scanner also accepts `file` URLs, for its bare test repositories. The enqueuer's tests fake the HTTP handler instead, so it accepts `https` only.

A new machine missing both the PAT and the queue name reports both on one line and exits 2, as Issue 1 does for every setting.

## The call (#106)

```csharp
namespace Enqueuer.AzureDevOps
{
    // One repository as Azure DevOps lists it: only the fields the enqueuer reads
    public sealed record AzureDevOpsRepository(Guid Id, string Name, string ProjectName, string? DefaultBranch, bool IsDisabled, bool IsInMaintenance);

    // Lists every repository in the organisation with one call (Issue 2 design, decision 1)
    public sealed class AzureDevOpsRepositoryClient
    {
        public const string HttpClientName = "azure-devops";

        // The named HTTP client, with its headers, redirects off and its retries, and the client itself. Program
        // and the tests both use this, so the tests run through the real retry handler.
        public static void Register(IServiceCollection services);

        public AzureDevOpsRepositoryClient(IHttpClientFactory httpClientFactory, IOptions<AzureDevOpsOptions> options);

        // In the order Azure DevOps gives them
        public Task<IReadOnlyList<AzureDevOpsRepository>> ListAsync(CancellationToken cancellationToken);
    }
}
```

`Register`:

- Adds the named client with `User-Agent: Enqueuer` and `X-TFS-FedAuthRedirect: Suppress` on every request, and `HttpClient.Timeout` infinite, so the resilience handler's 2 minutes is the limit.
- Sets its primary handler to a `SocketsHttpHandler` with `AllowAutoRedirect = false` (decision 6).
- Adds the standard resilience handler with a 30-second attempt timeout, a 60-second sampling duration (the handler insists on at least twice the attempt timeout) and a 2-minute total, as RepoScanner's nuget.org client does.

`ListAsync`:

1. Builds `{OrganizationUrl without a trailing /}/_apis/git/repositories?api-version=7.1`.
2. Sends a `GET` with `Authorization: Basic <Base64 of ":" + Pat>` on the request.
3. On `200`, reads the body as JSON (`JsonSerializerDefaults.Web`) into private response types: `value`, and for each repository `id`, `name`, `project.name`, `defaultBranch`, `isDisabled` and `isInMaintenance`. Everything else, including `count`, is ignored.
4. Returns one `AzureDevOpsRepository` each.

`EnqueueOrchestrator` gets `AzureDevOpsRepositoryClient` and `IOptions<AzureDevOpsOptions>` through its constructor, and calls `ListAsync` with the token it is given, so stopping the job stops the call.

In #106 any status other than `200` throws `HttpRequestException` and the enqueuer exits 1 through Issue 1's "Enqueuer failed" path. #108 replaces that with the two failures below.

## When the call fails (#108)

```csharp
namespace Enqueuer.AzureDevOps
{
    // Azure DevOps refused the PAT: it is wrong, expired or revoked, or lacks Code (Read). Nothing can be listed,
    // so Program exits 1 (Issue 2 design, decision 7).
    public sealed class AzureDevOpsAuthenticationException : Exception
    {
        public AzureDevOpsAuthenticationException(string message);
    }

    // Azure DevOps could not list the repositories for any other reason (decision 8)
    public sealed class AzureDevOpsUnavailableException : Exception
    {
        public AzureDevOpsUnavailableException(string message, Exception? innerException);
    }
}
```

| What came back | Exception | Message |
|---|---|---|
| `401` or `403` | `AzureDevOpsAuthenticationException` | `Azure DevOps refused the token in Enqueuer:AzureDevOps:Pat for https://dev.azure.com/BlacklightInternalsMo. Check that it has not expired or been revoked, and that it has Code (Read).` |
| `203`, or any `3xx` | `AzureDevOpsAuthenticationException` | The same |
| Any other status, after the retries for those that are retried | `AzureDevOpsUnavailableException` | `Repositories could not be listed from https://dev.azure.com/BlacklightInternalsMo: Azure DevOps answered 404 (Not Found).` |
| A network error, or a timeout after the retries | `AzureDevOpsUnavailableException` | `Repositories could not be listed from <OrganizationUrl>: ` and the `HttpRequestException` or timeout message, which never holds a header. The original is the inner exception |
| A `200` whose body is not JSON, has no `value` list, or has a repository with no `id`, `name` or `project.name` | `AzureDevOpsUnavailableException` | `Azure DevOps answered https://dev.azure.com/BlacklightInternalsMo with something other than a repository list.` The parser's message, which can quote the body, stays on the inner exception |

If the token passed to `ListAsync` is cancelled, `OperationCanceledException` goes through unchanged, as Issue 1 handles it today.

`Program.RunAsync` gets two new `catch` blocks, before the one for any other exception:

```csharp
catch (AzureDevOpsAuthenticationException exception)
{
    // The scanner logs the same first words, and one alert watches for them (#108)
    Log.Fatal("Azure DevOps refused the token, enqueuer stopped: {Problem}", exception.Message);
    return ExitCodes.Failure;
}
catch (AzureDevOpsUnavailableException exception)
{
    Log.Fatal("Azure DevOps could not list the repositories, enqueuer stopped: {Problem}", exception.Message);
    return ExitCodes.Failure;
}
```

Both are one line with no stack trace: neither is fixed by changing code.

## Choosing what to queue (#107)

```csharp
namespace Enqueuer.Repositories
{
    public enum SkipReason
    {
        Disabled,
        InMaintenance,
        Empty
    }

    // A repository a scan request will go to: the four fields the request needs from it (Issue 3)
    public sealed record RepositoryToScan(Guid Id, string ProjectName, string Name, string DefaultBranch);

    public sealed record SkippedRepository(string ProjectName, string Name, SkipReason Reason);

    public sealed record RepositorySelection(IReadOnlyList<RepositoryToScan> ToScan, IReadOnlyList<SkippedRepository> Skipped);

    // Which listed repositories to scan, and why the others are skipped (decisions 10 to 13)
    public static class RepositorySelector
    {
        public static RepositorySelection Select(IReadOnlyList<AzureDevOpsRepository> repositories);
    }
}
```

| A listed repository | Result |
|---|---|
| `isDisabled` | Skipped, `Disabled` |
| `isInMaintenance` | Skipped, `InMaintenance` |
| No `defaultBranch`, or a blank one | Skipped, `Empty` |
| Any other, forks included | Scanned, with `refs/heads/` taken off `refs/heads/develop` to give `develop` |

The checks run in the order of the table, so a disabled empty repository is skipped once, as `Disabled`. Both lists keep the order Azure DevOps gave. `RepositorySelector` is a pure function: it logs nothing, and the orchestrator logs what it returns.

## The orchestrator (#106, #107)

`EnqueueOrchestrator.RunAsync` becomes:

1. Logs `Enqueuer starting in {EnvironmentName} for queue {QueueName}`, as today.
2. Lists the repositories (#106).
3. Selects them (#107), and logs each skipped one: `Skipped {ProjectName}/{RepositoryName}: {SkipReason}`.
4. Logs `Listed {ListedCount} repositories in {OrganizationUrl}: {ToScanCount} to queue, {SkippedCount} skipped`.
5. Logs the warning from decision 14 if nothing was listed.
6. Logs `Nothing sent: sending scan requests arrives in Issue 3`, in place of `No repositories to queue, exiting`. Issue 3 removes this line.

`{SkipReason}` is the enum's name, so Issue 4 can count skips by reason from the log.

## Settings

| Setting | Required | `Development` | Notes |
|---|---|---|---|
| `Enqueuer:AzureDevOps:OrganizationUrl` | Yes | `https://dev.azure.com/BlacklightInternalsMo` | An absolute `https` URL with no user name, query string or fragment |
| `Enqueuer:AzureDevOps:Pat` | Yes | | Locally the user environment variable `Enqueuer__AzureDevOps__Pat`; in Azure a Key Vault secret reference (Issue 6). Never in an appsettings file |

`AzureDevOpsOptions` is a new group (RepoScanner ADR-022), bound, validated and checked at start-up like `QueueOptions`.

## Local setup

The enqueuer uses the scanner's PAT. If `Scanner__AzureDevOps__Pat` is already set, copy it, so the token never shows on screen or in the terminal's history:

```powershell
[Environment]::SetEnvironmentVariable("Enqueuer__AzureDevOps__Pat", [Environment]::GetEnvironmentVariable("Scanner__AzureDevOps__Pat", "User"), "User")
```

Otherwise make one as RepoScanner's README says (organisation **BlacklightInternalsMo**, scope **Code: Read**, a short expiry) and save it with `setx Enqueuer__AzureDevOps__Pat "<the token>"`. Then open a new terminal, or restart Visual Studio or VS Code, and run `dotnet run --project src/Enqueuer`.

## README

- **Run:** the enqueuer now needs the PAT and a network connection; the local setup above.
- **Configuration:** the two `Enqueuer:AzureDevOps` settings.
- **What it logs:** the skipped lines and the totals line, with an example from BlacklightInternalsMo.
- **Exit codes:** 1 now also means Azure DevOps refused the PAT, with what the line looks like.
- **Known limitations:** those below.

## Testing

Each task is written test first. `dotnet test` still needs no network, no Azurite and no PAT.

`FakeAzureDevOps`, a test helper, is an `HttpMessageHandler` that answers each request with a status and body the test sets, and keeps every request it was sent. Tests put it in place of the primary handler through `RunAsync`'s `configureServices`, so the real headers and resilience handler still run. The retry delay is set to zero so retried tests do not wait. Fake PATs start with `your-`, which RepoScanner's self-scan treats as a placeholder.

`Main_WithValidQueueSettings_ReturnsZero` and `Main_WithValidQueueSettings_RunsTheOrchestrator` call `Program.Main`, which would now reach the real Azure DevOps. They move to `RunAsync` with `FakeAzureDevOps`, so no test reaches the network.

| Task | Tests |
|---|---|
| #106 | `AzureDevOpsOptions`: each rule; a missing PAT naming `Enqueuer__AzureDevOps__Pat`; no failure message holding the value; `ToString` hiding it. `AzureDevOpsRepositoryClient` against `FakeAzureDevOps`: one request, to exactly `<OrganizationUrl>/_apis/git/repositories?api-version=7.1`, with a trailing `/` ignored; `Authorization` is `Basic` with the Base64 of `":" + PAT`; `X-TFS-FedAuthRedirect: Suppress` is sent; the PAT and its Base64 are not in the URL; the 7.1 reference's sample response, with `isDisabled` and `isInMaintenance` added, gives the expected repositories in order. The registered primary handler does not follow redirects. `Program`: a missing PAT exits 2 with one line |
| #107 | `RepositorySelector`: each row of the table; a repository both disabled and empty skipped once, as `Disabled`; a fork kept; `refs/heads/feature/x` gives `feature/x`; a branch without the prefix unchanged; order kept. `EnqueueOrchestrator`: a line per skipped repository with its reason; the totals line; the warning when nothing is listed |
| #108 | `AzureDevOpsRepositoryClient`: `401`, `403`, `203` and `302` each throw `AzureDevOpsAuthenticationException` with the message above; `404`, unreadable JSON, a missing `value` and a repository with no `id` each throw `AzureDevOpsUnavailableException`; a `503` then a `200` retried to success through the real resilience handler; a `401` sent once, not retried; no exception message holding the PAT or its Base64. `Program`: a refused PAT exits 1 with one line starting `Azure DevOps refused the token`, and no stack trace; a `404` exits 1 with one line; criterion 4, nothing the program printed in any test holds the PAT or its Base64 |

- The 80% line coverage gate on the `Enqueuer` project covers the new classes.
- **Manual checks** against BlacklightInternalsMo, after #108:
  1. With the real PAT: `Listed 5 repositories in https://dev.azure.com/BlacklightInternalsMo: 5 to queue, 0 skipped` (as on 2026-10-08), and exit 0.
  2. A wrong PAT for one terminal (`$env:Enqueuer__AzureDevOps__Pat = "your-wrong-token"`): one line starting `Azure DevOps refused the token`, within seconds, and `$LASTEXITCODE` 1.
  3. No PAT for one terminal (`$env:Enqueuer__AzureDevOps__Pat = ""`): one line naming `Enqueuer__AzureDevOps__Pat`, and exit 2.
  4. `--Enqueuer:AzureDevOps:OrganizationUrl https://dev.azure.com/no-such-org-zz9q1`: one line starting `Azure DevOps could not list the repositories`, and exit 1.

BlacklightInternalsMo had no empty, disabled or in-maintenance repository on 2026-10-08, so those skips are checked by tests only.

## Order of work

One task at a time, stopping after each for review, then a commit whose message starts "This commit" and ends in its task number (ENQ-ADR-001):

1. **#80** This design document.
2. **#106** `AzureDevOpsOptions`, `AzureDevOpsRepositoryClient` and its registration, the package, the log level overrides, `FakeAzureDevOps`, and the orchestrator listing and logging the count. `ProgramTests` moved off the network.
3. **#107** `RepositorySelector`, and the orchestrator's skipped lines, totals line and empty-listing warning.
4. **#108** The two exceptions, `Program`'s two one-line failures, `ExitCodes`, the README, and the manual checks.

## Not in Issue 2

| Item | Where it is on the board |
|---|---|
| Sending one scan request per repository | Issue 3 |
| The run summary line and correlation IDs | Issue 4 |
| docker-compose passing `Enqueuer__AzureDevOps__Pat` to the container | Issue 5 |
| The Key Vault secret reference and `OrganizationUrl` on the enqueuer job | Issue 6 |
| An alert on a refused PAT | RepoScanner Issue 10's alert watches the `Azure DevOps refused the token` line. Issue 6 makes sure it sees the enqueuer's logs too |
| A managed identity in place of the PAT | Not on the board: RepoScanner ADR-007's production upgrade |

## Known limitations

- The PAT acts as the person who created it. A repository they cannot see is not listed, and nothing says so unless no repository at all is listed (decision 14).
- Hidden repositories are left out, as the API does unless `includeHidden=true` is asked for.
- A refused PAT is recognised by `X-TFS-FedAuthRedirect: Suppress`, whose effect was checked by hand on 2026-10-08, not taken from the REST reference. If it stopped, a `203` or a redirect is still treated as refused (decision 7).
- Locally the one PAT is in two user environment variables, so renewing it means updating both.
- The listing is a snapshot. A repository created after it is queued the next night.
- One organisation per enqueuer deployment, as for the scanner.
