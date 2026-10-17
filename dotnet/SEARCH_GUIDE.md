# .NET search guide

Manual local startup and a code walkthrough of the Razor Pages app.
Use the [root quickstart](../README.md) for prerequisites and deployment.
[README.md](README.md) retains package details and the separate legacy
existing-resource/synthetic-fixture workflow. That loader is not required here.

## Deploy once with azd up

From the **repository root**, use `azd up`. This remains the deployment entry
point: dependencies, keyless backend, generated config, and the bounded real
movie seed. `azd provision` alone does not seed. No new resources are needed for
advanced reranking. App startup and search requests never provision or write data.

`pwsh ./scripts/start.ps1` normally starts both apps in the foreground.
To run just Razor manually, stop your owned supervisor first to release port 8503
and avoid locked build outputs. Do not stop unrelated processes.

### Manual startup from the repository root

Use PowerShell 7 and the pinned .NET SDK family:

```powershell
dotnet restore dotnet/SemanticReranker.sln
dotnet build dotnet/SemanticReranker.sln --no-restore
. ./.local/azd-env.ps1
dotnet run --no-build --no-launch-profile `
  --project dotnet/src/SemanticReranker.Web -- --urls http://127.0.0.1:8503
```

`azd up` normally already restores/builds. Repeat those steps after changing
dependencies/code. Keep public NuGet sources and the committed package pins.
Do not add a separate Azure.Identity package: credential types in this sample
come from Azure.Core 1.63 in the `Azure.Identity` namespace.

### Equivalent commands from the dotnet directory

Start in **`dotnet/`**, not `src/`:

```powershell
dotnet restore SemanticReranker.sln
dotnet build SemanticReranker.sln --no-restore
# The generated configuration is in the REPOSITORY root, not dotnet/.local:
. ../.local/azd-env.ps1
dotnet run --no-build --no-launch-profile `
  --project src/SemanticReranker.Web -- --urls http://127.0.0.1:8503
```

Open <http://127.0.0.1:8503>. Ctrl+C stops this manual process. `--urls` explicitly
chooses the local binding rather than a launch profile. Select another loopback
port if occupied. The root launcher instead executes the built DLL with an
explicit content root; both routes use the same application.

### Configuration precedence, identity, and restarts

Dot-source `.local/azd-env.ps1` in the shell that starts the process. It sets
process environment variables and can replace earlier shell values. Apply any
intentional temporary overrides afterwards, before starting the app.

[`SampleSettings.FromEnvironment`](src/SemanticReranker.Core/SampleSettings.cs)
reads **process environment**, not Python's `.env`, azd state directly, or an
appsettings file. Important names are `COSMOS_DB_ENDPOINT`, `COSMOS_DB_DATABASE`,
`COSMOS_DB_CONTAINER_QFLAT`, `COSMOS_DB_CONTAINER_DISKANN`, `INFERENCE_ENDPOINT`,
`AZURE_OPENAI_ENDPOINT`, and `AZURE_OPENAI_EMBEDDING_DEPLOYMENT`.
The default selectors map to `reranker_dotnet_qflat` and `reranker_dotnet_diskann`.
Generated `AZURE_OPENAI_AUTH_MODE=entra` keeps embedding authentication keyless.

[`Program.cs`](src/SemanticReranker.Web/Program.cs) registers singleton settings,
credentials, and reusable service clients. Local `DefaultAzureCredential` can
use Azure CLI sign-in. Use the intended tenant/subscription and the appropriate
Cosmos, inference, and OpenAI data-plane roles. No Cosmos account key is used.
Semantic Reranker User is required for inference.

Settings are captured at process startup. Changing environment variables in a
different terminal or refreshing the browser cannot update the running app.
After correcting configuration or switching identity/environment, stop the owned
process, reload generated settings in a clean shell, and restart. Follow the root
README for azd environment switching. Never log tokens or commit local config.

## Request and data flow

[`IndexModel.OnPostAsync`](src/SemanticReranker.Web/Pages/Index.cshtml.cs) binds
the form and validates it. It rejects invalid requests before resolving search
services and returns safe errors. Antiforgery remains enabled.
[`SearchService.SearchAsync`](src/SemanticReranker.Core/SearchService.cs)
embeds when needed, retrieves, validates document metadata, and optionally reranks.
[`Index.cshtml`](src/SemanticReranker.Web/Pages/Index.cshtml) renders the result
table, counts, timings, and optional native expandable details.

[`CosmosStore.ExecuteAsync`](src/SemanticReranker.Core/CosmosStore.cs) executes
read-only queries against existing containers and accumulates RU across pages.
[`QueryBuilder.Build`](src/SemanticReranker.Core/QueryBuilder.cs) projects
ID, title, text, and vector distance where applicable, not embeddings/system
properties. Both indexes contain the same original dataset.

