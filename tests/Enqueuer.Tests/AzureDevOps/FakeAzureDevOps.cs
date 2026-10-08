using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Enqueuer.AzureDevOps;
using Enqueuer.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Enqueuer.Tests.AzureDevOps
{
    // Stands in for Azure DevOps at the bottom of the HTTP client, beneath the real retry and timeout handler, so no
    // test reaches the network (Issue 2 design, Testing). Answers every request with the next queued response, then
    // with the last one again.
    internal sealed class FakeAzureDevOps : HttpMessageHandler
    {
        // The 7.1 reference's sample response for Repositories - List, with isDisabled and isInMaintenance added as
        // the live listing gives them. AnotherRepository has no defaultBranch, as an empty repository has none.
        public const string SampleListing = """
            {
              "count": 3,
              "value": [
                {
                  "id": "5febef5a-833d-4e14-b9c0-14cb638f91e6",
                  "name": "AnotherRepository",
                  "url": "https://dev.azure.com/fabrikam/_apis/git/repositories/5febef5a-833d-4e14-b9c0-14cb638f91e6",
                  "project": {
                    "id": "6ce954b1-ce1f-45d1-b94d-e6bf2464ba2c",
                    "name": "Fabrikam-Fiber-Git",
                    "state": "wellFormed"
                  },
                  "remoteUrl": "https://dev.azure.com/fabrikam/Fabrikam-Fiber-Git/_git/AnotherRepository",
                  "isDisabled": false,
                  "isInMaintenance": false
                },
                {
                  "id": "278d5cd2-584d-4b63-824a-2ba458937249",
                  "name": "Fabrikam-Fiber-Git",
                  "url": "https://dev.azure.com/fabrikam/_apis/git/repositories/278d5cd2-584d-4b63-824a-2ba458937249",
                  "project": {
                    "id": "6ce954b1-ce1f-45d1-b94d-e6bf2464ba2c",
                    "name": "Fabrikam-Fiber-Git",
                    "state": "wellFormed"
                  },
                  "defaultBranch": "refs/heads/master",
                  "size": 0,
                  "remoteUrl": "https://dev.azure.com/fabrikam/_git/Fabrikam-Fiber-Git",
                  "isDisabled": true,
                  "isInMaintenance": false
                },
                {
                  "id": "66efb083-777a-4cac-a350-a24b046be6be",
                  "name": "TestGit",
                  "url": "https://dev.azure.com/fabrikam/_apis/git/repositories/66efb083-777a-4cac-a350-a24b046be6be",
                  "project": {
                    "id": "281f9a5b-af0d-49b4-a1df-fe6f5e5f84d0",
                    "name": "TestGit",
                    "state": "wellFormed"
                  },
                  "defaultBranch": "refs/heads/master",
                  "remoteUrl": "https://dev.azure.com/fabrikam/_git/TestGit",
                  "isDisabled": false,
                  "isInMaintenance": true
                }
              ]
            }
            """;

        private readonly object _lock = new object();
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new Queue<(HttpStatusCode Status, string Body)>();
        private readonly List<SeenRequest> _requests = new List<SeenRequest>();
        private (HttpStatusCode Status, string Body) _last = (HttpStatusCode.OK, SampleListing);

        public IReadOnlyList<SeenRequest> Requests
        {
            get
            {
                lock (_lock)
                {
                    return _requests.ToList();
                }
            }
        }

        // Answers the next request with this status and body. Several calls answer several requests in turn.
        public void Respond(HttpStatusCode status, string body)
        {
            lock (_lock)
            {
                _responses.Enqueue((status, body));
            }
        }

        // Puts this fake in place of the network beneath the named client that AzureDevOpsRepositoryClient.Register
        // adds, with no wait between retries so the tests stay fast
        public void ReplaceAzureDevOps(IServiceCollection services)
        {
            services.AddHttpClient(AzureDevOpsRepositoryClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => this);
            services.Configure<HttpStandardResilienceOptions>(AzureDevOpsRepositoryClient.ResiliencePipelineName, options => options.Retry.Delay = TimeSpan.Zero);
        }

        // The real client, registered as Program registers it, with this fake beneath it
        public ServiceProvider BuildServices(string organizationUrl, string pat)
        {
            ServiceCollection services = new ServiceCollection();
            services.AddSingleton(Options.Create(new AzureDevOpsOptions { OrganizationUrl = organizationUrl, Pat = pat }));
            AzureDevOpsRepositoryClient.Register(services);
            ReplaceAzureDevOps(services);

            return services.BuildServiceProvider();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (HttpStatusCode Status, string Body) response;

            lock (_lock)
            {
                _requests.Add(new SeenRequest(
                    request.Method,
                    request.RequestUri!.AbsoluteUri,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-TFS-FedAuthRedirect", out IEnumerable<string>? redirect) ? string.Join(",", redirect) : null,
                    request.Headers.UserAgent.ToString()));

                if (_responses.Count > 0)
                {
                    _last = _responses.Dequeue();
                }

                response = _last;
            }

            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }

        // What the fake was sent: the parts the tests check
        internal sealed record SeenRequest(HttpMethod Method, string Url, string? Authorization, string? FedAuthRedirect, string UserAgent);
    }
}
