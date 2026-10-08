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

        [Test]
        public async Task ListAsync_AnEmptyListing_GivesNoRepositories()
        {
            _azureDevOps.Respond(HttpStatusCode.OK, """{ "count": 0, "value": [] }""");

            IReadOnlyList<AzureDevOpsRepository> repositories = await ListAsync();

            Assert.That(repositories, Is.Empty);
        }

        // Until #108 gives the failures their own exceptions, any answer but 200 fails the call
        [Test]
        public void ListAsync_AnyAnswerButOk_Throws()
        {
            _azureDevOps.Respond(HttpStatusCode.NotFound, "Not found");

            Assert.ThrowsAsync<HttpRequestException>(ListAsync);
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

        private Task<IReadOnlyList<AzureDevOpsRepository>> ListAsync()
        {
            return _services.GetRequiredService<AzureDevOpsRepositoryClient>().ListAsync(CancellationToken.None);
        }
    }
}
