using Azure.Core;
using Azure.Identity;
using SemanticReranker.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddSingleton(SampleSettings.FromEnvironment());
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddSingleton<CosmosStore>();
builder.Services.AddSingleton<IQueryExecutor>(services => services.GetRequiredService<CosmosStore>());
builder.Services.AddSingleton<IReranker, AzureReranker>();
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
builder.Services.AddSingleton<IEmbeddingProvider, OpenAiEmbeddings>();
builder.Services.AddSingleton<SearchService>();

var app = builder.Build();
// There is deliberately no Cosmos provisioning, read, token acquisition or data load at startup.
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();
app.MapGet("/healthz", () => Results.Text("healthy")); // process health, not a service connectivity claim
app.MapRazorPages();
app.Run();

public partial class Program { }
