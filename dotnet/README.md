# .NET semantic search and standalone reranking

This is a small ASP.NET Core Razor Pages equivalent of the original
Streamlit search workflow. It uses `Microsoft.Azure.Cosmos` for retrieval and
the separately pinned public GA `Azure.Data.AI` `1.0.0` for reranking.
It does not call a reranking method on the Cosmos client.
See the [search guide](SEARCH_GUIDE.md) for manual startup, feature walkthroughs,
and the collapsed advanced JSON targeting, result limit and sentence diagnostics.

`Azure.Data.AI` `1.0.0` declares `Azure.Core` `1.65.0` as its minimum
dependency in the public NuGet package, so Core is also pinned to `1.65.0`.
That Core package includes the credential classes in the `Azure.Identity`
namespace. Do not add the older standalone `Azure.Identity` `1.17.1` package:
it defines duplicate credential types and causes compiler error CS0433.
The code still uses `using Azure.Identity;` and `DefaultAzureCredential`;
no alias or replacement authentication flow is needed.

## Requirements and layout

Recommended for a clean clone: [root azd up quickstart](../README.md#deploy-and-start).
It restores the full MovieLens 4,489-source-row corpus with real 1536-dimensional
embeddings. Duplicate source movie IDs survive in deterministic row-indexed
document IDs; original metadata/IDs and source/model provenance are retained.
The [root restoration contract](../README.md#movielens-source-and-migration-boundary)
covers pinned bytes/checksum, resumable embedding cache, full readback and the
explicit conditional cleanup of only the ten unchanged old fixture records.
The home summary describes the source corpus, not a live custom-container count.
The root launcher starts both
apps. Use `OriginalMovieLiveTests` and `python/scripts/verify_browser.py --include-dotnet`
for the new real-embedding matrix. The older manual/synthetic validation below
remains a separate integration and error-path workflow.
It provisions the backend and generates `.local/azd-env.ps1`. Dot-source
that script from the repository root before running this guide's loaders,
apps or live tests. Do not replace generated endpoints with the manual
placeholders below. The existing-resource route remains an alternative.

- .NET 10 SDK. `global.json` allows the latest installed .NET 10 feature band.
- Existing Cosmos DB for NoSQL account/database with vector/full-text support.
- Existing compatible containers and a companion inference endpoint.
- Azure CLI sign-in for local `DefaultAzureCredential`, or a managed identity.
- PowerShell 7 for setup/fixture generation; Python 3.10+ only for optional
  Playwright browser tests, not for running the .NET app.

```text
dotnet/
  src/SemanticReranker.Core/    typed queries, SDK adapter, guarded loader logic
  src/SemanticReranker.Web/     Razor Pages UI
  tools/DataLoader/            explicit bounded preembedded upserts
  tests/SemanticReranker.Tests/ unit, HTTP and opt-in live tests
  tests/browser/               real Playwright UI validation
```

Run commands below from the **repository root** unless stated otherwise.

## Restore, build and offline tests

```powershell
dotnet restore dotnet/SemanticReranker.sln
dotnet build dotnet/SemanticReranker.sln --no-restore
dotnet test dotnet/SemanticReranker.sln --no-build --filter 'Category!=Live'
```

Use public NuGet by default. If host network policy requires an approved
alternate source, apply it to the restore command only. Do not add private
source URLs to tracked NuGet configuration, documentation or lockfiles.
Never disable TLS validation. Build outputs and NuGet assets under `obj/`
are ignored.

## Local environment and authentication

First perform authorized [explicit setup](../infra/README.md). Then export:

```powershell
az login
$env:COSMOS_DB_ENDPOINT = 'https://<account>.documents.azure.com:443/'
$env:COSMOS_DB_DATABASE = '<existing-database>'
$env:COSMOS_DB_CONTAINER_QFLAT = 'reranker_dotnet_qflat'
$env:COSMOS_DB_CONTAINER_DISKANN = 'reranker_dotnet_diskann'
$env:INFERENCE_ENDPOINT = 'https://<inference-endpoint-from-resource>'
```

On bash, use `export NAME='value'` for the same variables. The app does not
automatically load `.env`. See [.env.example](.env.example) for all names.
Save actual shell settings under `.local/`, never in a tracked file.
`DefaultAzureCredential` is shared. There is no Cosmos account-key path.
An optional `AZURE_CLIENT_ID` selects a user-assigned managed identity.

Defaults are Direct mode for Cosmos and the `aisearch-reranker` model.
Set `COSMOS_CONNECTION_MODE=Gateway` only if your network requires HTTPS-only
Cosmos connections. No certificate-validation bypass is supported.

### Optional embeddings

Only vector/hybrid UI requests require:

```powershell
$env:AZURE_OPENAI_ENDPOINT = 'https://<openai-account>.openai.azure.com/'
$env:AZURE_OPENAI_EMBEDDING_DEPLOYMENT = '<1536-dimensional-deployment>'
```

The REST adapter explicitly requests 1536 dimensions and validates exactly
1536 finite float values. Use a compatible text-embedding-3 deployment.
Prefer Entra authentication with an OpenAI data role. An optional
`AZURE_OPENAI_API_KEY` is used only outside generated `AZURE_OPENAI_AUTH_MODE=entra`
configuration. The default azd path remains keyless. API version
defaults to `2024-02-01`, configurable with `AZURE_OPENAI_API_VERSION`.
No OpenAI client/request is created for full-text retrieval, reranking or
preembedded loading. Missing embedding configuration is a visible UI error,
not a fake vector result.

## Bounded preembedded fixture loading

The original stories in `fixtures/dotnet/movies.synthetic.json` include an
`embeddingAxis` recipe. Materialize the full 1536-float arrays locally:

```powershell
pwsh scripts/New-SyntheticFixture.ps1
dotnet run --no-build --project dotnet/tools/DataLoader -- `
  --path .local/fixtures/movies.dotnet.preembedded.json `
  --existing-only --max-items 3 --confirm-write
```

The generated file is ordinary preembedded data:

```json
[
  {
    "id": "stable-id",
    "title": "Original title",
    "text": "Original description",
    "embedding": ["replace this illustration with 1536 numeric float values"]
  }
]
```

The illustration is not loadable data. Use the materializer or supply a
valid JSON array. The loader does not embed, truncate, pad or silently
repair vectors. It validates the whole file, item count, IDs and vectors
before writes, then verifies both container policies before the first
upsert. It reads back every item, including the full embedding.

There are three stable synthetic IDs per container. Run the exact command
twice to verify idempotent upserts; a second run must still produce six
successful exact readbacks, not additional documents. The hard bound is
nine documents per invocation, and `--max-items` must be explicit.
SDK throttling retries are bounded; other errors fail the CLI with exit
code 1. A partial failure may leave some documents upserted. Re-run the same
IDs after fixing the issue. No rollback, deletion or cleanup is attempted.

Only the dedicated .NET containers should be used for these tests. Do not
reuse another language agent's containers. The loader gets database and
container names from environment, not a `--database` switch.

The synthetic basis vectors are integration fixtures. With only a few
items, the service may use a scan rather than build/use a vector index.
A successful direct vector/hybrid query does not establish index usage,
performance, embedding quality or production relevance.

## Run the UI

```powershell
dotnet run --no-build --no-launch-profile `
  --project dotnet/src/SemanticReranker.Web --urls http://127.0.0.1:8503
```

Open `http://127.0.0.1:8503`:

1. Enter `ocean marine`.
2. Select Quantized flat or DiskANN and Full-text search.
3. Choose all/any keywords and turn semantic reranking off or on.
4. Search. Inspect IDs, text, retrieval rank, optional vector distance,
   rerank score, request charge and timings.

Full-text ranking uses `ORDER BY RANK FullTextScore`; hybrid uses
`ORDER BY RANK RRF(FullTextScore(...), VectorDistance(...))`.
Full-text and hybrid scores are not fabricated or projected as numbers.
Vector distance is lower-is-better; rerank score is higher-is-better.
The all/any selector only affects Full-text search.

RU is summed across every retrieval page. Timings are client wall-clock
milliseconds for retrieval, optional embedding, optional reranking and the
whole operation, not server execution time. Reranking costs are not Cosmos
request charge. Empty retrieval does not invoke inference.

SQL full-text literals are JSON escaped, and vectors use a SQL parameter.
Inputs and results are HTML encoded by Razor. POSTs retain antiforgery
protection. SDK/service errors are displayed without raw endpoint, token
or exception details. Failed reranking does not return original results
under a success label. Nullable, duplicate, negative and out-of-range
indices and non-finite scores are rejected before mapping.

Clients are shared: one Cosmos client, one lazily created inference client,
one HTTP embedding client and one credential. The host disposes the Cosmos
and HTTP clients on shutdown. The inference SDK client exposes no disposable
resource contract. Startup and GET `/healthz` do not contact Azure; health
is process health, not proof of configured-service access.

## Validation

### Offline tests

```powershell
dotnet test dotnet/SemanticReranker.sln --no-build --filter 'Category!=Live'
```

Tests cover query construction, SQL escaping, reranking index/score
validation, duplicate text and metadata preservation, optional embeddings,
loader guards/policies/readback/idempotence/partial failures and real local
HTTP form/antiforgery/error rendering. HTTP tests use service doubles and
are not proof of Azure connectivity.

### Real integration tests

Configure the dedicated .NET containers, run the loader twice, then:

```powershell
$env:RUN_LIVE_TESTS = '1'
dotnet test dotnet/SemanticReranker.sln --no-build `
  --filter 'Category=Live&FullyQualifiedName~SemanticReranker.Tests.LiveTests.'
```

Without the opt-in flag, live tests are reported as skipped. With it, missing
configuration or credentials is a failure. The live suite is read-only
against Cosmos. It checks exact fixture readback, all four query modes,
full-text all/any, actual standalone inference, finite scores, metadata,
and a clearly relevant first item. Vector/hybrid queries pass the fixture
vector directly and are query-execution tests, not OpenAI-quality tests.
Do not enable live suites against arbitrary production datasets.

### Real browser validation

```powershell
python -m venv .local/browser-venv
$browserPython = '.local/browser-venv/Scripts/python.exe'
# bash: browserPython=.local/browser-venv/bin/python
& $browserPython -m pip install -r dotnet/tests/browser/requirements.txt
& $browserPython -m playwright install chromium
$env:RUN_LIVE_TESTS = '1'
& $browserPython dotnet/tests/browser/live_ui.py
```

The script starts its own .NET process on an available loopback port,
checks HTTP health, performs real full-text searches and inference with
reranking on/off in both containers, then checks visible missing-OpenAI
errors for vector/hybrid. It deliberately clears OpenAI settings in its
child process for that deterministic error test. It stops only its own
process tree. Logs/screenshots remain under `.local/validation/`.
It does not claim vector/hybrid browser success with a real embedding.

Record exact commands, outputs and any blocked cases in a private receipt.
Do not publish endpoint-bearing logs or treat skipped live tests as passes.

## App Service deployment

These commands are an **operator-run, paid deployment procedure**. They are
not part of setup/tests and must not be run without authorization. Use a
Linux App Service plan and a separate web app. Cosmos account/database and
inference remain existing resources.

### Publish code and select runtime

```powershell
$app = '<unique-web-app-name>'
$plan = '<app-service-plan>'
$location = '<app-service-region>'
# Use the same $subscription/$resourceGroup/$account/$database variables as infra/README.md.

az webapp list-runtimes --os linux
# Confirm DOTNETCORE:10.0 is available before requesting this runtime.
az appservice plan create --subscription $subscription --resource-group $resourceGroup `
  --name $plan --location $location --is-linux --sku B1
az webapp create --subscription $subscription --resource-group $resourceGroup `
  --plan $plan --name $app --runtime 'DOTNETCORE:10.0'

dotnet publish dotnet/src/SemanticReranker.Web -c Release `
  --output .local/publish/dotnet
Compress-Archive -Path .local/publish/dotnet/* -DestinationPath .local/publish/dotnet-web.zip -Force
az webapp config set --subscription $subscription --resource-group $resourceGroup `
  --name $app --linux-fx-version 'DOTNETCORE|10.0' `
  --startup-file 'dotnet SemanticReranker.Web.dll --urls http://0.0.0.0:8080'
az webapp config appsettings set --subscription $subscription --resource-group $resourceGroup `
  --name $app --settings SCM_DO_BUILD_DURING_DEPLOYMENT=false ASPNETCORE_ENVIRONMENT=Production
```

The ZIP must contain the published DLL, runtime/dependency files, Razor
assemblies and `wwwroot` at its root, not a nested publish folder.
On Linux/macOS, use a zip utility from inside the publish directory.
If the managed .NET 10 runtime is not offered in your target environment,
stop and choose a supported deployment environment or a reviewed
self-contained Linux/container deployment. Do not silently choose a .NET 8
runtime for a `net10.0` build.

### Managed identity and separate data roles

```powershell
$principal = az webapp identity assign --subscription $subscription `
  --resource-group $resourceGroup --name $app --query principalId -o tsv

pwsh scripts/Initialize-Inference.ps1 `
  -SubscriptionId $subscription -ResourceGroup $resourceGroup `
  -CosmosAccountName $account -DatabaseName $database `
  -AssignRoles -PrincipalId $principal -PrincipalType ServicePrincipal `
  -CosmosAccess Reader -ConfirmChanges

az webapp config appsettings set --subscription $subscription --resource-group $resourceGroup `
  --name $app --settings `
  COSMOS_DB_ENDPOINT=$env:COSMOS_DB_ENDPOINT COSMOS_DB_DATABASE=$database `
  COSMOS_DB_CONTAINER_QFLAT=reranker_dotnet_qflat `
  COSMOS_DB_CONTAINER_DISKANN=reranker_dotnet_diskann `
  INFERENCE_ENDPOINT=$env:INFERENCE_ENDPOINT RERANKER_MODEL=aisearch-reranker
```

The web identity gets Cosmos data **Reader**, not Contributor, on the two
sample containers, and Semantic Reranker User on the inference account.
Do not give the app management-plane Owner/Contributor or account keys.
If using embeddings, set the two OpenAI settings and separately grant the
identity an OpenAI data role. Keep keys in Key Vault references if you
deliberately choose key authentication instead.

Configure App Service Authentication (Microsoft Entra), require authenticated
requests, restrict allowed users and network access, and configure HTTPS-only
before deploying/exposing this sample. For private service endpoints,
configure App Service VNet integration and DNS separately. Do not expose
an anonymous managed-identity proxy to private Cosmos data.

```powershell
az webapp update --subscription $subscription --resource-group $resourceGroup --name $app --https-only true
# Deploy only after identity, authentication, network and settings review:
az webapp deploy --subscription $subscription --resource-group $resourceGroup `
  --name $app --src-path .local/publish/dotnet-web.zip --type zip
```

Load fixtures with the separate authorized operator/loader, not by running
DDL at web startup. Verify authenticated GET and a real text search after
deployment. Health alone is insufficient.

## Troubleshooting and limits

- **403:** Cosmos data and inference roles are separate. Check object IDs,
  container scopes, inference scope and propagation. Management Contributor
  is not a substitute for either data role.
- **Container missing/policy mismatch:** use explicit setup. Do not recreate
  resources or silently change throughput.
- **Empty text results immediately after loading:** allow indexing to catch up,
  then rerun the read-only query. Never turn an empty result into a fake pass.
- **SDK changes:** this is the pinned public GA package. Use its actual
  `InferenceClient.SemanticRerankAsync(SemanticRerankingContent, ...)`,
  `SemanticRerankingResult.Scores`, `Metadata`, nullable `Index` and nullable
  float `Score`; populate the `TargetPaths` collection for JSON requests and validate mapping when
  upgrading. No legacy Cosmos semantic-rerank APIs are used.
- **Missing OpenAI settings on the manual path:** full-text and reranking still
  work, but vector/hybrid requests fail explicitly. The default azd path supplies
  the deployed embedding settings automatically.
- **Large workloads:** this bounded sample is not a bulk loader or benchmark.
  Add production observability, authorization, quotas and resilient UX before
  broader deployment.
