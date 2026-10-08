using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Enqueuer.Tests.Queue
{
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

        // Each case breaks one rule: one of the scanner's value rules, or the enqueuer's stricter rule that the names are
        // exactly the example's, in its case and order, each once (Issue 3 design, decisions 3 and 5). The scanner
        // itself ignores case and extra fields. Exactly one problem, starting with the rule's own words, so no case can
        // pass through another rule by accident.
        [TestCase("\"develop\"", "\"refs/heads/develop\"", "defaultBranch")]
        [TestCase("\"develop\"", "\"Refs/Heads/develop\"", "defaultBranch")]
        [TestCase("\"develop\"", "\"  \"", "defaultBranch")]
        [TestCase("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c", "00000000-0000-0000-0000-000000000000", "correlationId")]
        [TestCase("0f8fad5b-d9cb-469f-a165-70867728950e", "not-a-guid", "repositoryId")]
        [TestCase("\"schemaVersion\":1", "\"schemaVersion\":2", "schemaVersion")]
        [TestCase("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", "schemaVersion")]
        [TestCase("\"Lumina\"", "\"\"", "projectName")]
        [TestCase("\"RepoScanner\"", "7", "repositoryName")]
        [TestCase("\"RepoScanner\"", "\" \"", "repositoryName")]
        [TestCase("\"schemaVersion\"", "\"SchemaVersion\"", "The fields are")]
        [TestCase(",\"defaultBranch\":\"develop\"", "", "The fields are")]
        [TestCase("{", "{\"priority\":1,", "The fields are")]
        [TestCase("}", ",\"defaultBranch\":\"develop\"}", "The fields are")]
        [TestCase("\"projectName\":\"Lumina\",\"repositoryName\":\"RepoScanner\"", "\"repositoryName\":\"RepoScanner\",\"projectName\":\"Lumina\"", "The fields are")]
        public void ProblemsWith_TheExampleWithOneThingChanged_GivesThatRulesProblem(string find, string replaceWith, string problemStart)
        {
            Assert.That(ScanRequestContract.ProblemsWith(Valid.Replace(find, replaceWith)), Has.Exactly(1).Items.And.All.StartsWith(problemStart));
        }

        // The scanner moves a Base64 body to the poison queue (RepoScanner Issue 7 design, decision 3)
        [Test]
        public void ProblemsWith_TheExampleInBase64_GivesNotJson()
        {
            Assert.That(ScanRequestContract.ProblemsWith(Convert.ToBase64String(Encoding.UTF8.GetBytes(Valid))), Is.EqualTo(new[] { "The body is not JSON" }));
        }

        // JSON, but not an object: the scanner refuses it before looking for a field
        [Test]
        public void ProblemsWith_AJsonArray_GivesNotAnObject()
        {
            Assert.That(ScanRequestContract.ProblemsWith("[]"), Is.EqualTo(new[] { "The body is not a JSON object" }));
        }
    }
}
