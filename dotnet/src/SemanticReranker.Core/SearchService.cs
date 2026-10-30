using System.Diagnostics;

namespace SemanticReranker.Core;

public sealed class SearchService(IQueryExecutor queries, IEmbeddingProvider embeddings, IReranker reranker)
{
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        QueryBuilder.Validate(request);
        var total = Stopwatch.StartNew();
        float[]? vector = null;
        double embeddingMs = 0, rerankMs = 0;
        if (request.Mode is SearchMode.Vector or SearchMode.Hybrid)
        {
            var timer = Stopwatch.StartNew();
            try { vector = await embeddings.EmbedAsync(request.Query, cancellationToken); }
            catch (Exception ex) when (ex is not SampleException and not OperationCanceledException)
            { throw new SampleException("Embedding failed. Check the OpenAI endpoint, deployment and identity permissions.", ex); }
            embeddingMs = timer.Elapsed.TotalMilliseconds;
        }

        var plan = QueryBuilder.Build(request, vector);
        QueryResult retrieved;
        try { retrieved = await queries.ExecuteAsync(request.Container, plan, cancellationToken); }
        catch (Exception ex) when (ex is not SampleException and not OperationCanceledException)
        { throw new SampleException("Retrieval failed. Check Cosmos configuration, policies and data permissions.", ex); }

        var rows = retrieved.Rows.Select((row, index) =>
            row with { RetrievalRank = index + 1, RetrievalIndex = index }).ToArray();
        if (rows.Any(row => string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Text) ||
                string.IsNullOrWhiteSpace(row.Title) ||
                (row.RetrievalScore.HasValue && !double.IsFinite(row.RetrievalScore.Value))) ||
            rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new SampleException("Retrieval returned invalid or duplicate document metadata.");

        var reranked = request.Rerank && rows.Length > 0;
        var options = request.RerankOptions ?? new RerankOptions();
        if (reranked)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var scores = await reranker.RerankAsync(request.Query, rows, cancellationToken, options);
                rows = RerankMapping.Apply(rows, scores, Math.Min(options.TopK, rows.Length),
                    options.ReturnSentenceScores).ToArray();
            }
            catch (Exception ex) when (ex is not SampleException and not OperationCanceledException)
            { throw new SampleException("Reranking failed. Check the inference endpoint and Semantic Reranker User permission. No fallback results were returned.", ex); }
            rerankMs = timer.Elapsed.TotalMilliseconds;
        }
        return new(rows, retrieved.RequestCharge, retrieved.RetrievalMilliseconds, embeddingMs,
            rerankMs, total.Elapsed.TotalMilliseconds, reranked, plan.Sql, retrieved.Rows.Count,
            reranked && options.ReturnSentenceScores);
    }
}

public static class RerankMapping
{
    public static IReadOnlyList<SearchRow> Apply(IReadOnlyList<SearchRow> rows, IReadOnlyList<RerankScore> scores,
        int? expectedCount = null, bool returnSentenceScores = false)
    {
        var expected = expectedCount ?? rows.Count;
        if (expected < 0 || expected > rows.Count || scores.Count != expected)
            throw new SampleException("Reranking returned an incomplete result set. No fallback results were returned.");
        var seen = new HashSet<int>();
        var output = new List<SearchRow>(rows.Count);
        foreach (var score in scores)
        {
            if (score is null || score.Index is not int index || index < 0 || index >= rows.Count || !seen.Add(index) ||
                score.Score is not double value || !double.IsFinite(value))
                throw new SampleException("Reranking returned invalid indices or scores. No fallback results were returned.");
            if (returnSentenceScores && score.SentenceScores is { } sentences)
            {
                var sentenceIndices = new HashSet<int>();
                foreach (var sentence in sentences)
                    if (sentence is null || sentence.Index is not int sentenceIndex || sentenceIndex < 0 ||
                        !sentenceIndices.Add(sentenceIndex) ||
                        sentence.Score is not double sentenceValue || !double.IsFinite(sentenceValue))
                        throw new SampleException("Reranking returned invalid sentence indices or scores. No fallback results were returned.");
            }
            output.Add(rows[index] with
            {
                RerankScore = value, RetrievalRank = index + 1, RetrievalIndex = index,
                SentenceScores = returnSentenceScores ? score.SentenceScores : null
            });
        }
        return output.OrderByDescending(row => row.RerankScore).ToArray();
    }
}
