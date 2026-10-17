using Azure.Identity;
using SemanticReranker.Core;

namespace SemanticReranker.Tests;

public sealed class OriginalMovieLiveTests
{
    [LiveFact, Trait("Category", "Live")]
    public async Task OriginalMoviesRealEmbeddingsEveryModeAndToggle()
    {
        var settings = SampleSettings.FromEnvironment();
        settings.ValidateCosmos();
        SampleSettings.RequireHttps(settings.OpenAiEndpoint, "AZURE_OPENAI_ENDPOINT");
        var credential = new DefaultAzureCredential();
        using var store = new CosmosStore(settings, credential);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var service = new SearchService(store, new OpenAiEmbeddings(settings, credential, http),
            new AzureReranker(settings, credential));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        foreach (var selector in new[] { "qflat", "diskann" })
        {
            await store.ValidateContainerAsync(selector, timeout.Token);
            var original = await store.ReadAsync(selector, "original-movies-v1-spectre", timeout.Token);
            Assert.Equal("The SPECTRE Signal", original.Title);
            QueryBuilder.ValidateVector(original.Embedding);
            foreach (var mode in Enum.GetValues<SearchMode>())
            foreach (var enabled in new[] { false, true })
            foreach (var all in mode == SearchMode.FullText ? new[] { false, true } : new[] { true })
            {
                var result = await service.SearchAsync(
                    new("SPECTRE spy", selector, mode, AllKeywords: all, Rerank: enabled), timeout.Token);
                Assert.NotEmpty(result.Rows);
                Assert.Equal(enabled, result.Reranked);
                Assert.True(result.RequestCharge > 0);
                Assert.Equal(result.Rows.Count, result.Rows.Select(row => row.Id).Distinct().Count());
                var position = result.Rows.ToList().FindIndex(row => row.Id == "original-movies-v1-spectre");
                Assert.True(position >= 0, $"SPECTRE missing: {selector}/{mode}/{enabled}/{all}");
                if (mode != SearchMode.FullText || enabled)
                    Assert.True(position < 3,
                        $"SPECTRE outside top 3: {selector}/{mode}/{enabled}; " +
                        $"SQL: {result.Sql}; IDs: {string.Join(", ", result.Rows.Select(row => row.Id))}");
                if (mode is SearchMode.Vector or SearchMode.Hybrid)
                    Assert.True(result.EmbeddingMilliseconds > 0);
                if (enabled)
                    Assert.All(result.Rows, row => Assert.True(
                        row.RerankScore.HasValue && double.IsFinite(row.RerankScore.Value)));
            }
        }
    }
}
