using Azure.Core;
using Microsoft.Azure.Cosmos;
using System.Diagnostics;
using System.Text.Json;

namespace SemanticReranker.Core;

public interface IFixtureStore
{
    Task ValidateContainerAsync(string selector, CancellationToken cancellationToken);
    Task UpsertAsync(string selector, MovieDocument document, CancellationToken cancellationToken);
    Task<MovieDocument> ReadAsync(string selector, string id, CancellationToken cancellationToken);
}

public sealed class CosmosStore : IQueryExecutor, IFixtureStore, IDisposable
{
    private readonly CosmosClient client;
    private readonly SampleSettings settings;

    public CosmosStore(SampleSettings settings, TokenCredential credential)
    {
        settings.ValidateCosmos();
        this.settings = settings;
        client = new CosmosClient(settings.CosmosEndpoint!, credential, new CosmosClientOptions
        {
            ApplicationName = "SemanticRerankerSample",
            ConnectionMode = settings.GatewayMode ? ConnectionMode.Gateway : ConnectionMode.Direct,
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
            MaxRetryAttemptsOnRateLimitedRequests = 3,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30),
            RequestTimeout = TimeSpan.FromSeconds(30)
        });
    }

    private Container Container(string selector) =>
        client.GetContainer(settings.Database!, settings.ContainerName(selector));

    public async Task<QueryResult> ExecuteAsync(string container, QueryPlan plan, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var query = new QueryDefinition(plan.Sql);
        if (plan.Vector is not null) query.WithParameter("@vector", plan.Vector);
        using var iterator = Container(container).GetItemQueryIterator<SearchRow>(query,
            requestOptions: new QueryRequestOptions { MaxItemCount = 50, MaxConcurrency = 1 });
        var rows = new List<SearchRow>();
        double ru = 0;
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            ru += page.RequestCharge;
            rows.AddRange(page);
        }
        return new(rows, ru, timer.Elapsed.TotalMilliseconds);
    }

    public async Task ValidateContainerAsync(string selector, CancellationToken cancellationToken)
    {
        using var response = await Container(selector).ReadContainerStreamAsync(cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken);
        ContainerPolicy.Validate(json.RootElement, selector == "qflat" ? "quantizedFlat" : "diskANN");
    }

    public async Task UpsertAsync(string selector, MovieDocument document, CancellationToken cancellationToken) =>
        _ = await Container(selector).UpsertItemAsync(document, new PartitionKey(document.Id),
            new ItemRequestOptions { EnableContentResponseOnWrite = false }, cancellationToken);

    public async Task<MovieDocument> ReadAsync(string selector, string id, CancellationToken cancellationToken) =>
        (await Container(selector).ReadItemAsync<MovieDocument>(id, new PartitionKey(id),
            cancellationToken: cancellationToken)).Resource;

    public void Dispose() => client.Dispose();
}

public static class ContainerPolicy
{
    public static void Validate(JsonElement container, string indexType)
    {
        try
        {
            var keys = container.GetProperty("partitionKey").GetProperty("paths");
            var vectors = container.GetProperty("vectorEmbeddingPolicy").GetProperty("vectorEmbeddings");
            var text = container.GetProperty("fullTextPolicy").GetProperty("fullTextPaths");
            var indexing = container.GetProperty("indexingPolicy");
            if (keys.GetArrayLength() != 1 || keys[0].GetString() != "/id" ||
                !vectors.EnumerateArray().Any(v => v.GetProperty("path").GetString() == "/embedding" &&
                    v.GetProperty("dataType").GetString() == "float32" &&
                    v.GetProperty("dimensions").GetInt32() == QueryBuilder.Dimensions &&
                    v.GetProperty("distanceFunction").GetString() == "cosine") ||
                !text.EnumerateArray().Any(t => t.GetProperty("path").GetString() == "/text" &&
                    t.GetProperty("language").GetString() == "en-US") ||
                !string.Equals(indexing.GetProperty("indexingMode").GetString(), "consistent", StringComparison.OrdinalIgnoreCase) ||
                !indexing.GetProperty("automatic").GetBoolean() ||
                !indexing.GetProperty("vectorIndexes").EnumerateArray().Any(v =>
                    v.GetProperty("path").GetString() == "/embedding" && v.GetProperty("type").GetString() == indexType) ||
                !indexing.GetProperty("fullTextIndexes").EnumerateArray().Any(t => t.GetProperty("path").GetString() == "/text"))
                throw new SampleException("Existing container policies are incompatible. No resources were changed. See explicit container setup.");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SampleException("Existing container is missing required policies. No resources were changed. See explicit container setup.");
        }
    }
}
