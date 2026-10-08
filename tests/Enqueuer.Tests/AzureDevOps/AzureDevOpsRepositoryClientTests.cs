using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Enqueuer.AzureDevOps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Enqueuer.Tests.AzureDevOps
{
    // Through the real headers, retry and timeout handler that Register adds, with FakeAzureDevOps beneath it (#106)
    [TestFixture]
    public class AzureDevOpsRepositoryClientTests
    {
        // A fake PAT: values starting "your-" are placeholders to RepoScanner's self-scan
        private const string Pat = "your-test-pat-0001";

        private const string ListUrl = "https://dev.azure.com/BlacklightInternalsMo/_apis/git/repositories?api-version=7.1";

        private FakeAzureDevOps _azureDevOps = null!;
        private ServiceProvider _services = null!;

        [SetUp]
        public void SetUp()
        {
            _azureDevOps = new FakeAzureDevOps();
            _services = _azureDevOps.BuildServices("https://dev.azure.com/BlacklightInternalsMo", Pat);
        }

        [TearDown]
        public void TearDown()
        {
            _services.Dispose();
            _azureDevOps.Dispose();
        }

        // Criterion 1 and decision 1: every repository in every project, with one call and no project in the path
        [Test]
        public async Task ListAsync_SendsOneGetToTheOrganisationsRepositoryList()
        {
            await ListAsync();

            IReadOnlyList<FakeAzureDevOps.SeenRequest> requests = _azureDevOps.Requests;
            Assert.That(requests, Has.Count.EqualTo(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(requests[0].Method, Is.EqualTo(HttpMethod.Get));
                Assert.That(requests[0].Url, Is.EqualTo(ListUrl));
            }
        }

        [Test]
        public async Task ListAsync_WithATrailingSlashOnTheOrganisation_SendsTheSameAddress()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            using (ServiceProvider services = azureDevOps.BuildServices("https://dev.azure.com/BlacklightInternalsMo/", Pat))
            {
                await services.GetRequiredService<AzureDevOpsRepositoryClient>().ListAsync(CancellationToken.None);

                Assert.That(azureDevOps.Requests[0].Url, Is.EqualTo(ListUrl));
            }
        }

        // Decision 5: Basic, with an empty user name and the PAT as the password
        [Test]
        public async Task ListAsync_SendsThePatAsBasicAuthorization()
        {
            await ListAsync();

            string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat));
            Assert.That(_azureDevOps.Requests[0].Authorization, Is.EqualTo(expected));
        }

        // Criterion 4: the URL is logged by the HTTP client at Debug, and shows in exception messages
        [Test]
        public async Task ListAsync_KeepsThePatOutOfTheUrl()
        {
            await ListAsync();

            string url = _azureDevOps.Requests[0].Url;
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat));
            bool leaked = url.Contains(Pat, StringComparison.Ordinal) || url.Contains(base64, StringComparison.Ordinal);

            Assert.That(leaked, Is.False, "the PAT is in the URL");
        }

        // Decision 6: a refused PAT then gives 401, not a redirect to a sign-in page
        [Test]
        public async Task ListAsync_AsksAzureDevOpsNotToRedirectToSignIn()
        {
            await ListAsync();

            Assert.That(_azureDevOps.Requests[0].FedAuthRedirect, Is.EqualTo("Suppress"));
        }

        [Test]
        public async Task ListAsync_NamesItselfInTheUserAgent()
        {
            await ListAsync();

            Assert.That(_azureDevOps.Requests[0].UserAgent, Is.EqualTo("Enqueuer"));
        }

        // Every repository, in the order Azure DevOps gave, with only the fields the enqueuer reads
        [Test]
        public async Task ListAsync_TheSampleListing_GivesEveryRepositoryInOrder()
        {
            IReadOnlyList<AzureDevOpsRepository> repositories = await ListAsync();

            Assert.That(repositories, Is.EqualTo(new[]
            {
                new AzureDevOpsRepository(new Guid("5febef5a-833d-4e14-b9c0-14cb638f91e6"), "AnotherRepository", "Fabrikam-Fiber-Git", null, false, false),
                new AzureDevOpsRepository(new Guid("278d5cd2-584d-4b63-824a-2ba458937249"), "Fabrikam-Fiber-Git", "Fabrikam-Fiber-Git", "refs/heads/master", true, false),
                new AzureDevOpsRepository(new Guid("66efb083-777a-4cac-a350-a24b046be6be"), "TestGit", "TestGit", "refs/heads/master", false, true)
            }));
        }

        // Decision 12: a fork is listed like any other repository, so a secret committed only in a fork is still found
        [Test]
        public async Task ListAsync_AFork_IsListedLikeAnyOtherRepository()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, """
                {
                  "count": 1,
                  "value": [
                    {
                      "id": "0f8fad5b-d9cb-469f-a165-70867728950e",
                      "name": "victim-repo-fork",
                      "project": { "name": "Kudos Wall" },
                      "defaultBranch": "refs/heads/main",
                      "isFork": true,
                      "parentRepository": { "id": "5febef5a-833d-4e14-b9c0-14cb638f91e6", "name": "victim-repo", "isFork": false },
                      "isDisabled": false,
                      "isInMaintenance": false
                    }
                  ]
                }
                """);

            IReadOnlyList<AzureDevOpsRepository> repositories = await ListAsync();

            Assert.That(repositories, Is.EqualTo(new[]
            {
                new AzureDevOpsRepository(new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"), "victim-repo-fork", "Kudos Wall", "refs/heads/main", false, false)
            }));
        }

        [Test]
        public async Task ListAsync_AnEmptyListing_GivesNoRepositories()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, """{ "count": 0, "value": [] }""");

            IReadOnlyList<AzureDevOpsRepository> repositories = await ListAsync();

            Assert.That(repositories, Is.Empty);
        }

        // Decision 7: 401 and 403 are the refusal itself; 203 and a redirect are the sign-in page, should Azure DevOps
        // ever stop honouring X-TFS-FedAuthRedirect
        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        [TestCase(HttpStatusCode.NonAuthoritativeInformation)]
        [TestCase(HttpStatusCode.Found)]
        [TestCase(HttpStatusCode.MovedPermanently)]
        public void ListAsync_ARefusedToken_ThrowsAzureDevOpsAuthenticationException(HttpStatusCode status)
        {
            _azureDevOps.Respond(status, "<html>Sign in</html>");

            AzureDevOpsAuthenticationException? exception = Assert.ThrowsAsync<AzureDevOpsAuthenticationException>(ListAsync);

            Assert.That(exception?.Message, Is.EqualTo(
                "Azure DevOps refused the token in Enqueuer:AzureDevOps:Pat for https://dev.azure.com/BlacklightInternalsMo. "
                + "Check that it has not expired or been revoked, and that it has Code (Read)."));
        }

        // Decision 9: a refusal is not retried, so a broken PAT fails in seconds
        [Test]
        public void ListAsync_ARefusedToken_IsAskedOnce()
        {
            _azureDevOps.Respond(HttpStatusCode.Unauthorized, "{}");

            Assert.ThrowsAsync<AzureDevOpsAuthenticationException>(ListAsync);

            Assert.That(_azureDevOps.Requests, Has.Count.EqualTo(1));
        }

        // Decision 8: the status, never the body, which is not ours to print
        [Test]
        public void ListAsync_AnOrganisationThatDoesNotExist_ThrowsAzureDevOpsUnavailableException()
        {
            _azureDevOps.Respond(HttpStatusCode.NotFound, "<html>404 - Page not found</html>");

            AzureDevOpsUnavailableException? exception = Assert.ThrowsAsync<AzureDevOpsUnavailableException>(ListAsync);

            Assert.That(exception?.Message, Is.EqualTo(
                "Repositories could not be listed from https://dev.azure.com/BlacklightInternalsMo: Azure DevOps answered 404 (Not Found)."));
        }

        // Decision 9: a 5xx is retried, so one brief outage does not cost the night
        [Test]
        public async Task ListAsync_AFailureThatPasses_IsRetriedAndSucceeds()
        {
            _azureDevOps.Respond(HttpStatusCode.ServiceUnavailable, "");
            _azureDevOps.Respond(HttpStatusCode.OK, FakeAzureDevOps.SampleListing);

            IReadOnlyList<AzureDevOpsRepository> repositories = await ListAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(repositories, Has.Count.EqualTo(3));
                Assert.That(_azureDevOps.Requests, Has.Count.EqualTo(2));
            }
        }

        // The handler's default: 3 retries after the first try
        [Test]
        public void ListAsync_AFailureThatLasts_IsTriedFourTimesThenThrows()
        {
            _azureDevOps.Respond(HttpStatusCode.ServiceUnavailable, "");

            AzureDevOpsUnavailableException? exception = Assert.ThrowsAsync<AzureDevOpsUnavailableException>(ListAsync);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception?.Message, Does.EndWith("Azure DevOps answered 503 (Service Unavailable)."));
                Assert.That(_azureDevOps.Requests, Has.Count.EqualTo(4));
            }
        }

        [Test]
        public void ListAsync_TheNetworkDown_ThrowsAzureDevOpsUnavailableException()
        {
            _azureDevOps.BeUnreachable();

            AzureDevOpsUnavailableException? exception = Assert.ThrowsAsync<AzureDevOpsUnavailableException>(ListAsync);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception?.Message, Does.StartWith("Repositories could not be listed from https://dev.azure.com/BlacklightInternalsMo: "));
                Assert.That(exception?.Message, Does.Contain("actively refused it"));
                Assert.That(exception?.InnerException, Is.TypeOf<HttpRequestException>());
            }
        }

        // The resilience handler's total timeout, shortened here so the test does not wait 2 minutes
        [Test]
        public void ListAsync_NoAnswerInTime_ThrowsAzureDevOpsUnavailableException()
        {
            using (FakeAzureDevOps azureDevOps = new FakeAzureDevOps())
            using (ServiceProvider services = azureDevOps.BuildServices("https://dev.azure.com/BlacklightInternalsMo", Pat, ShortTimeouts))
            {
                azureDevOps.Hang();

                AzureDevOpsUnavailableException? exception = Assert.ThrowsAsync<AzureDevOpsUnavailableException>(
                    () => services.GetRequiredService<AzureDevOpsRepositoryClient>().ListAsync(CancellationToken.None));

                Assert.That(exception?.Message, Does.StartWith("Repositories could not be listed from https://dev.azure.com/BlacklightInternalsMo: "));
            }
        }

        // The job stopping is not Azure DevOps' fault: the cancellation goes through unchanged, as Issue 1 handles it
        [Test]
        public void ListAsync_WhenCancelled_ThrowsOperationCanceledException()
        {
            _azureDevOps.Hang();

            using (CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                Assert.That(
                    () => _services.GetRequiredService<AzureDevOpsRepositoryClient>().ListAsync(cancellation.Token),
                    Throws.InstanceOf<OperationCanceledException>());
            }
        }

        // Decision 8: the parser's message, which can quote the body, stays on the inner exception
        [TestCase("<html>Sign in</html>")]
        [TestCase("null")]
        [TestCase("""{ "count": 0 }""")]
        [TestCase("""{ "value": [ { "name": "victim-repo", "project": { "name": "Kudos Wall" } } ] }""")]
        [TestCase("""{ "value": [ { "id": "0f8fad5b-d9cb-469f-a165-70867728950e", "project": { "name": "Kudos Wall" } } ] }""")]
        [TestCase("""{ "value": [ { "id": "0f8fad5b-d9cb-469f-a165-70867728950e", "name": "victim-repo" } ] }""")]
        [TestCase("""{ "value": [ { "id": "0f8fad5b-d9cb-469f-a165-70867728950e", "name": "victim-repo", "project": { } } ] }""")]
        public void ListAsync_SomethingOtherThanARepositoryList_ThrowsAzureDevOpsUnavailableException(string body)
        {
            _azureDevOps.Respond(HttpStatusCode.OK, body);

            AzureDevOpsUnavailableException? exception = Assert.ThrowsAsync<AzureDevOpsUnavailableException>(ListAsync);

            Assert.That(exception?.Message, Is.EqualTo(
                "Azure DevOps answered https://dev.azure.com/BlacklightInternalsMo with something other than a repository list."));
        }

        // Review finding 2: a body in a character set .NET cannot read is not a repository list either, and must give
        // the same one line rather than a stack trace
        [Test]
        public void ListAsync_ABodyInAnUnknownCharacterSet_ThrowsAzureDevOpsUnavailableException()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, FakeAzureDevOps.SampleListing, "application/json; charset=no-such-charset");

            AzureDevOpsUnavailableException? exception = Assert.ThrowsAsync<AzureDevOpsUnavailableException>(ListAsync);

            Assert.That(exception?.Message, Is.EqualTo(
                "Azure DevOps answered https://dev.azure.com/BlacklightInternalsMo with something other than a repository list."));
        }

        // Review finding 3: a connection dropped part way through the body is retried like any other network error
        [Test]
        public async Task ListAsync_ABodyCutOffPartWay_IsRetriedAndSucceeds()
        {
            _azureDevOps.RespondWithBrokenBody();
            _azureDevOps.Respond(HttpStatusCode.OK, FakeAzureDevOps.SampleListing);

            IReadOnlyList<AzureDevOpsRepository> repositories = await ListAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(repositories, Has.Count.EqualTo(3));
                Assert.That(_azureDevOps.Requests, Has.Count.EqualTo(2));
            }
        }

        // Criterion 4: no failure's message holds the PAT, plain or in Base64
        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.NotFound)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public void ListAsync_AFailure_NeverPutsThePatInTheMessage(HttpStatusCode status)
        {
            _azureDevOps.Respond(status, "");

            Exception? exception = Assert.CatchAsync(ListAsync);
            string text = exception?.ToString() ?? string.Empty;
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat));
            bool leaked = text.Contains(Pat, StringComparison.Ordinal) || text.Contains(base64, StringComparison.Ordinal);

            Assert.That(leaked, Is.False, "the PAT is in the exception");
        }

        // Decision 6: the client never follows a redirect, so it never lands on the sign-in page, and the request
        // never leaves the organisation's address. Checked on the real primary handler, without the fake.
        [Test]
        public void Register_ThePrimaryHandler_DoesNotFollowRedirects()
        {
            ServiceCollection services = new ServiceCollection();
            AzureDevOpsRepositoryClient.Register(services);

            using (ServiceProvider provider = services.BuildServiceProvider())
            {
                HttpClientFactoryOptions options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                    .Get(AzureDevOpsRepositoryClient.HttpClientName);
                HttpMessageHandlerBuilder builder = provider.GetRequiredService<HttpMessageHandlerBuilder>();
                builder.Name = AzureDevOpsRepositoryClient.HttpClientName;

                foreach (Action<HttpMessageHandlerBuilder> action in options.HttpMessageHandlerBuilderActions)
                {
                    action(builder);
                }

                Assert.That(builder.PrimaryHandler, Is.TypeOf<SocketsHttpHandler>());
                Assert.That(((SocketsHttpHandler)builder.PrimaryHandler).AllowAutoRedirect, Is.False);
            }
        }

        // Decision 9: the same limits as RepoScanner's nuget.org client
        [Test]
        public void Register_TheRetryOptionsTheTestsChange_AreTheClientsOwn()
        {
            HttpStandardResilienceOptions options = _services.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
                .Get(AzureDevOpsRepositoryClient.ResiliencePipelineName);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(options.Retry.Delay, Is.EqualTo(TimeSpan.Zero), "set by FakeAzureDevOps.ReplaceAzureDevOps");
                Assert.That(options.AttemptTimeout.Timeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
                Assert.That(options.TotalRequestTimeout.Timeout, Is.EqualTo(TimeSpan.FromMinutes(2)));
            }
        }

        // Each try gives up after 100 ms and the whole call after 300 ms. The handler insists the sampling duration
        // is at least twice the try's timeout.
        private static void ShortTimeouts(HttpStandardResilienceOptions options)
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(100);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMilliseconds(300);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(1);
        }

        private Task<IReadOnlyList<AzureDevOpsRepository>> ListAsync()
        {
            return _services.GetRequiredService<AzureDevOpsRepositoryClient>().ListAsync(CancellationToken.None);
        }
    }
}
