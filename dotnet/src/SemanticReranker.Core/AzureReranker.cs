using Azure.Core;
using Azure.Data.AI;
using System.Text.Json;

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

    public static SemanticRerankingContent CreateRequest(
        string query, IReadOnlyList<SearchRow> rows, string model, RerankOptions? options = null)
    {
        options ??= new();
        options.Validate();
        if (rows.Count > 10)
            throw new SampleException("Reranking accepts at most 10 retrieved candidates.");
        string[] paths = options.DocumentFormat == "json" ? options.TargetPaths.Split(',') : ["text"];
        if (rows.Any(row => paths.Any(path =>
                string.IsNullOrWhiteSpace(path == "title" ? row.Title : row.Text))))
            throw new SampleException("Reranking requires nonempty selected fields on every retrieved document.");
        // Whitelist projection, never serialize a whole Cosmos document or SearchRow.
        var documents = rows.Select(row => options.DocumentFormat == "json"
            ? JsonSerializer.Serialize(paths.ToDictionary(path => path,
                path => path == "title" ? row.Title : row.Text))
            : row.Text);
        var content = new SemanticRerankingContent(query, documents)
        {
            DocumentType = options.DocumentFormat == "json"
                ? SemanticRerankingDocumentType.Json : SemanticRerankingDocumentType.Text,
            ReturnDocuments = false,
            TopK = Math.Min(options.TopK, rows.Count),
            Sort = true,
            Model = model
        };
        if (options.DocumentFormat == "json")
            foreach (var path in paths) content.TargetPaths.Add(path);
        if (options.ReturnSentenceScores) content.ReturnSentenceScore = true;
        return content;
    }

    public static IReadOnlyList<RerankScore> MapResponse(
        SemanticRerankingResult result, bool returnSentenceScores = false) =>
        result.Scores.Select(score => new RerankScore(score.Index, score.Score,
            returnSentenceScores ? score.SentenceScores?.Select(sentence =>
                new SentenceScore(sentence.Index, sentence.Score)).ToArray() : null)).ToArray();

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<SearchRow> rows, CancellationToken cancellationToken,
        RerankOptions? options = null)
    {
        options ??= new();
        options.Validate();
        if (rows.Count == 0) return [];
        var response = await client.Value.SemanticRerankAsync(
            CreateRequest(query, rows, model, options), cancellationToken);
        return MapResponse(response.Value, options.ReturnSentenceScores);
    }
}
