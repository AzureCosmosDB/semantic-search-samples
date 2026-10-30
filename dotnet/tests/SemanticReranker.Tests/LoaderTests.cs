using SemanticReranker.Core;
using System.Text.Json;

namespace SemanticReranker.Tests;

public class LoaderTests
{
    [Fact]
    public async Task ValidatesBothContainersBeforeWritingAndStopsOnFailure()
    {
        var badPolicy = new FakeStore { FailSecondPolicy = true };
        await Assert.ThrowsAsync<SampleException>(() => FixtureLoader.LoadAsync(badPolicy, TestData.Documents(), 3));
        Assert.Equal(0, badPolicy.Writes);
        var badWrite = new FakeStore { FailWrite = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureLoader.LoadAsync(badWrite, TestData.Documents(), 3));
        Assert.Equal(1, badWrite.Writes);
    }

    [Fact]
    public async Task RepeatedLoadUsesStableIdsAndVerifiesEveryReadback()
    {
        var store = new FakeStore();
        Assert.Equal(6, await FixtureLoader.LoadAsync(store, TestData.Documents(), 3));
        Assert.Equal(6, await FixtureLoader.LoadAsync(store, TestData.Documents(), 3));
        Assert.Equal(6, store.Items.Count);
        Assert.Equal(12, store.Writes);
        store.CorruptReadback = true;
        await Assert.ThrowsAsync<SampleException>(() => FixtureLoader.LoadAsync(store, TestData.Documents(), 3));
    }

    [Fact]
    public void RequiresExplicitWriteBoundsAndExistingOnly()
    {
        Assert.Throws<SampleException>(() => LoaderOptions.Parse(["--path", "data.json"]));
        Assert.Throws<SampleException>(() => LoaderOptions.Parse(
            ["--path", "data.json", "--existing-only", "--confirm-write", "--max-items", "10"]));
        Assert.Throws<SampleException>(() => LoaderOptions.Parse(
            ["--path", "data.json", "--existing-only", "--confirm-write", "--max-items", "3", "--typo"]));
        var options = LoaderOptions.Parse(
            ["--path", "data.json", "--existing-only", "--confirm-write", "--max-items", "3"]);
        Assert.Equal(3, options.MaxItems);
    }

    [Fact]
    public void AllItemsAreValidatedBeforeAnyWrite()
    {
        var valid = TestData.Documents();
        FixtureLoader.ValidateDocuments(valid, 3);
        Assert.Throws<SampleException>(() => FixtureLoader.ValidateDocuments(valid, 2));
        Assert.Throws<SampleException>(() => FixtureLoader.ValidateDocuments([], 3));
        Assert.Throws<SampleException>(() => FixtureLoader.ValidateDocuments([valid[0], valid[0]], 3));
        Assert.Throws<SampleException>(() => FixtureLoader.ValidateDocuments(
            [valid[0] with { Embedding = [1, 2] }], 3));
        Assert.Throws<SampleException>(() => FixtureLoader.ValidateDocuments(
            [valid[0] with { Id = "bad/id" }], 3));
        var nonFinite = TestData.Vector(0);
        nonFinite[1] = float.NaN;
        Assert.Throws<SampleException>(() => FixtureLoader.ValidateDocuments(
            [valid[0] with { Embedding = nonFinite }], 3));
        Assert.Throws<SampleException>(() => FixtureLoader.ValidateDocuments(
            [valid[0] with { Embedding = new float[1536] }], 3));
    }

    [Fact]
    public void PolicyCheckRejectsWrongPartitionVectorAndFullTextIndex()
    {
        var policy = """
            {"partitionKey":{"paths":["/id"]},
             "vectorEmbeddingPolicy":{"vectorEmbeddings":[{"path":"/embedding","dataType":"float32","dimensions":1536,"distanceFunction":"cosine"}]},
             "fullTextPolicy":{"defaultLanguage":"en-US","fullTextPaths":[{"path":"/text","language":"en-US"}]},
             "indexingPolicy":{"indexingMode":"consistent","automatic":true,"vectorIndexes":[{"path":"/embedding","type":"quantizedFlat"}],"fullTextIndexes":[{"path":"/text"}]}}
            """;
        using var correct = JsonDocument.Parse(policy);
        ContainerPolicy.Validate(correct.RootElement, "quantizedFlat");
        Assert.Throws<SampleException>(() => ContainerPolicy.Validate(correct.RootElement, "diskANN"));
        foreach (var wrong in new[] { policy.Replace("/id", "/pk"), policy.Replace("1536", "3"),
                     policy.Replace("float32", "int8"), policy.Replace("cosine", "euclidean"),
                     policy.Replace("/text", "/other"), policy.Replace("en-US", "fr-FR"),
                     policy.Replace("consistent", "none") })
        {
            using var doc = JsonDocument.Parse(wrong);
            Assert.Throws<SampleException>(() => ContainerPolicy.Validate(doc.RootElement, "quantizedFlat"));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"partitionKey":123}""")]
    public void MissingOrMalformedPolicyRetainsParsingFailure(string policy)
    {
        using var doc = JsonDocument.Parse(policy);
        var error = Assert.Throws<SampleException>(() =>
            ContainerPolicy.Validate(doc.RootElement, "quantizedFlat"));
        Assert.NotNull(error.InnerException);
        Assert.True(error.InnerException is KeyNotFoundException or InvalidOperationException);
        Assert.Contains(nameof(ContainerPolicy.Validate), error.InnerException.StackTrace);
        Assert.Contains("No resources were changed", error.Message);
    }
}

internal sealed class FakeStore : IFixtureStore
{
    public bool FailSecondPolicy { get; init; }
    public bool FailWrite { get; init; }
    public bool CorruptReadback { get; set; }
    public int Writes { get; private set; }
    public Dictionary<(string, string), MovieDocument> Items { get; } = [];
    public Task ValidateContainerAsync(string selector, CancellationToken cancellationToken)
    {
        if (FailSecondPolicy && selector == "diskann") throw new SampleException("Incompatible policy.");
        return Task.CompletedTask;
    }
    public Task UpsertAsync(string selector, MovieDocument document, CancellationToken cancellationToken)
    {
        Writes++;
        if (FailWrite) throw new InvalidOperationException("Service failed.");
        Items[(selector, document.Id)] = document;
        return Task.CompletedTask;
    }
    public Task<MovieDocument> ReadAsync(string selector, string id, CancellationToken cancellationToken) =>
        Task.FromResult(CorruptReadback ? Items[(selector, id)] with { Title = "wrong" } : Items[(selector, id)]);
}

internal static class TestData
{
    public static float[] Vector(int axis)
    {
        var vector = new float[1536];
        vector[axis] = 1;
        return vector;
    }

    public static MovieDocument[] Documents()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "movies.synthetic.json")));
        return json.RootElement.EnumerateArray().Select(item => new MovieDocument(
            item.GetProperty("id").GetString()!,
            item.GetProperty("title").GetString()!,
            item.GetProperty("text").GetString()!,
            Vector(item.GetProperty("embeddingAxis").GetInt32()))).ToArray();
    }
}
