using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Enqueuer.Queue;
using NUnit.Framework;

namespace Enqueuer.Tests.Queue
{
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
}
