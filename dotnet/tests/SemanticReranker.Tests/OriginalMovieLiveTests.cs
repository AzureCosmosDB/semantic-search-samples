using Azure.Identity;
using SemanticReranker.Core;

namespace SemanticReranker.Tests;

public sealed class OriginalMovieLiveTests
{
    [LiveFact, Trait("Category", "Live")]
    public async Task AdvancedRerankingSubsetsBothIndexes()
    {
        var settings = SampleSettings.FromEnvironment();
        settings.ValidateCosmos();
        var credential = new DefaultAzureCredential();
        using var store = new CosmosStore(settings, credential);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var service = new SearchService(store, new OpenAiEmbeddings(settings, credential, http),
            new AzureReranker(settings, credential));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        RerankOptions[] cases =
        [
            new("text", "title,text", 1), new("text", "title,text", 3, true),
            new("json", "title", 1, true), new("json", "text", 3, true),
            new("json", "title,text", 10, true), new("json", "title,text", 3)
        ];
        foreach (var selector in new[] { "qflat", "diskann" })
        {
            var request = new SearchRequest("Woody Buzz", selector, SearchMode.Vector);
            var baseline = await service.SearchAsync(request, timeout.Token);
            Assert.NotEmpty(baseline.Rows);
            foreach (var options in cases)
            {
                var off = await service.SearchAsync(request with { RerankOptions = options }, timeout.Token);
                Assert.Equal(baseline.Rows.Select(r => r.Id), off.Rows.Select(r => r.Id));
                Assert.False(off.Reranked);
                Assert.False(off.SentenceScoresRequested);
                var result = await service.SearchAsync(
                    request with { Rerank = true, RerankOptions = options }, timeout.Token);
                Assert.True(result.Reranked);
                Assert.Equal(Math.Min(options.TopK, result.RetrievedCount), result.Rows.Count);
                Assert.Equal(result.Rows.Count, result.Rows.Select(r => r.RetrievalIndex).Distinct().Count());
                foreach (var row in result.Rows)
                {
                    Assert.NotNull(row.RetrievalIndex);
                    var original = baseline.Rows[row.RetrievalIndex.Value];
                    Assert.Equal(original.Id, row.Id);
                    Assert.Equal(original.Title, row.Title);
                    Assert.Equal(original.Text, row.Text);
                    Assert.Equal(row.RetrievalIndex.Value + 1, row.RetrievalRank);
                    Assert.True(row.RerankScore.HasValue && double.IsFinite(row.RerankScore.Value));
                    if (options.ReturnSentenceScores)
                        foreach (var sentence in row.SentenceScores ?? [])
                        {
                            Assert.True(sentence.Index is >= 0);
                            Assert.True(sentence.Score.HasValue && double.IsFinite(sentence.Score.Value));
                        }
                    else Assert.Null(row.SentenceScores);
                }
            }
        }
    }

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
            var original = await store.ReadAsync(selector, "movielens-4489-v1-0001-862", timeout.Token);
            Assert.Equal("Toy Story", original.Title);
            QueryBuilder.ValidateVector(original.Embedding);
            foreach (var mode in Enum.GetValues<SearchMode>())
            foreach (var enabled in new[] { false, true })
            foreach (var all in mode == SearchMode.FullText ? new[] { false, true } : new[] { true })
            {
                var result = await service.SearchAsync(
                    new("Woody Buzz", selector, mode, AllKeywords: all, Rerank: enabled), timeout.Token);
                Assert.NotEmpty(result.Rows);
                Assert.Equal(enabled, result.Reranked);
                Assert.True(result.RequestCharge > 0);
                Assert.Equal(result.Rows.Count, result.Rows.Select(row => row.Id).Distinct().Count());
                Assert.InRange(result.Rows.Count, 1, 10); // Candidate limit, not dataset count.
                var position = result.Rows.ToList().FindIndex(row => row.Id == "movielens-4489-v1-0001-862");
                // Any-keyword filtering can match more than ten rows and has no ordering.
                if (mode != SearchMode.FullText || all)
                    Assert.True(position >= 0, $"Toy Story missing: {selector}/{mode}/{enabled}/{all}");
                if (mode != SearchMode.FullText)
                    Assert.True(position < 3,
                        $"Toy Story outside top 3: {selector}/{mode}/{enabled}; " +
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
