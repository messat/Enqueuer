# Issue 3: Scan request sending, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The enqueuer sends one scan request per chosen repository to `scan-nightly`, in the format the scanner reads. Task 1 (#109) is the message and its contract tests; Task 2 (#110) sends it, with a missing queue exiting 2 and a failed send exiting 1, each as one line; Task 3 (#111) proves it end to end, the enqueuer filling `scan-nightly` in Azurite and RepoScanner's scanner scanning each request.

**Architecture:** Task 1: two small types in a new `Enqueuer.Queue` namespace, a record with a static builder and a static writer using `Utf8JsonWriter`, and a test helper, `ScanRequestContract`, holding the scanner's rules. Task 2: `QueueClients` builds the Azure `QueueClient` from Issue 1's `QueueOptions`; `ScanRequestSender` sends one request and turns Azure's failures into two exceptions `Program` logs as one line each; `EnqueueOrchestrator` sends the chosen repositories one at a time and stops at the first failure. Tests use a hand-written `FakeQueueClient`, so `dotnet test` never reaches Azurite. Task 3 adds no code: it runs both apps by hand against Azurite and SQL Server Express, records what each step printed for the pull request, and documents it in the README.

**Tech Stack:** .NET 10, `System.Text.Json`, `Azure.Storage.Queues` 12.27.1, `Azure.Identity` 1.21.0, NUnit 4, coverlet.msbuild; Azurite 3.37.0, the `az` CLI 2.87.0, SQL Server Express (`.\SQLEXPRESS`) with `sqlcmd`, and RepoScanner's scanner for the manual checks.

