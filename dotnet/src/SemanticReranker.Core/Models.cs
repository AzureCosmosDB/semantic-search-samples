using System.Text.Json.Serialization;

namespace SemanticReranker.Core;

public enum SearchMode { Vector, FullText, FullTextRank, Hybrid }

public sealed record SearchRequest(
    string Query, string Container, SearchMode Mode,
    bool AllKeywords = true, bool Rerank = false, int Limit = 10,
    RerankOptions? RerankOptions = null);

public sealed record RerankOptions(
    string DocumentFormat = "text", string TargetPaths = "title,text",
    int TopK = 10, bool ReturnSentenceScores = false)
{
    public void Validate()
    {
        if (DocumentFormat is not ("text" or "json"))
            throw new SampleException("Select text or json document format.");
        if (TopK is < 1 or > 10)
            throw new SampleException("Rerank result limit must be between 1 and 10.");
        if (DocumentFormat == "json" && TargetPaths is not ("title" or "text" or "title,text"))
            throw new SampleException("JSON targeting must select title, text, or title and text.");
    }
}

public sealed record SearchRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("retrievalScore")] double? RetrievalScore = null,
    [property: JsonIgnore] double? RerankScore = null,
    [property: JsonIgnore] int RetrievalRank = 0,
    [property: JsonIgnore] int? RetrievalIndex = null,
    [property: JsonIgnore] IReadOnlyList<SentenceScore>? SentenceScores = null);

public sealed record MovieDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("embedding")] float[] Embedding);

public sealed record SentenceScore(int? Index, double? Score);
public sealed record RerankScore(int? Index, double? Score,
    IReadOnlyList<SentenceScore>? SentenceScores = null);
public sealed record QueryPlan(string Sql, float[]? Vector = null);
public sealed record QueryResult(IReadOnlyList<SearchRow> Rows, double RequestCharge, double RetrievalMilliseconds);
public sealed record SearchResult(
    IReadOnlyList<SearchRow> Rows, double RequestCharge, double RetrievalMilliseconds,
    double EmbeddingMilliseconds, double RerankMilliseconds, double TotalMilliseconds,
    bool Reranked, string Sql, int RetrievedCount = 0, bool SentenceScoresRequested = false);

// Only messages intentionally safe for the UI should use this exception.
public sealed class SampleException(string message, Exception? innerException = null)
    : Exception(message, innerException) { }

public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
}
public interface IQueryExecutor
{
    Task<QueryResult> ExecuteAsync(string container, QueryPlan plan, CancellationToken cancellationToken);
}
public interface IReranker
{
    Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<SearchRow> rows, CancellationToken cancellationToken,
        RerankOptions? options = null);
}
