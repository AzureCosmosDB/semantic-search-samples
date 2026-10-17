using Azure.Data.AI;
using SemanticReranker.Core;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace SemanticReranker.Tests;

public sealed class AdvancedRerankingTests
{
    private static readonly SearchRow[] Rows =
    [
        new("a", "First", "duplicate text", 0.1),
        new("b", "Second", "duplicate text", 0.2),
        new("c", "Third", "duplicate text", 0.3)
    ];

    [Fact]
    public void DefaultRequestKeepsPlainTextAndDoesNotRequestDiagnostics()
    {
        var content = AzureReranker.CreateRequest("query", Rows, "model");
        using var json = JsonDocument.Parse(ModelReaderWriter.Write(content).ToString());
        var root = json.RootElement;
        Assert.Equal("text", root.GetProperty("documentType").GetString());
        Assert.Equal(3, root.GetProperty("topK").GetInt32());
        Assert.True(root.GetProperty("sort").GetBoolean());
        Assert.False(root.GetProperty("returnDocuments").GetBoolean());
        Assert.False(root.TryGetProperty("targetPaths", out _));
        Assert.False(root.TryGetProperty("returnSentenceScore", out _));
        Assert.All(root.GetProperty("documents").EnumerateArray(),
            document => Assert.Equal("duplicate text", document.GetString()));
    }

    [Fact]
    public void NativeResponseMapsOnlyRequestedSentenceDiagnostics()
    {
        var result = ModelReaderWriter.Read<SemanticRerankingInferenceResult>(BinaryData.FromString(
            """{"scores":[{"index":2,"score":0.9,"document":"ignored","sentenceScores":[{"index":7,"score":0.75}]}]}"""))!;
        var score = Assert.Single(AzureReranker.MapResponse(result, true));
        Assert.Equal(2, score.Index);
        Assert.Equal(7, Assert.Single(score.SentenceScores!).Index);
        Assert.Equal(0.75, score.SentenceScores![0].Score);
        Assert.Null(Assert.Single(AzureReranker.MapResponse(result)).SentenceScores);
        var absent = ModelReaderWriter.Read<SemanticRerankingInferenceResult>(BinaryData.FromString(
            """{"scores":[{"index":0,"score":0.9}]}"""))!;
        var mapped = Assert.Single(AzureReranker.MapResponse(absent, true));
        Assert.True(mapped.SentenceScores is null or { Count: 0 });
    }

    [Theory]
    [InlineData("title", 1)]
    [InlineData("text", 3)]
    [InlineData("title,text", 10)]
    public void NativeRequestSerializesOnlySelectedFields(string paths, int k)
    {
        var options = new RerankOptions("json", paths, k, true);
        var content = AzureReranker.CreateRequest("query", Rows, "model", options);
        using var json = JsonDocument.Parse(ModelReaderWriter.Write(content).ToString());
        var root = json.RootElement;
        Assert.Equal("json", root.GetProperty("documentType").GetString());
        Assert.Equal(paths, root.GetProperty("targetPaths").GetString());
        Assert.Equal(Math.Min(k, 3), root.GetProperty("topK").GetInt32());
        Assert.True(root.GetProperty("returnSentenceScore").GetBoolean());
        Assert.False(root.GetProperty("returnDocuments").GetBoolean());
        var documents = root.GetProperty("documents").EnumerateArray().ToArray();
        Assert.Equal(3, documents.Length);
        for (var i = 0; i < documents.Length; i++)
        {
            using var document = JsonDocument.Parse(documents[i].GetString()!);
            Assert.Equal(paths.Split(','), document.RootElement.EnumerateObject().Select(p => p.Name));
            foreach (var path in paths.Split(','))
                Assert.Equal(path == "title" ? Rows[i].Title : Rows[i].Text,
                    document.RootElement.GetProperty(path).GetString());
        }
        Assert.Equal(1536, QueryBuilder.Dimensions);
    }

