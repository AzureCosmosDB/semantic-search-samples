using Azure.Identity;
using SemanticReranker.Core;

try
{
    var options = LoaderOptions.Parse(args);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var documents = await FixtureLoader.ReadFileAsync(options.Path, options.MaxItems, timeout.Token);
    using var store = new CosmosStore(SampleSettings.FromEnvironment(), new DefaultAzureCredential());
    var readbacks = await FixtureLoader.LoadAsync(store, documents, options.MaxItems, timeout.Token);
    Console.WriteLine($"Loaded {documents.Length} documents per container; {readbacks} exact readbacks verified. No resources created or deleted.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex is SampleException ? ex.Message :
        "Load failed. Check the JSON file, existing container policies, credentials and service availability.");
    Console.Error.WriteLine("Some items may have been written. No rollback or cleanup was attempted. Re-run the same stable IDs after fixing the error.");
    return 1;
}
