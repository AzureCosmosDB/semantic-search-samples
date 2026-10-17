using System.Text.Json;

namespace SemanticReranker.Core;

public static class QueryBuilder
{
    public const int Dimensions = 1536;

    public static void Validate(SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 1000)
            throw new SampleException("Enter a query between 1 and 1000 characters.");
        if (request.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 32)
            throw new SampleException("Use at most 32 query terms.");
        if (request.Container is not ("qflat" or "diskann"))
            throw new SampleException("Select qflat or diskann.");
        if (!Enum.IsDefined(request.Mode))
            throw new SampleException("Select a supported search mode.");
        if (request.Limit is < 1 or > 50)
            throw new SampleException("Result limit must be between 1 and 50.");
        if (request.Rerank)
        {
            if (request.Limit > 10)
                throw new SampleException("Reranking accepts at most 10 retrieved candidates.");
            (request.RerankOptions ?? new RerankOptions()).Validate();
        }
    }

    public static void ValidateVector(float[]? vector)
    {
        if (vector is null || vector.Length != Dimensions ||
            vector.Any(value => !float.IsFinite(value)) || vector.All(value => value == 0))
            throw new SampleException("An embedding must contain 1536 finite float values and must not be all zero.");
    }

    public static QueryPlan Build(SearchRequest request, float[]? vector = null)
    {
        Validate(request);
        if (request.Mode is SearchMode.Vector or SearchMode.Hybrid)
            ValidateVector(vector);
        // FullTextScore does not support ordinary SQL parameters on all service versions.
        // JSON string serialization, never raw interpolation, produces safe Cosmos SQL literals.
        var terms = request.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var keywordLiterals = string.Join(", ", terms.Select(term => JsonSerializer.Serialize(term)));
        // Match Python's ranked-query contract: one argument per search term,
        // not the entire user input serialized as a single phrase argument.
        var projection = $"SELECT TOP {request.Limit} c.id, c.title, c.text";
        const string distance = "VectorDistance(c.embedding, @vector)";
        var sql = request.Mode switch
        {
            SearchMode.Vector => $"{projection}, {distance} AS retrievalScore FROM c ORDER BY {distance}",
            SearchMode.FullText => $"{projection} FROM c WHERE FullTextContains{(request.AllKeywords ? "All" : "Any")}(c.text, {keywordLiterals})",
            SearchMode.FullTextRank => $"{projection} FROM c ORDER BY RANK FullTextScore(c.text, {keywordLiterals})",
            SearchMode.Hybrid => $"{projection} FROM c ORDER BY RANK RRF(FullTextScore(c.text, {keywordLiterals}), {distance})",
            _ => throw new SampleException("Select a supported search mode.")
        };
        return new QueryPlan(sql, request.Mode is SearchMode.Vector or SearchMode.Hybrid ? vector : null);
    }
}
