using Azure.Identity;
using SemanticReranker.Core;

namespace SemanticReranker.Tests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_LIVE_TESTS") != "1")
            Skip = "Set RUN_LIVE_TESTS=1 to run real Azure tests against explicitly provisioned .NET fixture containers.";
    }
}

// No writes in this suite. Run the guarded loader twice before enabling it.
public sealed class LiveTests
{
    private static SampleSettings Settings()
    {
        var settings = SampleSettings.FromEnvironment();
        settings.ValidateCosmos(); // enabled but missing configuration MUST fail, not skip
        SampleSettings.RequireHttps(settings.InferenceEndpoint, "INFERENCE_ENDPOINT");
        Assert.Equal("reranker_dotnet_qflat", settings.QflatContainer);
        Assert.Equal("reranker_dotnet_diskann", settings.DiskannContainer);
        return settings;
    }

    [LiveFact, Trait("Category", "Live")]
    public async Task ExactFixtureReadbackBothContainers()
    {
        using var store = new CosmosStore(Settings(), new DefaultAzureCredential());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var selector in new[] { "qflat", "diskann" })
        {
            await store.ValidateContainerAsync(selector, timeout.Token);
            foreach (var expected in TestData.Documents())
            {
                var actual = await store.ReadAsync(selector, expected.Id, timeout.Token);
                Assert.Equal(expected.Id, actual.Id);
                Assert.Equal(expected.Title, actual.Title);
                Assert.Equal(expected.Text, actual.Text);
                Assert.Equal(expected.Embedding, actual.Embedding);
            }
        }
    }

    [LiveFact, Trait("Category", "Live")]
    public async Task RealRetrievalAllModesAndStandaloneReranking()
    {
        var settings = Settings();
        var credential = new DefaultAzureCredential();
        using var store = new CosmosStore(settings, credential);
        var reranker = new AzureReranker(settings, credential);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        foreach (var selector in new[] { "qflat", "diskann" })
        {
            foreach (var mode in Enum.GetValues<SearchMode>())
            {
                var request = new SearchRequest("ocean marine", selector, mode, AllKeywords: false);
                // Synthetic basis vectors exercise query execution ONLY, not semantic embedding quality.
                var plan = QueryBuilder.Build(request,
                    mode is SearchMode.Vector or SearchMode.Hybrid ? TestData.Vector(0) : null);
                var result = await store.ExecuteAsync(selector, plan, timeout.Token);
                Assert.NotEmpty(result.Rows);
                Assert.True(result.RequestCharge > 0);
                Assert.All(result.Rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Id)));
            }

            var service = new SearchService(store, new FailingEmbedding(), reranker);
            var all = await service.SearchAsync(new("ocean marine", selector, SearchMode.FullText), timeout.Token);
            Assert.Contains(all.Rows, row => row.Id == "synthetic-dotnet-ocean");
            var ranked = await service.SearchAsync(new("ocean marine research whale", selector,
                SearchMode.FullTextRank, Rerank: true), timeout.Token);
            Assert.True(ranked.Reranked);
            Assert.Equal("synthetic-dotnet-ocean", ranked.Rows[0].Id);
            Assert.Equal("The Listening Vessel", ranked.Rows[0].Title);
            Assert.All(ranked.Rows, row => Assert.True(row.RerankScore.HasValue && double.IsFinite(row.RerankScore.Value)));
            Assert.Equal(ranked.Rows.Count, ranked.Rows.Select(row => row.Id).Distinct().Count());
            Assert.True(ranked.RerankMilliseconds > 0);
        }
    }

    [LiveFact, Trait("Category", "Live")]
    public async Task ActualInferenceReordersCandidatesAndPreservesOriginalMetadata()
    {
        var settings = Settings();
        var rows = TestData.Documents().Reverse().Select(d => new SearchRow(d.Id, d.Title, d.Text)).ToArray();
        var reranker = new AzureReranker(settings, new DefaultAzureCredential());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var scores = await reranker.RerankAsync("Scientists studying whales and ocean wildlife at sea", rows, timeout.Token);
        var ranked = RerankMapping.Apply(rows, scores);
        Assert.Equal("synthetic-dotnet-ocean", ranked[0].Id);
        Assert.Equal(3, ranked[0].RetrievalRank);
        Assert.Equal("The Listening Vessel", ranked[0].Title);
        Assert.Equal(3, ranked.Count);
    }
}
