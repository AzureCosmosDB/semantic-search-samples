using Azure.Core;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SemanticReranker.Core;

// A small REST adapter keeps optional embeddings separate from the reranking SDK.
// The reusable HttpClient and credential are owned by the application.
public sealed class OpenAiEmbeddings(SampleSettings settings, TokenCredential credential, HttpClient http) : IEmbeddingProvider
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.OpenAiEndpoint) || string.IsNullOrWhiteSpace(settings.OpenAiDeployment))
            throw new SampleException("Configure AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_EMBEDDING_DEPLOYMENT for vector/hybrid search. Full-text search does not require OpenAI.");
        var endpoint = SampleSettings.RequireHttps(settings.OpenAiEndpoint, "AZURE_OPENAI_ENDPOINT");
        var uri = new Uri(endpoint.AbsoluteUri.TrimEnd('/') +
            $"/openai/deployments/{Uri.EscapeDataString(settings.OpenAiDeployment)}/embeddings?api-version={Uri.EscapeDataString(settings.OpenAiApiVersion)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new { input = text })
        };
        if (!string.IsNullOrWhiteSpace(settings.OpenAiKey))
            request.Headers.Add("api-key", settings.OpenAiKey);
        else
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var entries = json.RootElement.GetProperty("data");
        if (entries.GetArrayLength() != 1 || entries[0].GetProperty("index").GetInt32() != 0)
            throw new SampleException("Embedding service returned an unexpected response.");
        var vector = entries[0].GetProperty("embedding").EnumerateArray().Select(item => item.GetSingle()).ToArray();
        QueryBuilder.ValidateVector(vector);
        return vector;
    }
}
