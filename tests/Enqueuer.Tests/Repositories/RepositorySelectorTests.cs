using System;
using Enqueuer.AzureDevOps;
using Enqueuer.Repositories;
using NUnit.Framework;

namespace Enqueuer.Tests.Repositories
{
    // Which listed repositories are scanned, and why the others are skipped (Issue 2 design, decisions 10 to 13)
    [TestFixture]
    public class RepositorySelectorTests
    {
        private static readonly Guid Id = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");

        [Test]
        public void Select_AnActiveRepositoryWithABranch_IsScannedWithoutRefsHeads()
        {
            RepositorySelection selection = RepositorySelector.Select(new[] { Repository("victim-repo", "refs/heads/main") });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(selection.ToScan, Is.EqualTo(new[] { new RepositoryToScan(Id, "Kudos Wall", "victim-repo", "main") }));
                Assert.That(selection.Skipped, Is.Empty);
            }
        }

        [Test]
        public void Select_ADisabledRepository_IsSkippedAsDisabled()
        {
            RepositorySelection selection = RepositorySelector.Select(new[] { Repository("old-repo", "refs/heads/main", isDisabled: true) });

            AssertSkippedOnly(selection, new SkippedRepository("Kudos Wall", "old-repo", SkipReason.Disabled));
        }

        [Test]
        public void Select_ARepositoryInMaintenance_IsSkippedAsInMaintenance()
        {
            RepositorySelection selection = RepositorySelector.Select(new[] { Repository("busy-repo", "refs/heads/main", isInMaintenance: true) });

            AssertSkippedOnly(selection, new SkippedRepository("Kudos Wall", "busy-repo", SkipReason.InMaintenance));
        }

        // Azure DevOps gives an empty repository no default branch (decision 10)
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("refs/heads/")]
        public void Select_ARepositoryWithNoBranch_IsSkippedAsEmpty(string? defaultBranch)
        {
            RepositorySelection selection = RepositorySelector.Select(new[] { Repository("new-repo", defaultBranch) });

            AssertSkippedOnly(selection, new SkippedRepository("Kudos Wall", "new-repo", SkipReason.Empty));
        }

        // The checks run in the design's order, and the first that applies is the one reason
        [Test]
        public void Select_ARepositoryBothDisabledAndEmpty_IsSkippedOnceAsDisabled()
        {
            RepositorySelection selection = RepositorySelector.Select(new[] { Repository("dead-repo", null, isDisabled: true, isInMaintenance: true) });

            AssertSkippedOnly(selection, new SkippedRepository("Kudos Wall", "dead-repo", SkipReason.Disabled));
        }

        [Test]
        public void Select_ARepositoryInMaintenanceAndEmpty_IsSkippedOnceAsInMaintenance()
        {
            RepositorySelection selection = RepositorySelector.Select(new[] { Repository("busy-repo", null, isInMaintenance: true) });

            AssertSkippedOnly(selection, new SkippedRepository("Kudos Wall", "busy-repo", SkipReason.InMaintenance));
        }

        // Only the prefix is removed, so a branch with a slash in its name keeps the rest (decision 13)
        [TestCase("refs/heads/feature/x", "feature/x")]
        [TestCase("refs/heads/refs/heads/odd", "refs/heads/odd")]
        [TestCase("develop", "develop")]
        public void Select_TheDefaultBranch_LosesOnlyALeadingRefsHeads(string defaultBranch, string expected)
        {
            RepositorySelection selection = RepositorySelector.Select(new[] { Repository("victim-repo", defaultBranch) });

            Assert.That(selection.ToScan[0].DefaultBranch, Is.EqualTo(expected));
        }

        // Git ref names match case exactly, so "Refs/Heads/" is not the prefix
        [Test]
        public void Select_TheDefaultBranch_MatchesRefsHeadsCaseExactly()
        {
            RepositorySelection selection = RepositorySelector.Select(new[] { Repository("victim-repo", "Refs/Heads/main") });

            Assert.That(selection.ToScan[0].DefaultBranch, Is.EqualTo("Refs/Heads/main"));
        }

        [Test]
        public void Select_SeveralRepositories_KeepsTheListingsOrderInBothLists()
        {
            RepositorySelection selection = RepositorySelector.Select(new[]
            {
                Repository("b-repo", "refs/heads/main"),
                Repository("z-empty", null),
                Repository("a-repo", "refs/heads/develop"),
                Repository("a-disabled", "refs/heads/main", isDisabled: true)
            });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(selection.ToScan, Has.Count.EqualTo(2));
                Assert.That(selection.ToScan[0].Name, Is.EqualTo("b-repo"));
                Assert.That(selection.ToScan[1].Name, Is.EqualTo("a-repo"));
                Assert.That(selection.Skipped, Has.Count.EqualTo(2));
                Assert.That(selection.Skipped[0].Name, Is.EqualTo("z-empty"));
                Assert.That(selection.Skipped[1].Name, Is.EqualTo("a-disabled"));
            }
        }

        [Test]
        public void Select_NothingListed_GivesTwoEmptyLists()
        {
            RepositorySelection selection = RepositorySelector.Select(Array.Empty<AzureDevOpsRepository>());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(selection.ToScan, Is.Empty);
                Assert.That(selection.Skipped, Is.Empty);
            }
        }

        private static AzureDevOpsRepository Repository(string name, string? defaultBranch, bool isDisabled = false, bool isInMaintenance = false)
        {
            return new AzureDevOpsRepository(Id, name, "Kudos Wall", defaultBranch, isDisabled, isInMaintenance);
        }

        private static void AssertSkippedOnly(RepositorySelection selection, SkippedRepository expected)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(selection.ToScan, Is.Empty);
                Assert.That(selection.Skipped, Is.EqualTo(new[] { expected }));
            }
        }
    }
}
