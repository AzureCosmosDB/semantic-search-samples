using Azure.Data.AI;
using SemanticReranker.Core;
using System.Text.Json;

namespace SemanticReranker.Tests;

public class CoreTests
{
    [Fact]
    public void CredentialComesFromConsolidatedCoreDependency()
    {
        Assert.Equal("Azure.Core", typeof(Azure.Identity.DefaultAzureCredential).Assembly.GetName().Name);
    }

    private static readonly SearchRow[] Rows =
    [
        new("a", "First", "identical text", 0.1),
        new("b", "Second", "identical text", 0.2)
    ];

    [Fact]
    public void RerankingUsesIndicesNotTextAndRetainsMetadata()
    {
        var actual = RerankMapping.Apply(Rows, [new(1, 0.9), new(0, 0.2)]);
        Assert.Equal("b", actual[0].Id);
        Assert.Equal("Second", actual[0].Title);
        Assert.Equal(0.2, actual[0].RetrievalScore);
        Assert.Equal(0.9, actual[0].RerankScore);
        Assert.Equal(2, actual[0].RetrievalRank);
        Assert.Equal("identical text", actual[0].Text);
    }

    [Fact]
    public void RejectsMalformedRerankingAndDoesNotReturnFallback()
    {
        RerankScore[][] invalid =
        [
            [new(null, 1), new(1, 0)],
            [new(-1, 1), new(1, 0)],
            [new(2, 1), new(1, 0)],
            [new(0, 1), new(0, 0)],
            [new(0, double.NaN), new(1, 0)],
            [new(0, double.PositiveInfinity), new(1, 0)],
            [new(0, null), new(1, 0)],
            [new(0, 1)],
            []
        ];
        foreach (var scores in invalid)
            Assert.Throws<SampleException>(() => RerankMapping.Apply(Rows, scores));
        Assert.Empty(RerankMapping.Apply([], []));
    }

    [Fact]
    public void ExactSdkContractMapsNullableIndexAndFloatScore()
    {
        var content = AzureReranker.CreateRequest("ocean research", Rows, "model");
        Assert.Equal(2, content.TopK);
        Assert.False(content.ReturnDocuments);
        Assert.Equal(SemanticRerankingDocumentType.Text, content.DocumentType);
        var result = InferenceModelFactory.SemanticRerankingInferenceResult(
            scores: [
                InferenceModelFactory.SemanticRerankingScore(index: 1, score: 0.75f),
                InferenceModelFactory.SemanticRerankingScore(index: 0, score: 0.25f)
            ]);
        var mapped = AzureReranker.MapResponse(result);
        Assert.Equal(1, mapped[0].Index);
        Assert.Equal(0.75, mapped[0].Score);
    }

    [Theory]
    [InlineData(SearchMode.Vector, "ORDER BY VectorDistance")]
    [InlineData(SearchMode.FullText, "FullTextContainsAll")]
    [InlineData(SearchMode.FullTextRank, "ORDER BY RANK FullTextScore")]
    [InlineData(SearchMode.Hybrid, "ORDER BY RANK RRF")]
    public void BuildsFourModes(SearchMode mode, string expected)
    {
        var plan = QueryBuilder.Build(new("ocean research", "qflat", mode, true, false),
            mode is SearchMode.Vector or SearchMode.Hybrid ? TestData.Vector(0) : null);
        Assert.Contains(expected, plan.Sql);
        Assert.Contains("SELECT TOP 10", plan.Sql);
        Assert.DoesNotContain("SELECT *", plan.Sql);
        Assert.DoesNotContain("1536", plan.Sql);
    }

    [Fact]
    public void AnyAndEscapedTermsCannotInjectSql()
    {
        var query = "ocean \" ) OR true --";
        var plan = QueryBuilder.Build(new(query, "diskann", SearchMode.FullText, false, false));
        Assert.Contains("FullTextContainsAny", plan.Sql);
        Assert.Contains(JsonSerializer.Serialize("\""), plan.Sql);
        Assert.Throws<SampleException>(() => QueryBuilder.Build(new(" ", "qflat", SearchMode.FullText)));
        Assert.Throws<SampleException>(() => QueryBuilder.Build(new("ok", "unknown", SearchMode.FullText)));
        Assert.Throws<SampleException>(() => QueryBuilder.Build(new("ok", "qflat", (SearchMode)99)));
        Assert.Throws<SampleException>(() => QueryBuilder.Build(new("ok", "qflat", SearchMode.Vector), [1]));
    }

    [Fact]
    public async Task TextDoesNotUseEmbeddingsOrDisabledReranker()
    {
        var executor = new FakeQuery(Rows);
        var service = new SearchService(executor, new FailingEmbedding(), new FailingReranker());
        var result = await service.SearchAsync(new("ocean", "qflat", SearchMode.FullText));
        Assert.Equal(2, result.Rows.Count);
        Assert.False(result.Reranked);
        Assert.Equal(1.25, result.RequestCharge);
        Assert.Equal(0, result.RerankMilliseconds);
    }

    [Fact]
    public async Task RerankerFailureIsAnErrorNotSuccess()
    {
        var service = new SearchService(new FakeQuery(Rows), new FailingEmbedding(), new FailingReranker());
        var ex = await Assert.ThrowsAsync<SampleException>(() =>
            service.SearchAsync(new("ocean", "qflat", SearchMode.FullText, Rerank: true)));
        Assert.Contains("Reranking failed", ex.Message);
        Assert.DoesNotContain("private", ex.Message);
    }

    [Fact]
    public async Task EmptyResultsDoNotInvokeReranker()
    {
        var service = new SearchService(new FakeQuery([]), new FailingEmbedding(), new FailingReranker());
        var result = await service.SearchAsync(new("none", "qflat", SearchMode.FullText, Rerank: true));
        Assert.Empty(result.Rows);
        Assert.False(result.Reranked);
    }

    [Fact]
    public async Task MissingOpenAiFailsOnlyWhenEmbeddingRequested()
    {
        using var http = new HttpClient();
        var embeddings = new OpenAiEmbeddings(new SampleSettings(), new Azure.Identity.DefaultAzureCredential(), http);
        var ex = await Assert.ThrowsAsync<SampleException>(() => embeddings.EmbedAsync("ocean", default));
        Assert.Contains("AZURE_OPENAI_ENDPOINT", ex.Message);
    }
}

internal sealed class FakeQuery(IReadOnlyList<SearchRow> rows) : IQueryExecutor
{
    public Task<QueryResult> ExecuteAsync(string container, QueryPlan plan, CancellationToken cancellationToken) =>
        Task.FromResult(new QueryResult(rows, 1.25, 2));
}
internal sealed class FailingEmbedding : IEmbeddingProvider
{
    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) =>
        throw new SampleException("Configure AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_EMBEDDING_DEPLOYMENT for vector/hybrid search.");
}
internal sealed class FailingReranker : IReranker
{
    public Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<SearchRow> rows, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("private endpoint and service details");
}
