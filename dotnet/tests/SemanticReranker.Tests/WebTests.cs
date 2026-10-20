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
    private static void AssertDatasetSummary(string html, string container = "reranker_dotnet_qflat",
        string database = "Not configured")
    {
        var summary = Regex.Match(html, "<section id=\"dataset-summary\"[\\s\\S]*?</section>");
        Assert.True(summary.Success, "Dataset summary must be outside the conditional search results.");
        Assert.Contains("Default source corpus: MovieLens", summary.Value);
        Assert.Contains("4,489 source rows", summary.Value);
        Assert.DoesNotContain("10 original movie plots", summary.Value);
        Assert.Contains("href=\"https://github.com/TheovanKraay/AzureDataRetrievalAugmentedGenerationSamples/blob/7fd162fc633617857f8b8ed2c5b2b92d4d8ef0b6/DataSet/Movies/MovieLens-4489-256D.json\"", summary.Value);
        Assert.Contains("Woody Buzz", summary.Value);
        Assert.Contains("fishing buddies", summary.Value);
        Assert.Contains("toys learning to share a child's affection", summary.Value);
        Assert.Contains("azd up", summary.Value);
        Assert.Contains("custom data", summary.Value);
        Assert.Contains("not a live inventory", summary.Value);
        Assert.Contains($"Configured database: <span>{database}</span>", summary.Value);
        Assert.Contains($"id=\"selected-container\">{container}</span>", summary.Value);
        Assert.DoesNotContain("<table", summary.Value);
        Assert.True(html.IndexOf("id=\"dataset-summary\"", StringComparison.Ordinal) >
                    html.IndexOf("<main", StringComparison.Ordinal));
        Assert.True(html.IndexOf("id=\"dataset-summary\"", StringComparison.Ordinal) <
                    html.IndexOf("aria-label=\"Search results\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DatasetSummaryUsesConfiguredNamesWithoutRetrievalAndTracksPostedSelection()
    {
        using var app = new TestWebApp(settings: new SampleSettings
        {
            Database = "custom-movies",
            QflatContainer = "custom-qflat",
            DiskannContainer = "custom-diskann",
            CosmosEndpoint = "https://private-endpoint.invalid",
            OpenAiKey = "private-key"
        });
        using var client = app.CreateClient();
        var page = await client.GetStringAsync("/");
        AssertDatasetSummary(page, "custom-qflat", "custom-movies");
        Assert.Null(app.LastPlan);
        Assert.Contains("data-container-name=\"custom-diskann\"", page);
        Assert.DoesNotContain("private-endpoint", page);
        Assert.DoesNotContain("private-key", page);
        var html = await Post(client, SearchMode.FullText, false, new() { ["Container"] = "diskann" });
        AssertDatasetSummary(html, "custom-diskann", "custom-movies");
        Assert.Equal("diskann", app.LastContainer);
    }

    [Fact]
    public async Task InvalidContainerDoesNotBreakDatasetSummary()
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        var html = await Post(client, SearchMode.FullText, false, new() { ["Container"] = "invalid" });
        AssertDatasetSummary(html, "Select a valid index");
        Assert.Contains("id=\"search-error\"", html);
        Assert.Null(app.LastPlan);
    }

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
        AssertDatasetSummary(html);
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
        AssertDatasetSummary(html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAdvancedFieldsKeepDefaultsIncludingDisabledJsonTargets(bool postVisibleDefaults)
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        // Old clients omit all advanced fields. A browser using plain-text defaults
        // omits the disabled TargetPaths select. Both must reach the same request.
        Dictionary<string, string>? fields = postVisibleDefaults ? new()
        {
            ["DocumentFormat"] = "text",
            ["RerankTopK"] = "10",
            ["ReturnSentenceScores"] = "false"
        } : null;
        var html = await Post(client, SearchMode.FullText, true, fields);
        Assert.DoesNotContain("id=\"search-error\"", html);
        Assert.Contains("Reranked results", html);
        Assert.Contains("Retrieved candidates: 2; returned results: 2", html);
        Assert.Equal(new RerankOptions(), app.LastRerankOptions);
        Assert.DoesNotContain("class=\"sentence-scores\"", html);
    }

    [Theory]
    [InlineData("DocumentFormat")]
    [InlineData("TargetPaths")]
    [InlineData("RerankTopK")]
    [InlineData("ReturnSentenceScores")]
    public async Task ExplicitEmptyAdvancedFieldIsNotTreatedAsOmitted(string field)
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        var html = await Post(client, SearchMode.FullText, true, new()
        {
            ["DocumentFormat"] = "json",
            [field] = ""
        });
        Assert.Contains("id=\"search-error\"", html);
        Assert.DoesNotContain("id=\"results\"", html);
        Assert.Null(app.LastRerankOptions);
    }

    [Fact]
    public async Task InvalidCheckboxBindingRendersActionableErrorWithoutCallingReranker()
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        var html = await Post(client, SearchMode.FullText, true, new()
        {
            ["DocumentFormat"] = "json",
            ["ReturnSentenceScores"] = "not-a-bool"
        });
        Assert.Contains("Sentence-score diagnostics must be true or false", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.DoesNotContain("id=\"results\"", html);
        Assert.DoesNotContain("not-a-bool", html);
        Assert.Null(app.LastRerankOptions);
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
        Assert.Contains("Azure Cosmos DB - Semantic Search Demo", html);
        Assert.DoesNotContain("id=\"search-error\"", html);
        Assert.Contains("<details id=\"advanced-reranking\">", html);
        // Native HTML must work without JavaScript. The enhancement disables
        // irrelevant controls only after load, and preserves their posted values.
        Assert.Matches("<fieldset(?=[^>]*id=\"rerank-options\")(?![^>]*disabled)[^>]*>", html);
        Assert.Matches("<input(?=[^>]*id=\"RerankTopK\")(?=[^>]*value=\"10\")[^>]*>", html);
        Assert.Contains("for=\"Query\">Search input</label>", html);
        Assert.Matches("<input(?=[^>]*id=\"Query\")(?=[^>]*type=\"text\")[^>]*>", html);
        Assert.DoesNotContain("<textarea", html);
        Assert.DoesNotContain("id=\"Mode\"", html);
        Assert.DoesNotMatch("<input[^>]*name=\"Mode\"", html);
        Assert.Matches("<input(?=[^>]*type=\"radio\")(?=[^>]*name=\"AllKeywords\")(?=[^>]*value=\"true\")[^>]*>", html);
        Assert.Matches("<input(?=[^>]*type=\"radio\")(?=[^>]*name=\"AllKeywords\")(?=[^>]*value=\"false\")[^>]*>", html);
        Assert.Contains("Choose a search to get started", html);
        AssertDatasetSummary(html);
        Assert.Null(app.LastPlan);
    }

    [Theory]
    [InlineData(SearchMode.Vector, "Similarity search", "ORDER BY VectorDistance")]
    [InlineData(SearchMode.FullTextRank, "Full text ranking", "ORDER BY RANK FullTextScore")]
    [InlineData(SearchMode.Hybrid, "Hybrid search", "ORDER BY RANK RRF")]
    [InlineData(SearchMode.FullText, "Full text search", "FullTextContainsAll")]
    public async Task NativeButtonPostsExactlyItsModeWithoutJavaScript(SearchMode mode, string label, string sql)
    {
        using var app = new TestWebApp(allowEmbeddings: true);
        using var client = app.CreateClient();
        var page = await client.GetStringAsync("/");
        Assert.Matches($"<button(?=[^>]*type=\"submit\")(?=[^>]*name=\"Mode\")(?=[^>]*value=\"{mode}\")[^>]*>{label}</button>", page);
        Assert.Equal(4, Regex.Matches(page, "<button[^>]*name=\"Mode\"").Count);
        var html = await Post(client, mode, false);
        Assert.DoesNotContain("id=\"search-error\"", html);
        Assert.Contains(sql, app.LastPlan?.Sql ?? "");
        Assert.Contains("id=\"results\"", html);
        AssertDatasetSummary(html);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Unknown", null)]
    [InlineData("99", null)]
    [InlineData("0", null)]
    [InlineData("Vector,Hybrid", null)]
    [InlineData("FullText", "Hybrid")]
    [InlineData("FullText", "FullText")]
    public async Task MissingInvalidOrDuplicateModeFailsBeforeRetrieval(string? mode, string? secondMode)
    {
        using var app = new TestWebApp(allowEmbeddings: true);
        using var client = app.CreateClient();
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Query", "ocean"), new("Container", "qflat"), new("Rerank", "true")
        };
        if (mode is not null) fields.Add(new("Mode", mode));
        if (secondMode is not null) fields.Add(new("Mode", secondMode));
        var html = await PostFields(client, fields);
        Assert.Contains("Choose one of the search buttons", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.DoesNotContain("id=\"results\"", html);
        Assert.Null(app.LastPlan);
        Assert.Null(app.LastRerankOptions);
    }

    [Fact]
    public async Task AnyKeywordsAndFormValuesSurviveSuccessAndServiceError()
    {
        foreach (var fail in new[] { false, true })
        {
            using var app = new TestWebApp(failReranker: fail);
            using var client = app.CreateClient();
            var html = await Post(client, SearchMode.FullText, true, new()
            {
                ["Query"] = "ocean research", ["Container"] = "diskann", ["AllKeywords"] = "false",
                ["DocumentFormat"] = "json", ["TargetPaths"] = "title",
                ["RerankTopK"] = "3", ["ReturnSentenceScores"] = "true"
            });
            Assert.Contains("FullTextContainsAny", app.LastPlan?.Sql ?? "");
            Assert.Equal("diskann", app.LastContainer);
            Assert.Matches("<input(?=[^>]*id=\"Query\")(?=[^>]*value=\"ocean research\")[^>]*>", html);
            Assert.Matches("<option(?=[^>]*value=\"diskann\")(?=[^>]*selected)[^>]*>", html);
            Assert.Matches("<input(?=[^>]*id=\"AnyKeywords\")(?=[^>]*checked)[^>]*>", html);
            Assert.Matches("<option(?=[^>]*value=\"json\")(?=[^>]*selected)[^>]*>", html);
            Assert.Matches("<option(?=[^>]*value=\"title\")(?=[^>]*selected)[^>]*>", html);
            Assert.Matches("<input(?=[^>]*id=\"RerankTopK\")(?=[^>]*value=\"3\")[^>]*>", html);
            Assert.Matches("<input(?=[^>]*id=\"ReturnSentenceScores\")(?=[^>]*checked)[^>]*>", html);
            Assert.Equal(fail, html.Contains("id=\"search-error\""));
        }
    }

    [Fact]
    public async Task RerankerOffRetainsValidAdvancedValuesWithoutUsingThem()
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        var html = await Post(client, SearchMode.FullText, false, new()
        {
            ["DocumentFormat"] = "json", ["TargetPaths"] = "title",
            ["RerankTopK"] = "3", ["ReturnSentenceScores"] = "true"
        });
        Assert.Contains("Retrieval results", html);
        Assert.Matches("<option(?=[^>]*value=\"json\")(?=[^>]*selected)[^>]*>", html);
        Assert.Matches("<option(?=[^>]*value=\"title\")(?=[^>]*selected)[^>]*>", html);
        Assert.Matches("<input(?=[^>]*id=\"RerankTopK\")(?=[^>]*value=\"3\")[^>]*>", html);
        Assert.Matches("<input(?=[^>]*id=\"ReturnSentenceScores\")(?=[^>]*checked)[^>]*>", html);
        Assert.Null(app.LastRerankOptions);
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

    [Theory]
    [InlineData("text", "title,text", false)]
    [InlineData("json", "title", true)]
    [InlineData("json", "text", false)]
    [InlineData("json", "title,text", true)]
    public async Task AdvancedOptionsRenderSubsetAndOptionalDetails(string format, string paths, bool diagnostics)
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        var html = await Post(client, SearchMode.FullText, true, new()
        {
            ["DocumentFormat"] = format, ["TargetPaths"] = paths,
            ["RerankTopK"] = "1", ["ReturnSentenceScores"] = diagnostics.ToString()
        });
        Assert.DoesNotContain("id=\"search-error\"", html);
        Assert.Contains("Retrieved candidates: 2; returned results: 1", html);
        Assert.Equal(diagnostics, html.Contains("class=\"sentence-scores\""));
        Assert.Equal(diagnostics, html.Contains("Service sentence index: 7; score:"));
    }

    [Theory]
    [InlineData("RerankTopK", "0")]
    [InlineData("RerankTopK", "11")]
    [InlineData("RerankTopK", "1.5")]
    [InlineData("DocumentFormat", "xml")]
    [InlineData("TargetPaths", "embedding")]
    [InlineData("ReturnSentenceScores", "not-a-bool")]
    public async Task CraftedAdvancedPostValidatesOnServerAndOffIgnores(string field, string value)
    {
        using var app = new TestWebApp();
        using var client = app.CreateClient();
        var options = new Dictionary<string, string> { ["DocumentFormat"] = "json", [field] = value };
        var enabled = await Post(client, SearchMode.FullText, true, options);
        Assert.Contains("id=\"search-error\"", enabled);
        Assert.DoesNotContain("id=\"results\"", enabled);
        var disabled = await Post(client, SearchMode.FullText, false, options);
        Assert.DoesNotContain("id=\"search-error\"", disabled);
        Assert.Contains("Retrieved candidates: 2; returned results: 2", disabled);
    }

    private static async Task<string> Post(HttpClient client, SearchMode mode, bool rerank,
        Dictionary<string, string>? advanced = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Query"] = "ocean",
            ["Container"] = "qflat",
            ["Mode"] = mode.ToString(),
            ["AllKeywords"] = "true",
            ["Rerank"] = rerank.ToString()
        };
        if (advanced is not null)
            foreach (var pair in advanced) fields[pair.Key] = pair.Value;
        return await PostFields(client, fields);
    }

    private static async Task<string> PostFields(HttpClient client, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var page = await client.GetStringAsync("/");
        var match = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Razor must emit an antiforgery token.");
        var submitted = fields.Append(new KeyValuePair<string, string>(
            "__RequestVerificationToken", WebUtility.HtmlDecode(match.Groups[1].Value)));
        using var response = await client.PostAsync("/", new FormUrlEncodedContent(submitted));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

internal sealed class TestWebApp(bool failReranker = false, bool allowEmbeddings = false,
    SampleSettings? settings = null) : WebApplicationFactory<Program>
{
    public RerankOptions? LastRerankOptions { get; private set; }
    public QueryPlan? LastPlan { get; private set; }
    public string? LastContainer { get; private set; }

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
            services.AddSingleton(settings ?? (allowEmbeddings
                ? new SampleSettings { OpenAiEndpoint = "https://example.invalid", OpenAiDeployment = "offline-test" }
                : new SampleSettings()));
            services.RemoveAll<IQueryExecutor>();
            services.AddSingleton<IQueryExecutor>(new CapturingQuery(
                [new("synthetic-dotnet-ocean", "The Listening Vessel", "<script>ocean</script>"),
                 new("synthetic-dotnet-other", "Other", "<script>ocean</script>")],
                (container, plan) => { LastContainer = container; LastPlan = plan; }));
            services.RemoveAll<IEmbeddingProvider>();
            services.AddSingleton<IEmbeddingProvider>(allowEmbeddings ? new TestEmbedding() : new FailingEmbedding());
            services.RemoveAll<IReranker>();
            services.AddSingleton<IReranker>(failReranker ? new FailingReranker()
                : new TestReranker(options => LastRerankOptions = options));
        });
    }

    private sealed class CapturingQuery(IReadOnlyList<SearchRow> rows, Action<string, QueryPlan> capture) : IQueryExecutor
    {
        public Task<QueryResult> ExecuteAsync(string container, QueryPlan plan, CancellationToken cancellationToken)
        {
            capture(container, plan);
            return Task.FromResult(new QueryResult(rows, 1.25, 2));
        }
    }

    private sealed class TestEmbedding : IEmbeddingProvider
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(TestData.Vector(0));
    }

    private sealed class TestReranker(Action<RerankOptions?> capture) : IReranker
    {
        public Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<SearchRow> rows,
            CancellationToken cancellationToken, RerankOptions? options = null)
        {
            capture(options);
            return Task.FromResult<IReadOnlyList<RerankScore>>(Enumerable.Range(0, Math.Min(options?.TopK ?? 10, rows.Count))
                .Select(i => new RerankScore(i, 0.9 - i / 10.0,
                    options?.ReturnSentenceScores == true ? [new(7, 0.75)] : null)).ToArray());
        }
    }
}
