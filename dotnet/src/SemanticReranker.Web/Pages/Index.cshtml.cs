using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SemanticReranker.Core;

namespace SemanticReranker.Web.Pages;

// Resolve the search service only on POST so the UI starts without credentials or configuration.
public sealed class IndexModel(IServiceProvider services) : PageModel
{
    [BindProperty] public string Query { get; set; } = "";
    [BindProperty] public string Container { get; set; } = "qflat";
    [BindProperty] public SearchMode Mode { get; set; } = SearchMode.FullText;
    [BindProperty] public bool AllKeywords { get; set; } = true;
    [BindProperty] public bool Rerank { get; set; }
    public SearchResult? Result { get; private set; }
    public string? ErrorMessage { get; private set; }
    public void OnGet() { }

    public async Task OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = "Enter a query and select valid search options.";
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var request = new SearchRequest(Query, Container, Mode, AllKeywords, Rerank);
            QueryBuilder.Validate(request);
            // Return the missing embedding configuration message before opening Cosmos clients.
            var settings = services.GetRequiredService<SampleSettings>();
            if ((Mode is SearchMode.Vector or SearchMode.Hybrid) &&
                (string.IsNullOrWhiteSpace(settings.OpenAiEndpoint) || string.IsNullOrWhiteSpace(settings.OpenAiDeployment)))
                throw new SampleException("Configure AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_EMBEDDING_DEPLOYMENT for vector/hybrid search. Full-text search does not require OpenAI.");
            Result = await services.GetRequiredService<SearchService>().SearchAsync(request, timeout.Token);
        }
        catch (SampleException ex) { ErrorMessage = ex.Message; }
        catch (OperationCanceledException) { ErrorMessage = "Search timed out or was cancelled. No success results were returned."; }
        catch (Exception) { ErrorMessage = "Search failed. Check service configuration and permissions. No success results were returned."; }
    }
}