### Embeddings and vector search

Vector search uses [`OpenAiEmbeddings.EmbedAsync`](src/SemanticReranker.Core/OpenAiEmbeddings.cs),
the existing Azure OpenAI REST embedding adapter with a reusable HttpClient.
It requests 1536 dimensions and validates the single embedding response through
`QueryBuilder.ValidateVector`: correct length, finite float values, nonzero vector.

`QueryBuilder.Build` orders `SELECT TOP 10` by
`VectorDistance(c.embedding, @vector)`. The vector is a query parameter.
The displayed vector distance is lower-is-better and remains distinct from
reranking scores. Stored embeddings and query embeddings must use matching models
and dimensions. A new deployment name does not update stored vectors automatically.

### Full-text all and any

Full-text search splits the input into whitespace-separated keywords.
`QueryBuilder.Build` creates `FullTextContainsAll` or `FullTextContainsAny` on
`c.text`. Terms are JSON-serialized SQL literals, never raw user interpolation.

All keywords means every supplied keyword must match. This is **not** a
natural-language rewrite service. Extra words can reduce matches; compare
`SPECTRE spy` with a longer sentence and try Any keyword. Title is not part of
this filter. Boolean filtering does not promise relevance order.
Neither this mode nor full-text ranking needs an embedding call.

### FullTextScore ranking

Full-text ranking uses `ORDER BY RANK FullTextScore(c.text, ...)`.
Each keyword is passed separately, matching the Python query contract.
This ranks candidates instead of applying the all/any filter. The sample does
not project numeric FullTextScore values; the table does not invent them.

### Hybrid RRF ranking

Hybrid mode calls the same embedding provider and orders by
`RRF(FullTextScore(...), VectorDistance(...))` inside Cosmos.
It combines lexical and vector rankings, not the arithmetic mean of raw scores.
The sample does not project a numeric RRF score. The matching All/Any control
belongs to Boolean full-text search, not hybrid or full-text ranking.

### Standalone reranking

With Semantic reranking off, search returns the retrieval results and ignores
advanced options. No inference call is made.

With it on, [`AzureReranker.RerankAsync`](src/SemanticReranker.Core/AzureReranker.cs)
calls `Azure.Data.AI.InferenceClient.SemanticRerankAsync` using native
`SemanticRerankingInferenceContent`. This is not Azure OpenAI embedding and not
a reranking method on `Microsoft.Azure.Cosmos.Container`.
Defaults remain plain descriptions, all candidates reranked, sorted scores,
no sentence diagnostics, and the existing configured model. No extra model/batch
selector or client wrapper is introduced.

[`RerankMapping.Apply`](src/SemanticReranker.Core/SearchService.cs) maps every
returned zero-based index to its original [`SearchRow`](src/SemanticReranker.Core/Models.cs).
The service's returned document text is not used, even if included despite
`ReturnDocuments=false`. Duplicate descriptions keep their original IDs, titles,
text, vector scores, and retrieval positions. `RetrievalIndex` is zero-based,
`RetrievalRank` and the table's displayed rank are one-based. RerankScore is
higher-is-better and does not overwrite RetrievalScore.

### Advanced reranking

The native **Advanced reranking** details element starts collapsed.
Enable Semantic reranking to use its controls:

| Control | Default | Native request behavior |
| --- | --- | --- |
| Document format | `text` | `DocumentType=Text`; JSON is opt-in |
| JSON fields | Title and Description | Whitelist `title`, `text`, or `title,text` |
| Rerank result limit | 10 | `TopK=min(requested, candidate count)` |
| Sentence-score diagnostics | Off | Set `ReturnSentenceScore=true` only when requested |

Description is the UI name for `text`.
[`AzureReranker.CreateRequest`](src/SemanticReranker.Core/AzureReranker.cs) uses
`JsonSerializer` to create new objects containing only selected fields, then
sets `DocumentType=SemanticRerankingDocumentType.Json` and `TargetPaths`.
It never serializes a whole SearchRow or Cosmos document. Unselected fields,
IDs, embeddings, and system properties do not go to inference. JSON targeting
changes only reranking, not the retrieval query.

[`RerankOptions.Validate`](src/SemanticReranker.Core/Models.cs) and
`QueryBuilder.Validate` enforce the whitelist and limit on the server.
The UI retrieves at most 10 candidates; exactly `min(requested K, candidates)`
must be returned after reranking. Fewer matches are normal. Zero candidates skip
inference. Separate retrieved and returned counts prevent confusing a reranking
limit with a query limit. Reranker-off requests ignore even invalid advanced
form values rather than using them to change retrieval.

