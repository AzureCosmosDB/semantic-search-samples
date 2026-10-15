using System.Text.Json;

namespace SemanticReranker.Core;

public sealed record LoaderOptions(string Path, int MaxItems)
{
    public static LoaderOptions Parse(string[] args)
    {
        string? path = null;
        int maxItems = 0;
        bool existingOnly = false, confirm = false;
        var seen = new HashSet<string>();
        for (int index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!seen.Add(arg)) throw new SampleException("Duplicate loader option.");
            switch (arg)
            {
                case "--path" when index + 1 < args.Length: path = args[++index]; break;
                case "--max-items" when index + 1 < args.Length:
                    if (!int.TryParse(args[++index], out maxItems)) throw new SampleException("Invalid --max-items.");
                    break;
                case "--existing-only": existingOnly = true; break;
                case "--confirm-write": confirm = true; break;
                default: throw new SampleException("Unknown or incomplete loader option.");
            }
        }
        if (string.IsNullOrWhiteSpace(path) || maxItems is < 1 or > 9 || !existingOnly || !confirm)
            throw new SampleException("Required: --path <preembedded.json> --existing-only --max-items <1..9> --confirm-write.");
        return new(path, maxItems);
    }
}

public static class FixtureLoader
{
    public static async Task<MovieDocument[]> ReadFileAsync(string path, int maxItems, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 2_000_000)
            throw new SampleException("Fixture must be an existing JSON file smaller than 2 MB.");
        await using var file = File.OpenRead(path);
        var documents = await JsonSerializer.DeserializeAsync<MovieDocument[]>(file, cancellationToken: cancellationToken)
            ?? throw new SampleException("Fixture must be a JSON array.");
        ValidateDocuments(documents, maxItems);
        return documents;
    }

    public static void ValidateDocuments(IReadOnlyList<MovieDocument> documents, int maxItems)
    {
        if (maxItems is < 1 or > 9 || documents.Count < 1 || documents.Count > maxItems)
            throw new SampleException("Fixture item count exceeds the explicitly allowed bound (maximum 9).");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in documents)
        {
            if (doc is null || string.IsNullOrWhiteSpace(doc.Id) || doc.Id.Length > 200 ||
                doc.Id.IndexOfAny(['/', '\\', '?', '#']) >= 0 || !ids.Add(doc.Id) ||
                string.IsNullOrWhiteSpace(doc.Title) || string.IsNullOrWhiteSpace(doc.Text) || doc.Text.Length > 20_000)
                throw new SampleException("Every fixture needs a unique valid id, title and bounded text.");
            QueryBuilder.ValidateVector(doc.Embedding);
        }
    }

    public static async Task<int> LoadAsync(
        IFixtureStore store, IReadOnlyList<MovieDocument> documents, int maxItems, CancellationToken cancellationToken = default)
    {
        ValidateDocuments(documents, maxItems);
        string[] targets = ["qflat", "diskann"];
        // Validate BOTH containers before the first data write.
        foreach (var target in targets)
            await store.ValidateContainerAsync(target, cancellationToken);
        int readbacks = 0;
        foreach (var target in targets)
        foreach (var document in documents)
        {
            await store.UpsertAsync(target, document, cancellationToken);
            var read = await store.ReadAsync(target, document.Id, cancellationToken);
            if (read.Id != document.Id || read.Title != document.Title || read.Text != document.Text ||
                read.Embedding is null || !read.Embedding.SequenceEqual(document.Embedding))
                throw new SampleException("Fixture readback did not match the upserted document.");
            readbacks++;
        }
        return readbacks;
    }
}
