using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SemanticReranker.Core;

namespace SemanticReranker.Tests;

// HTTP contract tests use test doubles. They are not evidence of live Azure behavior.
public sealed class WebTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextPostRendersMetadataTimingsAndCorrectRerankState(bool rerank)
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        var html = await Post(client, SearchMode.FullText, rerank);
        Assert.Contains(rerank ? "Reranked results" : "Retrieval results", html);
        Assert.Contains("The Listening Vessel", html);
        Assert.Contains("synthetic-dotnet-ocean", html);
        Assert.Contains("Cosmos request charge (all pages)", html);
        Assert.Contains("RU", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("id=\"search-error\"", html);
    }

    [Fact]
    public async Task RerankerFailureRendersAlertWithoutSuccessTable()
    {
        using var app = new TestWebApp(failReranker: true);
        using var client = app.CreateClient();
        var html = await Post(client, SearchMode.FullText, true);
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("Reranking failed", html);
        Assert.DoesNotContain("id=\"results\"", html);
        Assert.DoesNotContain("private endpoint", html);
    }

    [Theory]
    [InlineData(SearchMode.Vector)]
    [InlineData(SearchMode.Hybrid)]
    public async Task MissingOpenAiIsVisibleAndDoesNotPretendSuccess(SearchMode mode)
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        var html = await Post(client, mode, false);
        Assert.Contains("AZURE_OPENAI_ENDPOINT", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.DoesNotContain("id=\"results\"", html);
    }

    [Fact]
    public async Task StartupAndHealthDoNotRequireAzureConfiguration()
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        Assert.Equal("healthy", await client.GetStringAsync("/healthz"));
        var html = await client.GetStringAsync("/");
        Assert.Contains("Cosmos DB semantic search", html);
        Assert.DoesNotContain("id=\"search-error\"", html);
    }

    [Fact]
    public async Task AntiforgeryIsNotDisabled()
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        using var response = await client.PostAsync("/", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["Query"] = "ocean", ["Mode"] = "FullText" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> Post(HttpClient client, SearchMode mode, bool rerank)
    {
        var page = await client.GetStringAsync("/");
        var match = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Razor must emit an antiforgery token.");
        using var response = await client.PostAsync("/", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
            ["Query"] = "ocean",
            ["Container"] = "qflat",
            ["Mode"] = mode.ToString(),
            ["AllKeywords"] = "true",
            ["Rerank"] = rerank.ToString()
        }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

internal sealed class TestWebApp(bool failReranker = false) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SemanticReranker.sln")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Cannot locate solution for HTTP tests.");
        builder.UseContentRoot(Path.Combine(directory.FullName, "src", "SemanticReranker.Web"));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<SampleSettings>();
            services.AddSingleton(new SampleSettings());
            services.RemoveAll<IQueryExecutor>();
            services.AddSingleton<IQueryExecutor>(new FakeQuery(
                [new("synthetic-dotnet-ocean", "The Listening Vessel", "<script>ocean</script>")]));
            services.RemoveAll<IEmbeddingProvider>();
            services.AddSingleton<IEmbeddingProvider, FailingEmbedding>();
            services.RemoveAll<IReranker>();
            services.AddSingleton<IReranker>(failReranker ? new FailingReranker() : new TestReranker());
        });
    }

    private sealed class TestReranker : IReranker
    {
        public Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<SearchRow> rows,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RerankScore>>([new(0, 0.9)]);
    }
}