[`AzureReranker.MapResponse`](src/SemanticReranker.Core/AzureReranker.cs) reads
native `SemanticRerankingScore.SentenceScores` entries through `Index` and `Score`.
Requested diagnostics are validated and shown per result in expandable details.
These are **service indices**, not established offsets into original text or JSON
fields. No source sentence splitting, highlighting, or field alignment is done.
Absent or empty optional diagnostics display **not returned**, not invented zero
scores. The service may return model/version/token metadata, but this bounded UI
does not display it or require its presence.

Duplicate/out-of-range indices, wrong cardinality, missing/nonfinite scores, and
malformed requested sentence diagnostics fail closed. Errors never masquerade as
successful reranking with retrieval fallback.

## Timing, cost, data, and troubleshooting

The displayed Cosmos request charge is retrieval RU across pages, not tokens
or a total monetary cost. Embedding and reranking invoke separate services.
Their elapsed times are separate from retrieval and total end-to-end time;
Azure OpenAI tokens and inference usage are not included in Cosmos RU.

The actual dataset is [`movies.original.json`](../python/sample-data/movies.original.json):
ten short original records with stable `original-movies-v1-` IDs and real
embedding provenance. The deployment's authorized seed path supplies it.
Synthetic fixtures in the legacy tests verify contracts, not embedding quality.

| Symptom | Check |
| --- | --- |
| Fewer than K results | Retrieved count, dataset size, all-keyword filtering, index propagation. Reranking cannot create candidates. |
| No matching documents | Short matching keywords, another mode/index. Inference was not called. |
| Missing sentence scores or metadata | Optional output is service-dependent. Only requested sentence diagnostics are displayed. |
| Embedding configuration error | `AZURE_OPENAI_ENDPOINT`, deployment, matching model/dimensions, Entra role. Try full-text to isolate retrieval. |
| Reranker authorization/token error | Correct generated inference endpoint, tenant, credential selection, Semantic Reranker User and propagation. Restart after fixing settings/auth. |
| Cosmos error | Existing database/container names, Cosmos data role, network, index readiness. App requests do not repair resources. |
| Invalid reranker response | Treat as a failure. Do not fabricate scores or label retrieval fallback as reranked. |
| Config appears unchanged | Stop the old process, reload root `.local/azd-env.ps1`, restart. This app does not load `.env`. |
| Build output locked or port busy | Stop only the supervisor/manual process you own, then rebuild or choose another port. |

## Verification map

From the repository root, after stopping owned apps before the build:

```powershell
dotnet build dotnet/SemanticReranker.sln --no-restore
dotnet test dotnet/SemanticReranker.sln --no-build --filter 'Category!=Live'
. ./.local/azd-env.ps1
$env:RUN_LIVE_TESTS = '1'
dotnet test dotnet/SemanticReranker.sln --no-build --filter 'FullyQualifiedName~OriginalMovieLiveTests'
Remove-Item Env:RUN_LIVE_TESTS
```

[`AdvancedRerankingTests`](tests/SemanticReranker.Tests/AdvancedRerankingTests.cs)
covers serialization, subsets, validation, diagnostics, and option propagation.
[`WebTests`](tests/SemanticReranker.Tests/WebTests.cs) exercises HTTP form binding,
server validation, errors, and HTML encoding without Azure.
[`OriginalMovieLiveTests`](tests/SemanticReranker.Tests/OriginalMovieLiveTests.cs)
covers the actual dataset and advanced service cases, without data writes.

For browser verification, restart both apps using the root launcher. In a second
PowerShell terminal at the repository root:

```powershell
. ./.local/azd-env.ps1
$py = if ($IsWindows) { './python/.venv/Scripts/python.exe' } else { './python/.venv/bin/python' }
& $py -m pip install -r python/requirements-browser.txt
& $py -m playwright install chromium
$env:RUN_LIVE_TESTS = '1'
& $py python/scripts/verify_browser.py --include-dotnet
Remove-Item Env:RUN_LIVE_TESTS
```

The [shared browser harness](../python/scripts/verify_browser.py) preserves the
baseline mode/toggle matrix and adds targeted advanced cases for both indexes,
limits, optional details, and reranker-off behavior. It does not start/stop apps
or seed data. Screenshots stay in ignored `python/.local/`.
For an advanced-only rerun, append `--matrix advanced` to the browser command.
Use `--matrix baseline` for only the original cases. The default runs both;
all selections still require `RUN_LIVE_TESTS=1`.
