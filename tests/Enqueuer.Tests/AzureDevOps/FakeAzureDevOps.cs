using System;
using System.Collections.Generic;
using System.IO;
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

        private const string JsonContentType = "application/json; charset=utf-8";

        private readonly object _lock = new object();
        private readonly Queue<Answer> _answers = new Queue<Answer>();
        private readonly List<SeenRequest> _requests = new List<SeenRequest>();
        private Answer _last = new Answer(HttpStatusCode.OK, SampleListing, JsonContentType, false);
        private bool _unreachable;
        private bool _hanging;

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

        // Answers the next request with this status and body, as JSON unless another Content-Type is given. Several
        // calls answer several requests in turn.
        public void Respond(HttpStatusCode status, string body, string contentType = JsonContentType)
        {
            lock (_lock)
            {
                _answers.Enqueue(new Answer(status, body, contentType, false));
            }
        }

        // Answers the next request with 200 and the start of the sample listing, then drops the connection, as a
        // network that fails part way through the body
        public void RespondWithBrokenBody()
        {
            lock (_lock)
            {
                _answers.Enqueue(new Answer(HttpStatusCode.OK, SampleListing, JsonContentType, true));
            }
        }

        // Every request fails as if the network were down, as HttpClient reports a refused connection
        public void BeUnreachable()
        {
            lock (_lock)
            {
                _unreachable = true;
            }
        }

        // Every request waits until it is cancelled, as a server that never answers
        public void Hang()
        {
            lock (_lock)
            {
                _hanging = true;
            }
        }

        // Puts this fake in place of the network beneath the named client that AzureDevOpsRepositoryClient.Register
        // adds, with no wait between retries so the tests stay fast
        public void ReplaceAzureDevOps(IServiceCollection services)
        {
            services.AddHttpClient(AzureDevOpsRepositoryClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => this);
            services.Configure<HttpStandardResilienceOptions>(AzureDevOpsRepositoryClient.ResiliencePipelineName, options => options.Retry.Delay = TimeSpan.Zero);
        }

        // The real client, registered as Program registers it, with this fake beneath it. A test can change the retry
        // and timeout options further, such as shortening the timeouts.
        public ServiceProvider BuildServices(string organizationUrl, string pat, Action<HttpStandardResilienceOptions>? configureResilience = null)
        {
            ServiceCollection services = new ServiceCollection();
            services.AddSingleton(Options.Create(new AzureDevOpsOptions { OrganizationUrl = organizationUrl, Pat = pat }));
            AzureDevOpsRepositoryClient.Register(services);
            ReplaceAzureDevOps(services);

            if (configureResilience != null)
            {
                services.Configure(AzureDevOpsRepositoryClient.ResiliencePipelineName, configureResilience);
            }

            return services.BuildServiceProvider();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Answer answer;
            bool unreachable;
            bool hanging;

            lock (_lock)
            {
                unreachable = _unreachable;
                hanging = _hanging;

                _requests.Add(new SeenRequest(
                    request.Method,
                    request.RequestUri!.AbsoluteUri,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-TFS-FedAuthRedirect", out IEnumerable<string>? redirect) ? string.Join(",", redirect) : null,
                    request.Headers.UserAgent.ToString()));

                if (_answers.Count > 0)
                {
                    _last = _answers.Dequeue();
                }

                answer = _last;
            }

            if (unreachable)
            {
                throw new HttpRequestException("No connection could be made because the target machine actively refused it. (dev.azure.com:443)");
            }

            if (hanging)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            byte[] body = Encoding.UTF8.GetBytes(answer.Body);
            HttpContent content = answer.BreakBody
                ? new StreamContent(new BrokenStream(body, body.Length / 2))
                : new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", answer.ContentType);

            return new HttpResponseMessage(answer.Status)
            {
                Content = content,
                RequestMessage = request
            };
        }

        // What the fake was sent: the parts the tests check
        internal sealed record SeenRequest(HttpMethod Method, string Url, string? Authorization, string? FedAuthRedirect, string UserAgent);

        private sealed record Answer(HttpStatusCode Status, string Body, string ContentType, bool BreakBody);

        // Gives the first bytes of the body, then fails as a dropped connection does
        private sealed class BrokenStream : MemoryStream
        {
            private readonly int _breakAt;

            public BrokenStream(byte[] body, int breakAt)
                : base(body)
            {
                _breakAt = breakAt;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ThrowIfPastBreak();
                return base.Read(buffer, offset, Math.Min(count, _breakAt - (int)Position));
            }

            public override int Read(Span<byte> buffer)
            {
                ThrowIfPastBreak();
                return base.Read(buffer.Slice(0, Math.Min(buffer.Length, _breakAt - (int)Position)));
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult(Read(buffer.Span));
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromResult(Read(buffer, offset, count));
            }

            private void ThrowIfPastBreak()
            {
                if (Position >= _breakAt)
                {
                    throw new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.");
                }
            }
        }
    }
}
