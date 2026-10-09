using System;
using System.IO;

namespace Enqueuer.Tests
{
    // The folder holding Enqueuer.sln, found by walking up from the test's output folder, so a test can read files in
    // the repository wherever it is checked out (Issue 3 design, decision 6)
    internal static class RepositoryRoot
    {
        public static string FullPath { get; } = Find();

        private static string Find()
        {
            DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder != null && !File.Exists(Path.Combine(folder.FullName, "Enqueuer.sln")))
            {
                folder = folder.Parent;
            }

            if (folder == null)
            {
                throw new InvalidOperationException("Enqueuer.sln was not found above " + AppContext.BaseDirectory);
            }

            return folder.FullName;
        }
    }
}
