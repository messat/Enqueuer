using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Enqueuer.Tests.Queue
{
    // What a scan request the enqueuer writes must keep (Issue 3 design, decision 5):
    // - the scanner's value rules (RepoScanner Issue 7 design, Message format);
    // - stricter than the scanner, which ignores case and extra fields: exactly the example's names, in its case and
    //   order, each once (decision 3);
    // - not the scanner's checks that a project or repository name could be a folder name (not . or .., no \ or /):
    //   the enqueuer sends Azure DevOps' names unchanged, and Azure DevOps' naming rules refuse those (The message).
    // The field names, their order and their kinds come from the example copied from RepoScanner, so a new example
    // changes the contract.
    internal static class ScanRequestContract
    {
        // docs/contracts/scan-request.example.json, copied unchanged from RepoScanner (ENQ-ADR-001)
        public static string ExamplePath { get; } = Path.Combine(RepositoryRoot.FullPath, "docs", "contracts", "scan-request.example.json");

        // The scanner refuses a GUID that is not one, or is all zeros
        private static readonly string[] GuidFields = { "correlationId", "repositoryId" };

        // The scanner refuses text that is empty or only white space
        private static readonly string[] TextFields = { "projectName", "repositoryName", "defaultBranch" };

        // What is wrong with body as a scan request, one line per problem; empty when nothing is
        public static IReadOnlyList<string> ProblemsWith(string body)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                // Includes a body sent as Base64: nothing else can be checked
                return new[] { "The body is not JSON" };
            }

            using (document)
            {
                using (JsonDocument example = JsonDocument.Parse(File.ReadAllText(ExamplePath)))
                {
                    return ProblemsWith(document.RootElement, example.RootElement);
                }
            }
        }

        private static List<string> ProblemsWith(JsonElement root, JsonElement example)
        {
            List<string> problems = new List<string>();
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add("The body is not a JSON object");
                return problems;
            }

            // Exactly the example's names, in its case and order, each once (decision 3)
            List<string> expectedNames = example.EnumerateObject().Select(property => property.Name).ToList();
            List<string> actualNames = root.EnumerateObject().Select(property => property.Name).ToList();
            if (!actualNames.SequenceEqual(expectedNames, StringComparer.Ordinal))
            {
                problems.Add("The fields are " + string.Join(", ", actualNames) + ", not " + string.Join(", ", expectedNames));
            }

            // A field that is missing is reported once, above, so only the fields present are checked further
            foreach (JsonProperty expected in example.EnumerateObject())
            {
                if (root.TryGetProperty(expected.Name, out JsonElement value))
                {
                    string? problem = ProblemWith(expected, value);
                    if (problem != null)
                    {
                        problems.Add(problem);
                    }
                }
            }

            return problems;
        }

        private static string? ProblemWith(JsonProperty expected, JsonElement value)
        {
            string name = expected.Name;
            if (value.ValueKind != expected.Value.ValueKind)
            {
                return name + " is a " + value.ValueKind + ", not a " + expected.Value.ValueKind;
            }

            if (name == "schemaVersion" && (!value.TryGetInt32(out int version) || version != expected.Value.GetInt32()))
            {
                return "schemaVersion is " + value.GetRawText() + ", not " + expected.Value.GetRawText();
            }

            if (GuidFields.Contains(name) && (!Guid.TryParse(value.GetString(), out Guid guid) || guid == Guid.Empty))
            {
                return name + " must be a GUID that is not all zeros";
            }

            if (TextFields.Contains(name) && string.IsNullOrWhiteSpace(value.GetString()))
            {
                return name + " must not be empty";
            }

            // Only a leading refs/ is refused, in any case, as the scanner checks it (Review Focus 3)
            if (name == "defaultBranch" && value.GetString()!.StartsWith("refs/", StringComparison.OrdinalIgnoreCase))
            {
                return "defaultBranch must be a branch name such as develop, not " + value.GetString();
            }

            return null;
        }
    }
}
