using Azure.Core;
using SemanticReranker.Core;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SemanticReranker.Tests;

public sealed class EmbeddingTests
{
    [Fact]
    public async Task ConfiguredEmbeddingUsesEntraAndValidatesDimensions()
    {
        var handler = new EmbeddingHandler(TestData.Vector(0));
        using var http = new HttpClient(handler);
        var credential = new TestCredential();
        var provider = new OpenAiEmbeddings(new SampleSettings
        {
            OpenAiEndpoint = "https://example.openai.azure.com",
            OpenAiDeployment = "embedding"
        }, credential, http);
        var result = await provider.EmbedAsync("ocean", default);
        Assert.Equal(1536, result.Length);
        Assert.Equal("Bearer", handler.AuthenticationScheme);
        Assert.Equal("https://cognitiveservices.azure.com/.default", credential.Scope);
        Assert.Equal("ocean", handler.Input);
        Assert.Equal(1536, handler.Dimensions);
    }

    [Fact]
    public async Task WrongDimensionsAreNotPaddedOrAccepted()
    {
        using var http = new HttpClient(new EmbeddingHandler([1, 2, 3]));
        var provider = new OpenAiEmbeddings(new SampleSettings
        {
            OpenAiEndpoint = "https://example.openai.azure.com",
            OpenAiDeployment = "embedding",
            OpenAiKey = "fake-test-only"
        }, new TestCredential(), http);
        await Assert.ThrowsAsync<SampleException>(() => provider.EmbedAsync("ocean", default));
    }

    [Theory]
    [InlineData(SearchMode.Vector)]
    [InlineData(SearchMode.Hybrid)]
    public async Task VectorModesCallEmbeddingAndPassItToRetrieval(SearchMode mode)
    {
        var embedding = new CountingEmbedding();
        var queries = new CapturingQuery();
        var service = new SearchService(queries, embedding, new FailingReranker());
        var result = await service.SearchAsync(new("ocean", "qflat", mode));
        Assert.Equal(1, embedding.Calls);
        Assert.NotNull(queries.Plan?.Vector);
        Assert.Equal(TestData.Vector(0), queries.Plan!.Vector);
        Assert.False(result.Reranked);
    }

    private sealed class TestCredential : TokenCredential
    {
        public string? Scope { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scope = requestContext.Scopes.Single();
            return new("fake-test-token", DateTimeOffset.UtcNow.AddMinutes(5));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class EmbeddingHandler(float[] vector) : HttpMessageHandler
    {
        public string? AuthenticationScheme { get; private set; }
        public string? Input { get; private set; }
        public int Dimensions { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthenticationScheme = request.Headers.Authorization?.Scheme;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Input = body.RootElement.GetProperty("input").GetString();
            Dimensions = body.RootElement.GetProperty("dimensions").GetInt32();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { index = 0, embedding = vector } } }),
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CountingEmbedding : IEmbeddingProvider
    {
        public int Calls { get; private set; }
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(TestData.Vector(0));
        }
    }

    private sealed class CapturingQuery : IQueryExecutor
    {
        public QueryPlan? Plan { get; private set; }
        public Task<QueryResult> ExecuteAsync(string container, QueryPlan plan, CancellationToken cancellationToken)
        {
            Plan = plan;
            return Task.FromResult(new QueryResult([], 1, 1));
        }
    }
}
