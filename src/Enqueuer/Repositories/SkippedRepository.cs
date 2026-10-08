namespace Enqueuer.Repositories
{
    // A listed repository that gets no scan request, and why
    public sealed record SkippedRepository(string ProjectName, string Name, SkipReason Reason);
}