**Spec:** [docs/design/2026-10-08-scan-request-sending.md](../../design/2026-10-08-scan-request-sending.md), committed as `40d0f9e` and amended in `6efcc7d` (#81). Read its sections The message, Sending, When a send fails, The orchestrator, Local setup, README and Testing before starting; this plan argues from it.

**Scope:** #109 (Task 1, done in `5fc67b8`), #110 (Task 2, done in `69d7d51`) and #111 (Task 3). After Task 3, the whole branch gets one fresh review, then superpowers:finishing-a-development-branch.

## Global Constraints

- Branch: `feature/81-scan-request-sending` (already created from `develop`, with the design committed).
- **Commits:** one per board task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with the task number, such as `#110`. It never mentions Claude, Opus or any AI tool: no `Co-Authored-By` line. Push and open a pull request only when the user asks.
- **Board:** set the task to Doing when it starts and to Done when its commit is made: `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id <109|110|111> --state <Doing|Done>`.
- **Exact values:** the example's `correlationId` is `5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c` and `repositoryId` `0f8fad5b-d9cb-469f-a165-70867728950e`; `schemaVersion` is `1`; the field names, in this order, are `schemaVersion`, `correlationId`, `repositoryId`, `projectName`, `repositoryName`, `defaultBranch`.
- **Namespaces:** product code in `Enqueuer.Queue` (`src/Enqueuer/Queue/`); tests in `Enqueuer.Tests.Queue` (`tests/Enqueuer.Tests/Queue/`), and `RepositoryRoot` in `Enqueuer.Tests`.
- **Code conventions (RepoScanner ADR-021, as in Issues 1 and 2):** classes `sealed` or `static`; explicit types, no `var`; block bodies, no expression bodies; `using (...) { }` blocks; `ImplicitUsings` is off, so every file lists its usings; a comment giving the reason for each non-obvious line, citing the design by decision number. Tests: NUnit, `Method_Condition_Result` names, `Assert.EnterMultipleScope()` for several asserts. NUnit.Analyzers runs with warnings as errors, so never assert on a `const`.
- No em dash (U+2014) in any file. British English.
- `dotnet build Enqueuer.sln` has zero warnings and `dotnet test Enqueuer.sln` passes with the 80% line coverage gate at the end of the task. Filtered runs add `-p:CollectCoverage=false`.

## Review Focus

Inputs the spec implies but does not test by name, most likely to bite first. Each has its test in the task named.

**Task 1 (#109):**

1. A repository name holding characters the default JSON encoder escapes, such as `O'Brien & Sons (old)`, or a branch with `+`: written as `\u0027`, `\u0026` and `\u002B`, and read back by `System.Text.Json`, as the scanner reads it, exactly as they were.
2. Non-ASCII letters, such as project `Café` and repository `Ölçek`: the same.
3. A branch with `refs/` further in, such as `feature/refs/x`: valid, because the scanner refuses only a leading `refs/`, so the contract helper must check the start of the branch, not anywhere in it.
4. A contract helper that passes anything: every contract test built on it would pass for nothing, so it has its own tests, one per rule broken.
5. The correlation ID and the repository ID swapped, both being GUIDs: the builder and writer tests use the two different GUIDs, so a swap fails them.

**Task 2 (#110):**

1. A bug inside a send, such as an `InvalidOperationException`: it is not dressed up as "could not be sent", and keeps Issue 1's "Enqueuer failed" line with its stack trace, so it can be diagnosed. Only Azure's three kinds of failure are turned into one line.
2. Azure's own `RequestFailedException` message, which lists every response header over many lines: none of it reaches the one line, only the status and the error code.
3. A `RequestFailedException` with no error code, such as a `503` from a proxy: the line reads `Azure answered 503.`, with no empty brackets.
4. The job stopped part way through a send: the `OperationCanceledException` goes through unchanged, not reported as a failed send.
5. `Enqueuer:Queue:ServiceUri` with a trailing `/`: the client still points at `<account>/scan-nightly`, not at an empty queue name.

The message body being plain text, not Base64 (decision 2), cannot be seen through `FakeQueueClient`: it is checked in Task 2's manual checks, by peeking at the queue.

**Task 3 (#111):** each is a check in Task 3's steps.

1. A repository name with a space, `Power Platform ALM`: the first real request with one goes through the scanner's clone, so its scan run must be as good as the others'.
2. The real Azure DevOps repository IDs: until now the scanner's `Repositories` rows came from `enqueue-local.ps1`'s made-up IDs. Each row must carry the ID the enqueuer sent, so a later rename keeps the same row.
3. Messages already on `scan-nightly` before the run, which the scanner would scan too and blur the result: the queue is looked at first, and anything there that is not this run's is the user's to decide about.
4. A scan that fails, such as a clone that times out: it is recorded as a `Failed` scan run, its message stays and nothing is poisoned. Whether the fault is the enqueuer's message or the scanner's is found with superpowers:systematic-debugging before going on.
5. Nightly or on demand: every scan run must be `Nightly`, because the scanner takes that from the queue it reads (RepoScanner Issue 7 design, decision 5), never from the message.

---

### Task 1: The scan request and its contract (#109)

**Files:**
- Create: `docs/contracts/scan-request.example.json` (copied from RepoScanner)
- Create: `src/Enqueuer/Queue/ScanRequest.cs`
- Create: `src/Enqueuer/Queue/ScanRequestWriter.cs`
- Create: `tests/Enqueuer.Tests/RepositoryRoot.cs`
- Create: `tests/Enqueuer.Tests/Queue/ScanRequestContract.cs`
- Test: `tests/Enqueuer.Tests/Queue/ScanRequestContractTests.cs`, `tests/Enqueuer.Tests/Queue/ScanRequestTests.cs`, `tests/Enqueuer.Tests/Queue/ScanRequestWriterTests.cs`

`ScanRequestContractTests` is not in the design's project layout. It is added because of Review Focus 4.

**Interfaces:**
- Consumes: `Enqueuer.Repositories.RepositoryToScan(Guid Id, string ProjectName, string Name, string DefaultBranch)` (Issue 2).
- Produces, for #110:
  - `Enqueuer.Queue.ScanRequest(Guid CorrelationId, Guid RepositoryId, string ProjectName, string RepositoryName, string DefaultBranch)`, a `sealed record`, with `public const int SchemaVersion = 1` and `public static ScanRequest For(RepositoryToScan repository, Guid correlationId)`.
  - `Enqueuer.Queue.ScanRequestWriter.Write(ScanRequest request)`, returning `string`.
  - Test helpers: `Enqueuer.Tests.RepositoryRoot.FullPath` (`string`), `Enqueuer.Tests.Queue.ScanRequestContract.ExamplePath` (`string`) and `ScanRequestContract.ProblemsWith(string body)`, returning `IReadOnlyList<string>`, empty when the body keeps the contract.

- [ ] **Step 1: Set #109 to Doing**

- [ ] **Step 2: Copy the example file**

```powershell
New-Item -ItemType Directory -Force docs\contracts | Out-Null
Copy-Item ..\RepoScanner\docs\contracts\scan-request.example.json docs\contracts\scan-request.example.json
```

Check it is the same file as RepoScanner's committed one, whatever the line endings in either checkout:

Run: `git hash-object docs/contracts/scan-request.example.json` and `git -C ../RepoScanner rev-parse develop:docs/contracts/scan-request.example.json`
Expected: the same hash from both.

- [ ] **Step 3: Write the contract helper's tests**

`tests/Enqueuer.Tests/Queue/ScanRequestContractTests.cs`, with usings `System`, `System.IO`, `System.Text` and `NUnit.Framework`:

```csharp
// The contract helper itself: it must pass the example and catch what the scanner would refuse, or every contract
// test built on it would pass for nothing (Issue 3 design, decision 5)
[TestFixture]
public class ScanRequestContractTests
{
    // The example's values, compact, so each case below can change one thing whatever the file's line endings
    private const string Valid = "{\"schemaVersion\":1,\"correlationId\":\"5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c\","
        + "\"repositoryId\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"projectName\":\"Lumina\","
        + "\"repositoryName\":\"RepoScanner\",\"defaultBranch\":\"develop\"}";

    [Test]
    public void ProblemsWith_TheExampleFile_IsEmpty()
    {
        Assert.That(ScanRequestContract.ProblemsWith(File.ReadAllText(ScanRequestContract.ExamplePath)), Is.Empty);
    }

    [Test]
    public void ProblemsWith_TheExampleCompact_IsEmpty()
    {
        Assert.That(ScanRequestContract.ProblemsWith(Valid), Is.Empty);
    }

    [TestCase("\"develop\"", "\"refs/heads/develop\"")]
    [TestCase("\"develop\"", "\"Refs/Heads/develop\"")]
    [TestCase("\"develop\"", "\"  \"")]
    [TestCase("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c", "00000000-0000-0000-0000-000000000000")]
    [TestCase("0f8fad5b-d9cb-469f-a165-70867728950e", "not-a-guid")]
    [TestCase("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [TestCase("\"schemaVersion\":1", "\"schemaVersion\":\"1\"")]
    [TestCase("\"schemaVersion\"", "\"SchemaVersion\"")]
    [TestCase("\"Lumina\"", "\"\"")]
    [TestCase("\"RepoScanner\"", "7")]
    [TestCase(",\"defaultBranch\":\"develop\"", "")]
    [TestCase("{", "{\"priority\":1,")]
    public void ProblemsWith_TheExampleWithOneThingChanged_IsNotEmpty(string find, string replaceWith)
    {
        Assert.That(ScanRequestContract.ProblemsWith(Valid.Replace(find, replaceWith)), Is.Not.Empty);
    }

    // The scanner moves a Base64 body to the poison queue (RepoScanner Issue 7 design, decision 3)
    [Test]
    public void ProblemsWith_TheExampleInBase64_IsNotEmpty()
    {
        Assert.That(ScanRequestContract.ProblemsWith(Convert.ToBase64String(Encoding.UTF8.GetBytes(Valid))), Is.Not.Empty);
    }
}
```

- [ ] **Step 4: Run them to see them fail**

Run: `dotnet test tests/Enqueuer.Tests --filter "FullyQualifiedName~Enqueuer.Tests.Queue" -p:CollectCoverage=false`
Expected: build FAILS with CS0103, `The name 'ScanRequestContract' does not exist in the current context`.

- [ ] **Step 5: Write `RepositoryRoot` and `ScanRequestContract`**

`tests/Enqueuer.Tests/RepositoryRoot.cs`: RepoScanner's `tests/RepoScanner.Scanner.Tests/RepositoryRoot.cs`, in namespace `Enqueuer.Tests`, looking for `Enqueuer.sln`.

`tests/Enqueuer.Tests/Queue/ScanRequestContract.cs`: `internal static class ScanRequestContract` with:

- `public static string ExamplePath { get; }`, `RepositoryRoot.FullPath` + `docs`, `contracts`, `scan-request.example.json`.
- `public static IReadOnlyList<string> ProblemsWith(string body)`, one line per problem found. It reads the example file for the field names, their order and their value kinds, so a new example changes the contract. Its rules are the scanner's (RepoScanner `ScanRequestReader`):

| Rule | Problem when broken |
|---|---|
| The body parses as a JSON object. A `JsonException` is caught | Stop here: nothing else can be checked |
| The property names, in order, are exactly the example's, in its case | Names the expected and actual lists |
| Each property has the example's `JsonValueKind` | Names the field |
| `schemaVersion` is the example's number | Names the value |
| `correlationId` and `repositoryId` parse as GUIDs and are not `Guid.Empty` | Names the field |
| `projectName`, `repositoryName` and `defaultBranch` are not null, empty or only white space | Names the field |
| `defaultBranch` does not start with `refs/`, ignoring case (`StringComparison.OrdinalIgnoreCase`) | Names the value |

Every rule after the first is checked only when its field is present and of the right kind, so a missing field gives one problem, not several.

- [ ] **Step 6: Run them to see them pass**

Same command. Expected: PASS, 15 tests.

- [ ] **Step 7: Write the builder's and the writer's tests**

`tests/Enqueuer.Tests/Queue/ScanRequestTests.cs`, with usings `System`, `Enqueuer.Queue`, `Enqueuer.Repositories` and `NUnit.Framework`:

```csharp
// Building the request for one repository Issue 2 chose (Issue 3 design, decision 4)
[TestFixture]
public class ScanRequestTests
{
    // Two different GUIDs, so swapping them fails the test
    private static readonly Guid RepositoryId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid CorrelationId = new Guid("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c");

    [Test]
    public void For_ARepositoryToScan_CopiesItsFieldsAndTheCorrelationId()
    {
        ScanRequest request = ScanRequest.For(new RepositoryToScan(RepositoryId, "Kudos Wall", "victim-repo", "main"), CorrelationId);

        Assert.That(request, Is.EqualTo(new ScanRequest(CorrelationId, RepositoryId, "Kudos Wall", "victim-repo", "main")));
    }
}
```

`tests/Enqueuer.Tests/Queue/ScanRequestWriterTests.cs`, with usings `System`, `System.IO`, `System.Text.Json`, `System.Text.Json.Nodes`, `Enqueuer.Queue` and `NUnit.Framework`:

```csharp
// The JSON the scanner reads (Issue 3 design, decisions 3 and 5)
[TestFixture]
public class ScanRequestWriterTests
{
    private static readonly Guid CorrelationId = new Guid("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c");
    private static readonly Guid RepositoryId = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");

    // The contract test (criterion 2): the example's own values are written as the example
    [Test]
    public void Write_TheExamplesOwnValues_GivesTheExample()
    {
        JsonNode? written = JsonNode.Parse(ScanRequestWriter.Write(new ScanRequest(CorrelationId, RepositoryId, "Lumina", "RepoScanner", "develop")));
        JsonNode? example = JsonNode.Parse(File.ReadAllText(ScanRequestContract.ExamplePath));

        Assert.That(JsonNode.DeepEquals(written, example), Is.True);
    }

    // The example's names and order, lower-case GUIDs, no indentation (decision 3)
    [Test]
    public void Write_ARequest_GivesCompactJsonInTheExamplesOrder()
    {
        string body = ScanRequestWriter.Write(new ScanRequest(CorrelationId, RepositoryId, "Kudos Wall", "victim-repo", "main"));

        Assert.That(body, Is.EqualTo(
            "{\"schemaVersion\":1,\"correlationId\":\"5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c\","
            + "\"repositoryId\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"projectName\":\"Kudos Wall\","
            + "\"repositoryName\":\"victim-repo\",\"defaultBranch\":\"main\"}"));
    }

    // Names Azure DevOps allows: a space, a slash in a branch, non-ASCII letters, characters the default encoder
    // escapes, and refs/ further into a branch (Review Focus 1 to 3). Read back as the scanner reads them.
    [TestCase("Kudos Wall", "victim-repo", "feature/x")]
    [TestCase("Café", "Ölçek", "release/1.2")]
    [TestCase("Kudos Wall", "O'Brien & Sons (old)", "fix/a+b")]
    [TestCase("Kudos Wall", "victim-repo", "feature/refs/x")]
    public void Write_AnyRepository_ReadsBackUnchangedAndKeepsTheContract(string projectName, string repositoryName, string defaultBranch)
    {
        string body = ScanRequestWriter.Write(new ScanRequest(CorrelationId, RepositoryId, projectName, repositoryName, defaultBranch));

        using (JsonDocument document = JsonDocument.Parse(body))
        {
            JsonElement root = document.RootElement;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.GetProperty("projectName").GetString(), Is.EqualTo(projectName));
                Assert.That(root.GetProperty("repositoryName").GetString(), Is.EqualTo(repositoryName));
                Assert.That(root.GetProperty("defaultBranch").GetString(), Is.EqualTo(defaultBranch));
                Assert.That(ScanRequestContract.ProblemsWith(body), Is.Empty);
            }
        }
    }
}
```

- [ ] **Step 8: Run them to see them fail**

Same command as Step 4. Expected: build FAILS with CS0246, `The type or namespace name 'ScanRequest' could not be found`.

- [ ] **Step 9: Write `ScanRequest` and `ScanRequestWriter`**

`src/Enqueuer/Queue/ScanRequest.cs`: the record, `SchemaVersion` and `For` exactly as the design's The message section gives them, with its comments. `For` takes the four fields from the repository (`Name` becomes `RepositoryName`) and the correlation ID as given; it makes none (decision 4).

`src/Enqueuer/Queue/ScanRequestWriter.cs`: `public static class ScanRequestWriter` with `public static string Write(ScanRequest request)`. A `Utf8JsonWriter` over an `ArrayBufferWriter<byte>`, with default `JsonWriterOptions` (no indentation, the default encoder): `WriteStartObject`, then `WriteNumber("schemaVersion", ScanRequest.SchemaVersion)`, `WriteString` for `correlationId`, `repositoryId` (the `Guid` overloads, which write the lower-case `D` form), `projectName`, `repositoryName`, `defaultBranch`, in that order; `WriteEndObject`, `Flush`, then `Encoding.UTF8.GetString` of the written bytes. The six names are written out in the code, not taken from property names (decision 3).

- [ ] **Step 10: Run them to see them pass**

Same command. Expected: PASS, 21 tests.

- [ ] **Step 11: Full build and test**

Run: `dotnet build Enqueuer.sln` then `dotnet test Enqueuer.sln --tl:off`
Expected: 0 warnings, 0 errors; every test passes; the coverage table shows `Enqueuer` at 80% line or above, and `ScanRequest` and `ScanRequestWriter` fully covered.

- [ ] **Step 12: Review, commit, set #109 to Done**

Stop for the user's review of the diff. Once approved:

```bash
git add docs/contracts/scan-request.example.json src/Enqueuer/Queue tests/Enqueuer.Tests/RepositoryRoot.cs tests/Enqueuer.Tests/Queue
git commit -m "This commit adds the enqueuer's own copy of the scan request: docs/contracts/scan-request.example.json copied unchanged from RepoScanner; ScanRequest at schema version 1, built for a chosen repository with the correlation ID its caller makes; ScanRequestWriter writing the six fields as compact JSON in the example's names and order; and contract tests that the example's own values are written as the example, and that every request written keeps the scanner's rules, checked by a helper with tests of its own #109"
```

---

### Task 2: Sending to the queue (#110)

**Files:**
- Modify: `Directory.Packages.props`, `src/Enqueuer/Enqueuer.csproj`
- Create: `src/Enqueuer/Queue/QueueClients.cs`, `src/Enqueuer/Queue/ScanRequestSender.cs`, `src/Enqueuer/Queue/ScanQueueNotFoundException.cs`, `src/Enqueuer/Queue/ScanQueueUnavailableException.cs`
- Modify: `src/Enqueuer/EnqueueOrchestrator.cs`, `src/Enqueuer/Program.cs`, `README.md`
- Create: `tests/Enqueuer.Tests/Queue/FakeResponse.cs`, `tests/Enqueuer.Tests/Queue/FakeQueueClient.cs` (test helpers)
- Test: `tests/Enqueuer.Tests/Queue/QueueClientsTests.cs`, `tests/Enqueuer.Tests/Queue/ScanRequestSenderTests.cs` (new); `tests/Enqueuer.Tests/EnqueueOrchestratorTests.cs`, `tests/Enqueuer.Tests/ProgramTests.cs` (modified)

`ExitCodes.cs` is unchanged: its comments already say 1 is "a send failed" and 2 is "the queue is missing".

**Interfaces:**
- Consumes: from Task 1, `ScanRequest`, `ScanRequest.For(RepositoryToScan repository, Guid correlationId)`, `ScanRequestWriter.Write(ScanRequest request)` returning `string`, and the test helper `ScanRequestContract.ProblemsWith(string body)`. From Issue 1, `Enqueuer.Configuration.QueueOptions` with `Name`, `ConnectionString` and `ServiceUri`, the last two `string?`, exactly one of them set.
- Produces, for #111 and Issue 4:
  - `Enqueuer.Queue.QueueClients.Create(QueueOptions options)`, returning `Azure.Storage.Queues.QueueClient`.
  - `Enqueuer.Queue.ScanRequestSender`: `public ScanRequestSender(QueueClient queue)` and `public Task SendAsync(ScanRequest request, CancellationToken cancellationToken)`.
  - `Enqueuer.Queue.ScanQueueNotFoundException(string queueName, Exception innerException)` and `Enqueuer.Queue.ScanQueueUnavailableException(string message, Exception innerException)`, both `sealed`, deriving from `Exception`.
  - `EnqueueOrchestrator`'s constructor gains `ScanRequestSender sender` as its last parameter.
  - The log lines `Queued {ProjectName}/{RepositoryName} with correlation {CorrelationId}` and `Sent {SentCount} scan requests to {QueueName}`.
  - The test helper `FakeQueueClient`: `public FakeQueueClient(string name)`; `Sent`, a `List<(string Text, TimeSpan? VisibilityTimeout, TimeSpan? TimeToLive)>` of the sends that succeeded; `Attempts`, an `int` counting every send tried; `FailSend(int attempt, Exception exception)`, making that attempt, counted from 1, throw that exception; `ReplaceQueue(IServiceCollection services)`, registering itself as the `QueueClient`.

- [ ] **Step 1: Set #110 to Doing**

- [ ] **Step 2: Add the two packages**

`Directory.Packages.props`: a new group after the Azure DevOps one, with the comment `<!-- The storage queue: Azurite's connection string locally, the job's managed identity in Azure (Issue 3 design, decisions 8 and 20). Added 2026-10-08 at RepoScanner's versions. -->`, holding `Azure.Storage.Queues` `12.27.1` and `Azure.Identity` `1.21.0`. `src/Enqueuer/Enqueuer.csproj`: a `PackageReference` to each, each with a one-line comment in the style of the others.

Run: `dotnet build Enqueuer.sln` then `dotnet list package --vulnerable --include-transitive`
Expected: 0 warnings, 0 errors; every project "has no vulnerable packages given the current sources".

- [ ] **Step 3: Write the two test helpers**

`tests/Enqueuer.Tests/Queue/FakeResponse.cs`: RepoScanner's `tests/RepoScanner.Scanner.Tests/Queue/FakeResponse.cs`, in namespace `Enqueuer.Tests.Queue`, `internal`.

`tests/Enqueuer.Tests/Queue/FakeQueueClient.cs`: `internal sealed class FakeQueueClient : QueueClient`, cut down from RepoScanner's to the members in the Interfaces block. It overrides `Name`, and exactly the overload the sender calls, `SendMessageAsync(string messageText, TimeSpan? visibilityTimeout = null, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)`: each call adds 1 to `Attempts`, throws the exception set for that attempt if there is one, and otherwise adds the call to `Sent` and returns `Response.FromValue(QueuesModelFactory.SendReceipt(...), new FakeResponse(201))`. `ReplaceQueue` is `services.AddSingleton<QueueClient>(this)`: registered after `Program`'s own, it wins.

- [ ] **Step 4: Write the client's and the sender's tests**

`tests/Enqueuer.Tests/Queue/QueueClientsTests.cs`. Building a client makes no network call, so neither test needs Azurite or Azure:

```csharp
[Test]
public void Create_WithAConnectionString_PointsAtTheNamedQueueInAzurite()
{
    QueueClient client = QueueClients.Create(new QueueOptions { Name = "scan-test", ConnectionString = "UseDevelopmentStorage=true" });

    using (Assert.EnterMultipleScope())
    {
        Assert.That(client.Uri, Is.EqualTo(new Uri("http://127.0.0.1:10001/devstoreaccount1/scan-test")));
        Assert.That(client.Name, Is.EqualTo("scan-test"));
    }
}

// A trailing / must not leave the queue name empty (Review Focus, Task 2, 5)
[TestCase("https://example.queue.core.windows.net")]
[TestCase("https://example.queue.core.windows.net/")]
public void Create_WithAServiceUri_PointsAtTheNamedQueueInTheAccount(string serviceUri)
{
    QueueClient client = QueueClients.Create(new QueueOptions { Name = "scan-nightly", ServiceUri = serviceUri });

    Assert.That(client.Uri, Is.EqualTo(new Uri("https://example.queue.core.windows.net/scan-nightly")));
}
```

`tests/Enqueuer.Tests/Queue/ScanRequestSenderTests.cs`: a `[SetUp]` making `_queue = new FakeQueueClient("scan-test")` and `_sender = new ScanRequestSender(_queue)`, and one request for every test, `new ScanRequest(new Guid("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c"), new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"), "Kudos Wall", "victim-repo", "main")`. Every `ScanQueueUnavailableException` message starts with the prefix `The scan request for Kudos Wall/victim-repo could not be sent to scan-test: `.

| Test | Set up | Asserts |
|---|---|---|
| `SendAsync_ARequest_SendsTheWritersBodyVisibleAtOnceWithTheDefaultLifetime` | nothing | `_queue.Sent` is exactly one `(ScanRequestWriter.Write(request), null, null)` |
| `SendAsync_WhenTheQueueIsMissing_ThrowsNotFoundNamingTheQueue` | attempt 1 throws `new RequestFailedException(404, "The specified queue does not exist.", "QueueNotFound", null)` | `ScanQueueNotFoundException`; message exactly `The queue scan-test does not exist. Create it, or correct Enqueuer:Queue:Name.`; `InnerException` the same instance |
| `SendAsync_WhenAzureRefuses_ThrowsUnavailableWithStatusAndErrorCode`, with `[TestCase(403, "AuthorizationPermissionMismatch", "Azure answered 403 (AuthorizationPermissionMismatch).")]` and `[TestCase(503, null, "Azure answered 503.")]` | attempt 1 throws `new RequestFailedException(status, "Service request failed.\r\nStatus: " + status + "\r\nHeaders:\r\nx-ms-request-id: 1234", errorCode, null)` | `ScanQueueUnavailableException`; message exactly the prefix plus the third argument; not containing `Headers`; `InnerException` the same instance (Review Focus, Task 2, 2 and 3) |
| `SendAsync_WhenTheRetriesRunOut_GivesTheFirstLineOfTheMessage` | attempt 1 throws `new AggregateException("Retry failed after 6 tries.\r\nRetry settings can be adjusted in ClientOptions.Retry.", new RequestFailedException("Connection refused"))` | `ScanQueueUnavailableException`; message exactly the prefix plus `Retry failed after 6 tries.` |
| `SendAsync_WhenTheIdentityGetsNoToken_GivesTheFirstLineOfTheMessage` | attempt 1 throws `new AuthenticationFailedException("DefaultAzureCredential failed to retrieve a token from the included credentials.\n- EnvironmentCredential authentication unavailable.")` | `ScanQueueUnavailableException`; message exactly the prefix plus `DefaultAzureCredential failed to retrieve a token from the included credentials.` |
| `SendAsync_WhenCancelled_LetsTheCancellationThrough` | attempt 1 throws `new OperationCanceledException()` | `Assert.ThrowsAsync<OperationCanceledException>` (Review Focus, Task 2, 4) |
| `SendAsync_WhenSomethingUnexpectedFails_LetsItThrough` | attempt 1 throws `new InvalidOperationException("a bug")` | `Assert.ThrowsAsync<InvalidOperationException>` (Review Focus, Task 2, 1) |

- [ ] **Step 5: Run them to see them fail**

Run: `dotnet test tests/Enqueuer.Tests --filter "FullyQualifiedName~Enqueuer.Tests.Queue" -p:CollectCoverage=false`
Expected: build FAILS with CS0103 or CS0246 for `QueueClients`, `ScanRequestSender`, `ScanQueueNotFoundException` and `ScanQueueUnavailableException`.

- [ ] **Step 6: Write `QueueClients`, `ScanRequestSender` and the two exceptions**

The signatures and comments are the design's (Sending, When a send fails).

- `QueueClients.Create`: RepoScanner's `QueueClients.Create`, cut to one queue, `options.Name`. `QueueOptions.ServiceUri` is a `string` here, already checked as an absolute `https` URL, so it goes into `QueueUriBuilder` as `new Uri(options.ServiceUri)`.
- `ScanQueueNotFoundException` builds its own message: `The queue {queueName} does not exist. Create it, or correct Enqueuer:Queue:Name.`
- `ScanRequestSender.SendAsync` writes the body with `ScanRequestWriter.Write` and calls `SendMessageAsync(body, visibilityTimeout: null, timeToLive: null, cancellationToken)`, the overload `FakeQueueClient` overrides. It catches these, in this order, and nothing else:
  1. A `RequestFailedException` whose `ErrorCode` is `QueueErrorCode.QueueNotFound`: throws `ScanQueueNotFoundException(_queue.Name, exception)`.
  2. Any other `RequestFailedException`: the text `Azure answered {Status} ({ErrorCode}).`, or `Azure answered {Status}.` when `ErrorCode` is null or empty.
  3. An `AggregateException` or an `AuthenticationFailedException`: the text is the first line of the exception's `Message`, as `StringReader.ReadLine` reads it.

  Cases 2 and 3 throw `ScanQueueUnavailableException`, its message `The scan request for {ProjectName}/{RepositoryName} could not be sent to {_queue.Name}: ` followed by that text, with the original as the inner exception.

- [ ] **Step 7: Run them to see them pass**

Same command. Expected: PASS, 37 tests (Task 1's 26, `QueueClientsTests`' 3 and `ScanRequestSenderTests`' 8).

- [ ] **Step 8: Write the orchestrator's and `Program`'s tests**

`EnqueueOrchestratorTests`: `SetUp` adds `_queue = new FakeQueueClient("scan-test")` and passes `new ScanRequestSender(_queue)` as the orchestrator's last argument. The default sample listing skips all three of its repositories, so the tests that send first answer with this listing, a constant in the test class:

```csharp
// Three repositories to scan, in two projects, and a disabled one among them
private const string ThreeToScanAndOneDisabled = """
    {
      "count": 4,
      "value": [
        { "id": "0f8fad5b-d9cb-469f-a165-70867728950e", "name": "victim-repo", "project": { "name": "Kudos Wall" }, "defaultBranch": "refs/heads/main" },
        { "id": "278d5cd2-584d-4b63-824a-2ba458937249", "name": "old-repo", "project": { "name": "Kudos Wall" }, "defaultBranch": "refs/heads/main", "isDisabled": true },
        { "id": "66efb083-777a-4cac-a350-a24b046be6be", "name": "ProblemApp", "project": { "name": "Kudos Wall" }, "defaultBranch": "refs/heads/develop" },
        { "id": "5febef5a-833d-4e14-b9c0-14cb638f91e6", "name": "WebShop", "project": { "name": "Retail" }, "defaultBranch": "refs/heads/main" }
      ]
    }
    """;
```

A private helper, `FieldIn(string body, string name)`, returns one field of a sent body as a string, read with `JsonDocument`.

| Test | Asserts |
|---|---|
| `RunAsync_SendsOneRequestPerRepositoryToScanInListedOrder` | the `repositoryName` of each body in `_queue.Sent` is exactly `victim-repo`, `ProblemApp`, `WebShop`: none for `old-repo` (criterion 1) |
| `RunAsync_EveryRequestSent_KeepsTheContract` | `ScanRequestContract.ProblemsWith(body)` is empty for every body sent (criterion 2) |
| `RunAsync_EachRequestSent_HasItsOwnCorrelationId` | the three `correlationId`s are unique and none is `Guid.Empty` (decision 4) |
| `RunAsync_LogsAQueuedLinePerRequestWithItsCorrelationId` | exactly three records have a `CorrelationId` property, their values the bodies' `correlationId`s in order; the first message is `Queued Kudos Wall/victim-repo with correlation ` followed by the first body's `correlationId` (decision 15) |
| `RunAsync_EndsWithHowManyWereSent` | the last record's message is `Sent 3 scan requests to scan-test`, its `SentCount` property `"3"` |
| `RunAsync_WhenTheSecondSendFails_StopsBeforeTheThird` | attempt 2 throws `new RequestFailedException(503, "busy", "ServerBusy", null)`; `RunAsync` throws `ScanQueueUnavailableException`; `_queue.Attempts` is 2; `_queue.Sent` holds `victim-repo`'s only; one `Queued` line; no record with `SentCount` (decision 12) |
| `RunAsync_NothingToScan_SendsNothingAndSaysSentZero` | the default sample listing; `_queue.Attempts` is 0; the last message is `Sent 0 scan requests to scan-test`. It replaces `RunAsync_UntilIssue3_SaysNothingWasSent`, which is deleted |

`ProgramTests`: a constant `OneToScan`, the first repository of `ThreeToScanAndOneDisabled` alone, with `"count": 1`, and a lambda replacing both fakes, `services => { azureDevOps.ReplaceAzureDevOps(services); queue.ReplaceQueue(services); }`. The existing tests keep the default sample listing, which sends nothing, so they need no queue fake.

| Test | Set up | Asserts |
|---|---|---|
| `RunAsync_WithARepositoryToScan_SendsItAndReturnsZero` | `OneToScan` | exit 0; `queue.Sent` has 1 item; the output contains `Sent 1 scan requests to scan-test` |
| `RunAsync_WhenTheQueueIsMissing_WritesOneLineAndReturnsTwo` | `OneToScan`; attempt 1 throws Step 4's `QueueNotFound` exception | exit 2; exactly one ` FTL] ` line, containing `Queue missing, enqueuer stopped: The queue scan-test does not exist. Create it, or correct Enqueuer:Queue:Name.`; no `   at ` in the output |
| `RunAsync_WhenASendFails_WritesOneLineAndReturnsOne` | `OneToScan`; attempt 1 throws Step 4's `403` exception | exit 1; exactly one ` FTL] ` line, containing `Scan requests could not be sent, enqueuer stopped: The scan request for Kudos Wall/victim-repo could not be sent to scan-test: Azure answered 403 (AuthorizationPermissionMismatch).`; neither `   at ` nor `Headers` in the output |
| `CreateBuilder_WhenBuilt_HasAClientForTheNamedQueue` | `Program.CreateBuilder(ValidArgs)`, built | the `QueueClient` service's `Name` is `scan-test`, and `EnqueueOrchestrator` resolves |

- [ ] **Step 9: Run them to see them fail**

Run: `dotnet test tests/Enqueuer.Tests -p:CollectCoverage=false`
Expected: build FAILS with CS1729, `'EnqueueOrchestrator' does not contain a constructor that takes 6 arguments`.

- [ ] **Step 10: Send from the orchestrator, and register and catch in `Program`**

- `EnqueueOrchestrator`: the design's The orchestrator, steps 6 and 7. The class comment loses "until then nothing is sent". `{QueueName}` is `_queueOptions.Name`, as on the starting line.
- `Program.CreateBuilder`, before the orchestrator's registration: the `QueueClient` as a singleton built by `QueueClients.Create(services.GetRequiredService<IOptions<QueueOptions>>().Value)`, with a comment that building it makes no network call; then `ScanRequestSender` as a singleton.
- `Program.RunAsync`: the design's two `catch` blocks, after `AzureDevOpsUnavailableException`'s and before the one for any other exception.

- [ ] **Step 11: Run them to see them pass**

Same command as Step 9. Expected: PASS, 159 tests (138, less the deleted one, plus 11 in `Queue`, 7 in `EnqueueOrchestratorTests` and 4 in `ProgramTests`), 0 failed.

- [ ] **Step 12: Manual checks against Azurite**

They need `Enqueuer__AzureDevOps__Pat` set (check it without printing it: `[Environment]::GetEnvironmentVariable("Enqueuer__AzureDevOps__Pat", "User").Length -gt 0`), Azurite started in its own terminal with `azurite --location c:\azurite_data`, and the queues created as the design's Local setup says. Keep each run's output for the README:

1. `dotnet run --project src/Enqueuer`. Expected: a `Queued` line for each repository Azure DevOps lists to scan, then `Sent N scan requests to scan-nightly`, and `$LASTEXITCODE` 0.
2. `az storage message peek --queue-name scan-nightly --num-messages 32 --connection-string "UseDevelopmentStorage=true"`. Expected: N messages, each `content` plain JSON beginning `{"schemaVersion":1,`, not Base64 (decision 2).
3. `az storage message clear --queue-name scan-nightly --connection-string "UseDevelopmentStorage=true"`, so a later scanner run does not scan these.
4. `dotnet run --project src/Enqueuer -- --Enqueuer:Queue:Name scan-missing-zz9q1`. Expected: one line starting `Queue missing, enqueuer stopped`, and `$LASTEXITCODE` 2.
5. Stop Azurite, then `dotnet run --project src/Enqueuer`. Expected: after about 25 seconds of retries, one line starting `Scan requests could not be sent, enqueuer stopped`, and `$LASTEXITCODE` 1. Record whether it shows `Azure answered` (a `RequestFailedException`) or `Retry failed` (an `AggregateException`), as the design asks.

A run that does not match its Expected line is a finding for superpowers:systematic-debugging, not a reason to change the Expected line.

- [ ] **Step 13: README**

The design's README section, for #110: **Run** gains Azurite and the queue (Local setup) before `dotnet run`; **What it logs** shows run 1's lines in place of the `Nothing sent` example; **Exit codes** shows run 4's and run 5's lines; **Known limitations** gains the design's. The end-to-end section with the scanner is #111's.

- [ ] **Step 14: Full build and test**

Run: `dotnet build Enqueuer.sln` then `dotnet test Enqueuer.sln --tl:off`
Expected: 0 warnings, 0 errors; 159 tests pass; `Enqueuer` at 80% line coverage or above, the new classes fully covered.

- [ ] **Step 15: Review, commit, set #110 to Done**

Stop for the user's review of the diff. Once approved:

```bash
git add Directory.Packages.props src/Enqueuer tests/Enqueuer.Tests README.md
git commit -m "This commit sends one scan request per chosen repository to the queue: QueueClients builds the client from Enqueuer:Queue, with Azurite's connection string locally and DefaultAzureCredential in Azure, sending plain text; the orchestrator sends each repository's request with a new correlation ID through ScanRequestSender, one at a time in the order listed, logging a Queued line with its correlation ID and then how many were sent; a missing queue exits 2 with one line starting 'Queue missing', and a send still failing after the storage SDK's retries stops the run and exits 1 with one line giving Azure's status and error code, never the SDK's own message; and the README gains Azurite, the queue and the new lines #110"
```

---

### Task 3: End to end with the scanner (#111)

**Files:**
- Modify: `README.md` (#111)
- Modify: `docs/design/2026-10-08-scan-request-sending.md` (#81, its own commit)
- Create: `.superpowers/sdd/2026-10-08-scan-request-sending/e2e-results.md`, the record for the pull request (git-ignored workspace, not committed)

No code changes: this task runs the two apps and records what they print. RepoScanner is only run, never changed.

**Interfaces:**
- Consumes: the enqueuer of Task 2 (`dotnet run --project src/Enqueuer`), its `Queued` and `Sent` lines; RepoScanner's scanner (`dotnet run --project ..\RepoScanner\src\RepoScanner.Scanner`), its line `Stopped taking messages from {QueueName}: {Reason}. {ScannedCount} scanned, {FailedCount} failed, {SkippedCount} skipped, {PoisonedCount} poisoned`; RepoScanner's database `RepoScanner` on `.\SQLEXPRESS`, tables `ScanRuns` (`CorrelationId`, `Status`, `TriggerType`, `RepositoryId`) and `Repositories` (`Id`, `AzureDevOpsId`, `ProjectName`, `Name`).
- Produces: the README section **Scan what it queued**, and `e2e-results.md` for the pull request.

Every command reads a secret from the user's environment into the process for that command only, never printing it, because this session's shell predates the variables (Task 2's ruling): `$env:<name> = [Environment]::GetEnvironmentVariable("<name>", "User")` for `Enqueuer__AzureDevOps__Pat`, `Scanner__AzureDevOps__Pat` and `Scanner__Database__ConnectionString`.

- [ ] **Step 1: Set #111 to Doing**

- [ ] **Step 2: Check what the run needs, changing nothing**

| Check | Expected | If not |
|---|---|---|
| Something listens on port 10001 | Azurite. Already running on 2026-10-08 as the user's own process | Ask the user to start it: `azurite --location c:\azurite_data` |
| `az storage queue list --connection-string "UseDevelopmentStorage=true" --query "[].name" -o tsv` | `scan-nightly` and `scan-nightly-poison` among them | Create them as the README's Run, step 3 |
| The three user variables above have a value (length only) | All three set | Ask the user |
| `sqlcmd -S .\SQLEXPRESS -E -C -d RepoScanner -h -1 -W -b -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM ScanRuns"` | A number, exit 0 | Ask the user: RepoScanner's README sets the database up |
| `git -C ..\RepoScanner status -sb` | On `develop`, with no changes | Ask the user before running another branch's scanner |

- [ ] **Step 3: Look at the queue before emptying it (Review Focus, Task 3, 3)**

Run: `az storage message peek --queue-name scan-nightly --num-messages 32 --connection-string "UseDevelopmentStorage=true" --query "length(@)" -o tsv` from bash (PowerShell passes `length(@)` badly to `az`).
Expected: `0`. Anything else is the user's: stop and ask before clearing it.

- [ ] **Step 4: Run the enqueuer**

Run: `dotnet run --project src/Enqueuer`, with `Enqueuer__AzureDevOps__Pat` read in.
Expected: a `Queued` line for each repository listed to scan (5 for BlacklightInternalsMo on 2026-10-08, none skipped), `Sent 5 scan requests to scan-nightly`, `$LASTEXITCODE` 0. Keep the five correlation IDs.

- [ ] **Step 5: Peek at what was sent**

Run: `az storage message peek --queue-name scan-nightly --num-messages 32 --connection-string "UseDevelopmentStorage=true" --query "[].content" -o tsv`
Expected: 5 lines of plain JSON starting `{"schemaVersion":1,`, each `correlationId` one of Step 4's. Keep each `repositoryId`.

- [ ] **Step 6: Run the scanner**

Run: `dotnet run --project ..\RepoScanner\src\RepoScanner.Scanner`, with `Scanner__AzureDevOps__Pat` and `Scanner__Database__ConnectionString` read in. It uses its own `Development` settings: `scan-nightly` in Azurite, BlacklightInternalsMo. Save the whole output to the workspace; it clones and scans five repositories, so allow up to 15 minutes.
Expected: for each request a `Message ... for Kudos Wall/<name>, correlation <id>, attempt 1` line with one of Step 4's IDs, a `Cloned ...` line, and finally `Stopped taking messages from scan-nightly: queue empty. 5 scanned, 0 failed, 0 skipped, 0 poisoned`; `$LASTEXITCODE` 0. A `Failed` scan or a poisoned message is Review Focus, Task 3, 4: debug it before going on, and change nothing in RepoScanner without asking.

- [ ] **Step 7: The queues afterwards**

Run: the Step 3 count on `scan-nightly` and on `scan-nightly-poison`.
Expected: `0` on both: every request was taken, scanned and deleted, and none was poisoned.

- [ ] **Step 8: Each correlation ID's scan run (criterion 1; Review Focus, Task 3, 1, 2 and 5)**

Run, with Step 4's five IDs in the `IN` list:

```
sqlcmd -S .\SQLEXPRESS -E -C -d RepoScanner -h -1 -W -s "|" -Q "SET NOCOUNT ON; SELECT s.CorrelationId, r.ProjectName, r.Name, r.AzureDevOpsId, s.TriggerType, s.Status FROM ScanRuns s JOIN Repositories r ON r.Id = s.RepositoryId WHERE s.CorrelationId IN ('<id1>', '<id2>', '<id3>', '<id4>', '<id5>') ORDER BY s.Id"
```

Expected: exactly 5 rows, one per ID, in the order sent; `TriggerType` `Nightly` on every row; `Status` `Completed` or `PartiallyCompleted` on every row, `Power Platform ALM`'s included; each `AzureDevOpsId` the `repositoryId` Step 5 showed for that repository.

- [ ] **Step 9: The two failure checks**

The design's End to end steps 6 and 7, a missing queue and an unreachable one, were run in Task 2, Step 12, runs 4 and 5, and their lines are in the README. They are not run again: run 5 needed Azurite stopped, and it is the user's process. Their lines go into the results as recorded in the ledger.

- [ ] **Step 10: Write the results for the pull request**

`e2e-results.md` in the workspace, under a heading `End to end, 2026-10-08 (#111)`: for each of Steps 3 to 9, the command and what it printed, with the scanner's log cut to its `Message`, `Cloned` and `Stopped taking messages` lines. No secret appears in it: check that none of the three variables' values is in the file before saving.

- [ ] **Step 11: README**

A section **Scan what it queued**, after **What it logs**: how to see the scanner take this run's requests, in five numbered steps (Steps 3 to 6 and 8, each its command and one line on what to expect), pointing at RepoScanner's README for the scanner's own one-off setup, with Step 8's query and two of its real rows as the example. The design's README section asks for it (decision 19).

- [ ] **Step 12: The design, under #81**

`docs/design/2026-10-08-scan-request-sending.md`, each change marked *after #110's final review* or *after #111*:
- **When a send fails**, the third row: a network error or timeout comes as an `AggregateException` when several tries failed, or bare when only the last did (a `RequestFailedException` with status 0, an `IOException`, or the SDK's own timeout as a cancellation the run did not ask for); a cancellation of the run itself goes through unchanged, even part way through the retries.
- **End to end**: step 7 says about 40 seconds, not 25; steps 6 and 7 were run in #110.
- **Known limitations**: a misspelt queue name in Azure may answer `403`, not `404`, because `id-scanner`'s role covers `scan-nightly` only, so exit 2 may hold only locally. For Issue 6 to check.

```bash
git add docs/design/2026-10-08-scan-request-sending.md
git commit -m "This commit brings Issue 3's design up to date after #110 and #111: a network failure after the retries may come bare when only the last try failed, and a stopped run is never a failed send; the unreachable-queue check took about 40 seconds and was run in #110; and a misspelt queue in Azure may answer 403, not 404, which Issue 6 is to check #81"
```

- [ ] **Step 13: Build and test**

Run: `dotnet build Enqueuer.sln` then `dotnet test Enqueuer.sln --tl:off`
Expected: 0 warnings; 167 tests pass, as after Task 2; nothing in the code changed.

- [ ] **Step 14: Review, commit, set #111 to Done**

Stop for the user's review of the README and `e2e-results.md`. Once approved:

```bash
git add README.md
git commit -m "This commit records the enqueuer and the scanner working together locally: the README gains how to see the scanner take a run's requests from scan-nightly in Azurite and find each one's scan run by its correlation ID, with real rows from 2026-10-08, when all five requests BlacklightInternalsMo listed were scanned as Nightly runs under their real Azure DevOps IDs #111"
```

The commit message's last part is written from what Step 8 actually showed.
