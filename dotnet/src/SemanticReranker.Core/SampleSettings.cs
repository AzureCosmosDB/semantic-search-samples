namespace SemanticReranker.Core;

public sealed record SampleSettings
{
    public string? CosmosEndpoint { get; init; }
    public string? Database { get; init; }
    public string QflatContainer { get; init; } = "reranker_dotnet_qflat";
    public string DiskannContainer { get; init; } = "reranker_dotnet_diskann";
    public string? InferenceEndpoint { get; init; }
    public string RerankerModel { get; init; } = "aisearch-reranker";
    public string? OpenAiEndpoint { get; init; }
    public string? OpenAiDeployment { get; init; }
    public string? OpenAiKey { get; init; }
    public string OpenAiApiVersion { get; init; } = "2024-02-01";
    public bool GatewayMode { get; init; }

    public static SampleSettings FromEnvironment() => new()
    {
        CosmosEndpoint = Env("COSMOS_DB_ENDPOINT"),
        Database = Env("COSMOS_DB_DATABASE"),
        QflatContainer = Env("COSMOS_DB_CONTAINER_QFLAT") ?? "reranker_dotnet_qflat",
        DiskannContainer = Env("COSMOS_DB_CONTAINER_DISKANN") ?? "reranker_dotnet_diskann",
        InferenceEndpoint = Env("INFERENCE_ENDPOINT"),
        RerankerModel = Env("RERANKER_MODEL") ?? "aisearch-reranker",
        OpenAiEndpoint = Env("AZURE_OPENAI_ENDPOINT"),
        OpenAiDeployment = Env("AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),
        OpenAiKey = Env("AZURE_OPENAI_API_KEY"),
        OpenAiApiVersion = Env("AZURE_OPENAI_API_VERSION") ?? "2024-02-01",
        GatewayMode = Env("COSMOS_CONNECTION_MODE")?.Equals("Gateway", StringComparison.OrdinalIgnoreCase) == true
    };

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public static Uri RequireHttps(string? value, string variable)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new SampleException($"Configure {variable} with an HTTPS service endpoint.");
        return uri;
    }

    public void ValidateCosmos()
    {
        RequireHttps(CosmosEndpoint, "COSMOS_DB_ENDPOINT");
        if (string.IsNullOrWhiteSpace(Database))
            throw new SampleException("Configure COSMOS_DB_DATABASE with an existing database.");
        if (string.IsNullOrWhiteSpace(QflatContainer) || string.IsNullOrWhiteSpace(DiskannContainer) ||
            QflatContainer == DiskannContainer)
            throw new SampleException("Configure two distinct existing Cosmos containers.");
    }

    public string ContainerName(string selector) => selector switch
    {
        "qflat" => QflatContainer,
        "diskann" => DiskannContainer,
        _ => throw new SampleException("Select qflat or diskann.")
    };
}