    [Fact]
    public void SubsetKeepsOriginalMetadataAndServiceSentenceIndices()
    {
        var mapped = RerankMapping.Apply(Rows,
            [new(2, 0.9, [new(7, 0.7), new(9, 0.2)])], 1, true);
        var row = Assert.Single(mapped);
        Assert.Equal("c", row.Id);
        Assert.Equal("Third", row.Title);
        Assert.Equal("duplicate text", row.Text);
        Assert.Equal(0.3, row.RetrievalScore);
        Assert.Equal(3, row.RetrievalRank);
        Assert.Equal(2, row.RetrievalIndex);
        Assert.Equal(7, row.SentenceScores![0].Index);
        Assert.Equal(9, row.SentenceScores![1].Index);
        Assert.Null(Assert.Single(RerankMapping.Apply(Rows, [new(2, 1)], 1, true)).SentenceScores);
        Assert.Null(Assert.Single(RerankMapping.Apply(Rows,
            [new(2, 1, [new(0, 1)])], 1)).SentenceScores);
    }

    [Fact]
    public void SubsetAndDiagnosticsRejectMalformedPayloads()
    {
        RerankScore[][] invalid =
        [
            [], [new(0, 1), new(1, 0)], [new(3, 1)], [new(-1, 1)],
            [new(null, 1)], [new(0, null)], [new(0, double.NaN)],
            [new(0, double.PositiveInfinity)],
            [new(0, 1, [new(-1, 1)])], [new(0, 1, [new(0, double.NaN)])],
            [new(0, 1, [new(null, 1)])], [new(0, 1, [new(0, null)])],
            [new(0, 1, [new(0, 1), new(0, 0)])]
        ];
        foreach (var scores in invalid)
            Assert.Throws<SampleException>(() => RerankMapping.Apply(Rows, scores, 1, true));
        Assert.Throws<SampleException>(() => RerankMapping.Apply(Rows, [new(0, 1), new(0, 0)], 2));
    }

    [Theory]
    [InlineData("xml", "title,text", 1)]
    [InlineData("json", "", 1)]
    [InlineData("json", "embedding", 1)]
    [InlineData("json", "title,title", 1)]
    [InlineData("text", "title,text", 0)]
    [InlineData("text", "title,text", 11)]
    public async Task InvalidOptionsRejectedOnlyWhenEnabled(string format, string paths, int k)
    {
        var options = new RerankOptions(format, paths, k);
        var request = new SearchRequest("query", "qflat", SearchMode.FullText, Rerank: true,
            RerankOptions: options);
        Assert.Throws<SampleException>(() => QueryBuilder.Validate(request));
        var service = new SearchService(new FakeQuery(Rows), new FailingEmbedding(), new FailingReranker());
        var result = await service.SearchAsync(request with { Rerank = false });
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(3, result.RetrievedCount);
    }

    [Fact]
    public async Task OptionsFlowToRerankerAndFewerCandidatesAreBounded()
    {
        var spy = new CapturingReranker();
        var service = new SearchService(new FakeQuery(Rows), new FailingEmbedding(), spy);
        foreach (var k in new[] { 1, 3, 10 })
        {
            var options = new RerankOptions("json", "title", k, true);
            var result = await service.SearchAsync(new("query", "qflat", SearchMode.FullText,
                Rerank: true, RerankOptions: options));
            Assert.Equal(options, spy.Options);
            Assert.Equal(Math.Min(k, 3), result.Rows.Count);
            Assert.Equal(3, result.RetrievedCount);
            Assert.True(result.SentenceScoresRequested);
        }
        var emptyService = new SearchService(new FakeQuery([]), new FailingEmbedding(), new FailingReranker());
        var empty = await emptyService.SearchAsync(new("query", "qflat", SearchMode.FullText,
            Rerank: true, RerankOptions: new("json", "title", 1, true)));
        Assert.Empty(empty.Rows);
        Assert.Equal(0, empty.RetrievedCount);
        Assert.False(empty.Reranked);
    }

    private sealed class CapturingReranker : IReranker
    {
        public RerankOptions? Options { get; private set; }
        public Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<SearchRow> rows,
            CancellationToken cancellationToken, RerankOptions? options = null)
        {
            Options = options;
            return Task.FromResult<IReadOnlyList<RerankScore>>(
                Enumerable.Range(0, Math.Min(options!.TopK, rows.Count))
                    .Select(i => new RerankScore(i, 1.0 - i / 10.0)).ToArray());
        }
    }
}
