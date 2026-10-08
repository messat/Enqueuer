using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Enqueuer.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Enqueuer.AzureDevOps
{
    // Lists every repository in the organisation with one call (Issue 2 design, The call)
    public sealed class AzureDevOpsRepositoryClient
    {
        public const string HttpClientName = "azure-devops";

        public const string ResiliencePipelineName = HttpClientName + "-standard";

        // Azure DevOps' JSON is camelCase
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AzureDevOpsOptions _options;

        public AzureDevOpsRepositoryClient(IHttpClientFactory httpClientFactory, IOptions<AzureDevOpsOptions> options)
        {
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
        }

        // The named HTTP client, with its headers, redirects off and its retries, and the client itself. Program and
        // the tests both use this, so the tests run through the real retry handler.
        public static void Register(IServiceCollection services)
        {
            services.AddHttpClient(HttpClientName, http =>
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Enqueuer");

                    // A refused PAT then gives 401, not a redirect to a sign-in page (decision 6)
                    http.DefaultRequestHeaders.Add("X-TFS-FedAuthRedirect", "Suppress");

                    // The resilience handler's total timeout below is the limit, not HttpClient's own 100 seconds
                    http.Timeout = Timeout.InfiniteTimeSpan;
                })
                // Never follow a redirect: it can only lead to the sign-in page, away from the organisation (decision 6)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                .AddStandardResilienceHandler(options =>
                {
                    // The retries themselves are the handler's defaults: 3, exponential with jitter, honouring
                    // Retry-After, for 5xx, 408, 429, network errors and timeouts (decision 9)
                    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);

                    // The handler checks at start-up that this is at least twice the attempt timeout
                    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                    options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
                });

            services.AddSingleton<AzureDevOpsRepositoryClient>();
        }

        // Every repository in every project, in the order Azure DevOps gives them (decision 1)
        public async Task<IReadOnlyList<AzureDevOpsRepository>> ListAsync(CancellationToken cancellationToken)
        {
            // No project in the path, so every project's repositories come back in one response
            string url = _options.OrganizationUrl.TrimEnd('/') + "/_apis/git/repositories?api-version=7.1";
            HttpClient http = _httpClientFactory.CreateClient(HttpClientName);

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                // A PAT is the password of Basic authentication, with an empty user name (decision 5). Set on this
                // request only, and never in the URL, so it reaches no log line or exception message.
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + _options.Pat)));

                using (HttpResponseMessage response = await http.SendAsync(request, cancellationToken))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new HttpRequestException(
                            "Azure DevOps answered " + (int)response.StatusCode + " (" + response.ReasonPhrase + ") when listing repositories from " + _options.OrganizationUrl,
                            null,
                            response.StatusCode);
                    }

                    RepositoryList? list = await response.Content.ReadFromJsonAsync<RepositoryList>(JsonOptions, cancellationToken);

                    return (list?.Value ?? new List<RepositoryItem>())
                        .Select(item => new AzureDevOpsRepository(item.Id, item.Name, item.Project.Name, item.DefaultBranch, item.IsDisabled, item.IsInMaintenance))
                        .ToList();
                }
            }
        }

        // The parts of Azure DevOps' response the enqueuer reads. Everything else, including count, is ignored.
        private sealed class RepositoryList
        {
            public List<RepositoryItem>? Value { get; init; }
        }

        private sealed class RepositoryItem
        {
            public Guid Id { get; init; }

            public string Name { get; init; } = string.Empty;

            public ProjectItem Project { get; init; } = new ProjectItem();

            public string? DefaultBranch { get; init; }

            public bool IsDisabled { get; init; }

            public bool IsInMaintenance { get; init; }
        }

        private sealed class ProjectItem
        {
            public string Name { get; init; } = string.Empty;
        }
    }
}
