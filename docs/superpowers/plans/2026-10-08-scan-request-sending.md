# Issue 3: Scan request sending, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The enqueuer has its own copy of the scan request: `docs/contracts/scan-request.example.json` copied from RepoScanner, `ScanRequest` built for a chosen repository with a correlation ID its caller makes, `ScanRequestWriter` writing it as the JSON the scanner reads, and contract tests tying the two to the example.

**Architecture:** Two small types in a new `Enqueuer.Queue` namespace, with no new package: a record with a static builder, and a static writer using `Utf8JsonWriter`. A test helper, `ScanRequestContract`, holds the scanner's rules and reads the example file through a `RepositoryRoot` helper; #110's orchestrator tests reuse it on every body sent. Nothing is sent yet, and nothing outside the tests calls the new types until #110.

**Tech Stack:** .NET 10, `System.Text.Json` (`Utf8JsonWriter`, `JsonNode.DeepEquals`, `JsonDocument`), NUnit 4, coverlet.msbuild, the `az` CLI for the board.

**Spec:** [docs/design/2026-10-08-scan-request-sending.md](../../design/2026-10-08-scan-request-sending.md), committed as `40d0f9e` (#81). Read its sections The message (#109) and Testing before starting; this plan argues from it.

**Scope:** task #109 only. #110 and #111 are added to this plan when they are planned.

## Global Constraints

- Branch: `feature/81-scan-request-sending` (already created from `develop`, with the design committed).
- **Commits:** one per board task, only after the user has reviewed that task's diff. The message starts `This commit` and ends with the task number, `#109`. It never mentions Claude, Opus or any AI tool: no `Co-Authored-By` line. Push and open a pull request only when the user asks.
- **Board:** set the task to Doing when it starts and to Done when its commit is made: `az boards work-item update --org https://dev.azure.com/Blacklight-Internal --id 109 --state <Doing|Done>`.
- **Exact values:** the example's `correlationId` is `5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c` and `repositoryId` `0f8fad5b-d9cb-469f-a165-70867728950e`; `schemaVersion` is `1`; the field names, in this order, are `schemaVersion`, `correlationId`, `repositoryId`, `projectName`, `repositoryName`, `defaultBranch`.
- **Namespaces:** product code in `Enqueuer.Queue` (`src/Enqueuer/Queue/`); tests in `Enqueuer.Tests.Queue` (`tests/Enqueuer.Tests/Queue/`), and `RepositoryRoot` in `Enqueuer.Tests`.
- **Code conventions (RepoScanner ADR-021, as in Issues 1 and 2):** classes `sealed` or `static`; explicit types, no `var`; block bodies, no expression bodies; `using (...) { }` blocks; `ImplicitUsings` is off, so every file lists its usings; a comment giving the reason for each non-obvious line, citing the design by decision number. Tests: NUnit, `Method_Condition_Result` names, `Assert.EnterMultipleScope()` for several asserts. NUnit.Analyzers runs with warnings as errors, so never assert on a `const`.
- No em dash (U+2014) in any file. British English.
- `dotnet build Enqueuer.sln` has zero warnings and `dotnet test Enqueuer.sln` passes with the 80% line coverage gate at the end of the task. Filtered runs add `-p:CollectCoverage=false`.

## Review Focus

Inputs the spec implies but does not test by name, most likely to bite first. Each has its test in Task 1.

1. A repository name holding characters the default JSON encoder escapes, such as `O'Brien & Sons (old)`, or a branch with `+`: written as `\u0027`, `\u0026` and `\u002B`, and read back by `System.Text.Json`, as the scanner reads it, exactly as they were.
2. Non-ASCII letters, such as project `Café` and repository `Ölçek`: the same.
3. A branch with `refs/` further in, such as `feature/refs/x`: valid, because the scanner refuses only a leading `refs/`, so the contract helper must check the start of the branch, not anywhere in it.
4. A contract helper that passes anything: every contract test built on it would pass for nothing, so it has its own tests, one per rule broken.
5. The correlation ID and the repository ID swapped, both being GUIDs: the builder and writer tests use the two different GUIDs, so a swap fails them.

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
