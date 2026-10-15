using Azure.Core;
using Azure.Data.AI;

namespace SemanticReranker.Core;

public sealed class AzureReranker : IReranker
{
    private readonly Lazy<InferenceClient> client;
    private readonly string model;

    public AzureReranker(SampleSettings settings, TokenCredential credential)
    {
        model = settings.RerankerModel;
        // Inference configuration is not needed for retrieval with reranking disabled.
        client = new(() => new InferenceClient(
            SampleSettings.RequireHttps(settings.InferenceEndpoint, "INFERENCE_ENDPOINT"), credential,
            new InferenceClientOptions { Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(60) } }));
    }

    public static SemanticRerankingInferenceContent CreateRequest(
        string query, IReadOnlyList<SearchRow> rows, string model) =>
        new(query, rows.Select(row => row.Text))
        {
            DocumentType = SemanticRerankingDocumentType.Text,
            ReturnDocuments = false,
            TopK = rows.Count,
            Sort = true,
            Model = model
        };

    public static IReadOnlyList<RerankScore> MapResponse(SemanticRerankingInferenceResult result) =>
        result.Scores.Select(score => new RerankScore(score.Index, score.Score)).ToArray();

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<SearchRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var response = await client.Value.SemanticRerankAsync(CreateRequest(query, rows, model), cancellationToken);
        return MapResponse(response.Value);
    }
}
