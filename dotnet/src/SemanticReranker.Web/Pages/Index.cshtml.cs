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
    [BindProperty] public string DocumentFormat { get; set; } = "text";
    [BindProperty] public string TargetPaths { get; set; } = "title,text";
    [BindProperty] public int RerankTopK { get; set; } = 10;
    [BindProperty] public bool ReturnSentenceScores { get; set; }
    public SearchResult? Result { get; private set; }
    public string? ErrorMessage { get; private set; }
    public void OnGet() { }

    public async Task OnPostAsync()
    {
        // A missing optional form field is different from an explicitly invalid value.
        // Razor binding can add implicit-required errors for absent nonnullable strings,
        // even when the page property has an initializer. Old POSTs and disabled selects
        // must retain the same defaults as the actual controls.
        var defaults = new RerankOptions();
        if (!Rerank || !Request.Form.ContainsKey(nameof(DocumentFormat)))
        {
            DocumentFormat = defaults.DocumentFormat;
            ModelState.Remove(nameof(DocumentFormat));
        }
        if (!Rerank || !Request.Form.ContainsKey(nameof(TargetPaths)))
        {
            TargetPaths = defaults.TargetPaths;
            ModelState.Remove(nameof(TargetPaths));
        }
        if (!Rerank || !Request.Form.ContainsKey(nameof(RerankTopK)))
        {
            RerankTopK = defaults.TopK;
            ModelState.Remove(nameof(RerankTopK));
        }
        if (!Rerank || !Request.Form.ContainsKey(nameof(ReturnSentenceScores)))
        {
            ReturnSentenceScores = defaults.ReturnSentenceScores;
            ModelState.Remove(nameof(ReturnSentenceScores));
        }
        if (!ModelState.IsValid)
        {
            ErrorMessage = ModelState.TryGetValue(nameof(ReturnSentenceScores), out var sentences)
                && sentences.Errors.Count > 0
                ? "Sentence-score diagnostics must be true or false. No search was run."
                : "Enter a query and select valid search options. Rerank result limit must be an integer between 1 and 10. No search was run.";
            // Checkbox tag helpers prefer ModelState's raw attempted value over the
            // typed property. Trying to convert an invalid bool again can throw during
            // error rendering. Keep validation errors, but render the typed fallback.
            foreach (var field in ModelState.Where(entry => entry.Value?.Errors.Count > 0)
                         .Select(entry => entry.Key).ToArray())
                ModelState.SetModelValue(field, rawValue: null, attemptedValue: null);
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var request = new SearchRequest(Query, Container, Mode, AllKeywords, Rerank,
                RerankOptions: Rerank ? new(DocumentFormat, TargetPaths, RerankTopK, ReturnSentenceScores) : null);
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
