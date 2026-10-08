using System;
using System.Collections.Generic;
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
using Polly.Timeout;

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
            IHttpClientBuilder client = services.AddHttpClient(HttpClientName, http =>
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Enqueuer");

                    // A refused PAT then gives 401, not a redirect to a sign-in page (decision 6)
                    http.DefaultRequestHeaders.Add("X-TFS-FedAuthRedirect", "Suppress");

                    // The resilience handler's total timeout below is the limit, not HttpClient's own 100 seconds
                    http.Timeout = Timeout.InfiniteTimeSpan;
                })
                // Never follow a redirect: it can only lead to the sign-in page, away from the organisation (decision 6)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

            client.AddStandardResilienceHandler(options =>
            {
                // The retries themselves are the handler's defaults: 3, exponential with jitter, honouring
                // Retry-After, for 5xx, 408, 429, network errors and timeouts (decision 9)
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);

                // The handler checks at start-up that this is at least twice the attempt timeout
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
            });

            // Added after the resilience handler, so it runs inside each try: the body is downloaded there, and a
            // connection dropped part way through it is retried (review finding 3)
            client.AddHttpMessageHandler(() => new BufferBodyHandler());

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

                using (HttpResponseMessage response = await SendAsync(http, request, cancellationToken))
                {
                    if (IsRefusal(response.StatusCode))
                    {
                        throw new AzureDevOpsAuthenticationException(
                            "Azure DevOps refused the token in Enqueuer:AzureDevOps:Pat for " + _options.OrganizationUrl
                            + ". Check that it has not expired or been revoked, and that it has Code (Read).");
                    }

                    // The status only, never the body, which is not ours to print (decision 8)
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new AzureDevOpsUnavailableException(
                            CouldNotList() + "Azure DevOps answered " + (int)response.StatusCode + " (" + response.ReasonPhrase + ").", null);
                    }

                    return await ReadAsync(response, cancellationToken);
                }
            }
        }

        // 401 and 403 are the refusal itself. 203 and a redirect are the sign-in page Azure DevOps sends a refused
        // token to, should it ever stop honouring X-TFS-FedAuthRedirect (decisions 6 and 7).
        private static bool IsRefusal(HttpStatusCode status)
        {
            int code = (int)status;

            return status == HttpStatusCode.Unauthorized
                || status == HttpStatusCode.Forbidden
                || status == HttpStatusCode.NonAuthoritativeInformation
                || (code >= 300 && code < 400);
        }

        // A network error, or a timeout after the retries, becomes AzureDevOpsUnavailableException. The job stopping
        // is not Azure DevOps' fault, so its cancellation goes through unchanged.
        private async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await http.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when ((exception is HttpRequestException || exception is TimeoutRejectedException) && !cancellationToken.IsCancellationRequested)
            {
                // Neither message holds a header, so the PAT stays out of it
                throw new AzureDevOpsUnavailableException(CouldNotList() + exception.Message, exception);
            }
        }

        // Every repository needs an id, a name and a project name, which Azure DevOps always sends. Without them the
        // answer is not one this code understands.
        private async Task<IReadOnlyList<AzureDevOpsRepository>> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            RepositoryList? list;
            try
            {
                list = await response.Content.ReadFromJsonAsync<RepositoryList>(JsonOptions, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException)
            {
                // Not JSON, or in a character set .NET cannot read (review finding 2). The parser's message can quote
                // the body, so it stays on the inner exception.
                throw NotARepositoryList(exception);
            }

            if (list?.Value == null)
            {
                throw NotARepositoryList(null);
            }

            List<AzureDevOpsRepository> repositories = new List<AzureDevOpsRepository>();
            foreach (RepositoryItem? item in list.Value)
            {
                Guid id = item?.Id ?? Guid.Empty;
                string? name = item?.Name;
                string? projectName = item?.Project?.Name;

                if (item == null || id == Guid.Empty || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(projectName))
                {
                    throw NotARepositoryList(null);
                }

                repositories.Add(new AzureDevOpsRepository(id, name, projectName, item.DefaultBranch, item.IsDisabled, item.IsInMaintenance));
            }

            return repositories;
        }

        private string CouldNotList()
        {
            return "Repositories could not be listed from " + _options.OrganizationUrl + ": ";
        }

        private AzureDevOpsUnavailableException NotARepositoryList(Exception? innerException)
        {
            return new AzureDevOpsUnavailableException(
                "Azure DevOps answered " + _options.OrganizationUrl + " with something other than a repository list.", innerException);
        }

        // HttpClient downloads the body after the handlers have returned, outside the retries. This downloads it
        // inside them instead, so a failure part way through is a failed try like any other network error.
        private sealed class BufferBodyHandler : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
                try
                {
                    await response.Content.LoadIntoBufferAsync(cancellationToken);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }

                return response;
            }
        }

        // The parts of Azure DevOps' response the enqueuer reads. Everything else, including count, is ignored. Each
        // is nullable, so a missing field is seen rather than read as an empty value.
        private sealed class RepositoryList
        {
            public List<RepositoryItem?>? Value { get; init; }
        }

        private sealed class RepositoryItem
        {
            public Guid? Id { get; init; }

            public string? Name { get; init; }

            public ProjectItem? Project { get; init; }

            public string? DefaultBranch { get; init; }

            public bool IsDisabled { get; init; }

            public bool IsInMaintenance { get; init; }
        }

        private sealed class ProjectItem
        {
            public string? Name { get; init; }
        }
    }
}
