# Azure Cosmos DB semantic search samples

> Advanced legacy/manual workflow. For automatic real embeddings, use
> [the default azd up quickstart](README.md). Synthetic integration fixtures
> below are optional and are never substituted for the default movie embeddings.
> Current azd infrastructure still provisions keyless OpenAI. Its quota and cost
> prerequisites apply even when these manual tests use preembedded fixtures.

Search Azure Cosmos DB for NoSQL, then optionally rerank the retrieved
documents with a **separate inference client**. Cosmos retrieval and
inference use distinct endpoints and permissions.

| Implementation | Entry point | Setup |
| --- | --- | --- |
| .NET 10, ASP.NET Core Razor Pages | `dotnet/src/SemanticReranker.Web` | [Detailed .NET guide](dotnet/README.md) |
| Python, Streamlit | `python/` | [Standalone Python guide](python/README.md) |

The .NET app offers vector search, full-text all/any matching, full-text
ranking and hybrid RRF ranking. Each mode can rerank candidates. Results
retain document IDs, titles and text, with retrieval rank, available vector
distance, rerank score, timings and total Cosmos request charge.

## Legacy manual integration setup: azd backend, local Python and .NET

Read the **[complete prerequisite table](python/infra/azd/README.md#all-prerequisites)**,
including installed tools and already-authorized Azure access. Root setup needs
Git, azd (implementation baseline 1.25.6), Azure CLI, PowerShell 7 on all OSes,
Python 3.10+/venv/pip, .NET 10 SDK, public package/network access with trusted
TLS, and subscription privileges for resource creation and role assignments.
Both `azd auth login` and `az login` are needed. Docker and OpenAI are not
required for the synthetic fixtures, full-text retrieval or reranking.

Run from a clean clone's **repository root**, in PowerShell 7:

```powershell
python -m venv python/.venv
$py = if ($IsWindows) { './python/.venv/Scripts/python.exe' } else { './python/.venv/bin/python' }
& $py -m pip install -r python/requirements.txt -r python/requirements-dev.txt
& $py -m pytest -q python/tests/unit python/tests/ui
dotnet restore dotnet/SemanticReranker.sln
dotnet build dotnet/SemanticReranker.sln --no-restore
dotnet test dotnet/SemanticReranker.sln --no-build --filter 'Category!=Live'
az bicep build --file infra/azd/main.bicep --stdout

$tenant = Read-Host 'Tenant ID'
$subscription = Read-Host 'Subscription ID'
azd auth login --tenant-id $tenant
az login --tenant $tenant
az account set --subscription $subscription
azd init --environment reranker-combined-dev
azd env set AZURE_SUBSCRIPTION_ID $subscription
azd env set AZURE_LOCATION uksouth
azd env select reranker-combined-dev
# Must print false before the FIRST provision. Choose another unused name otherwise.
az group exists --name rg-reranker-reranker-combined-dev --subscription $subscription
azd provision
if ($LASTEXITCODE -ne 0) { throw 'Provisioning or configuration hook failed' }
. ./.local/azd-env.ps1
```

On Linux/macOS create the venv with `python3` and run these commands in
`pwsh`. Change the environment name consistently if it is already in use.
Use different names for root and standalone `python/` deployments.

The isolated `infra/azd/` path provisions a new dedicated resource group,
same-name GUID Cosmos and inference accounts, database `reranker`, and four
containers: `search_qflat`, `search_diskann`, `reranker_dotnet_qflat`,
`reranker_dotnet_diskann`. Each has vector and full-text policies and
dedicated manual **400 RU/s**, for **1600 RU/s total**, plus storage and
inference charges until cleanup. Accounts are keyless; the signed-in azd
user receives scoped Cosmos Data Contributor and Semantic Reranker User.
The preprovision hook persists a canonical lowercase GUID starting **a-f**
for both accounts. It adopts a valid existing account output when present,
and reuses the persisted name on reprovision. Preserve ignored azd state.
Failed provisioning can leave paid partial resources; see
[naming preflight and explicit recovery](python/infra/azd/README.md#naming-preflight-and-failed-provisioning).
No failed resources are automatically deleted or rolled back.

There are **no hosted services**. This manual workflow uses `azd provision`;
`azd up` has no cloud app deployment to perform. Previous existing accounts
are not referenced or modified. The postprovision hook automatically
configures `python/.env`, `python/.local/azd-env.ps1` and root
`.local/azd-env.ps1`. It preserves unrelated Python `.env` settings.
Dot-source root config for **both** apps; no endpoint copying is needed.
.NET does not read `.env`. The hook cannot change the calling shell,
and Python process variables take precedence over `.env`.

Explicitly load the original synthetic fixtures using generated config:

```powershell
& $py python/scripts/make-fixtures.py
& $py python/src/data/data-loader.py `
  --path_to_json_array python/tests/fixtures/movies.synthetic.json `
  --text_field_name text --vector_field_name embedding --re_embed False `
  --existing-only --max-items 3 --confirm-write --concurrency 1
if ($LASTEXITCODE -ne 0) { throw 'Python fixture loading failed' }
pwsh scripts/New-SyntheticFixture.ps1
dotnet run --no-build --project dotnet/tools/DataLoader -- `
  --path .local/fixtures/movies.dotnet.preembedded.json `
  --existing-only --max-items 3 --confirm-write
if ($LASTEXITCODE -ne 0) { throw '.NET fixture loading failed' }
# Repeat both loaders to check stable-ID upserts, without deleting anything.
$env:RUN_LIVE_TESTS = '1'
$env:ALLOW_FIXTURE_UPSERT = '1'
& $py -m pytest -q python/tests/live --ignore=python/tests/live/test_original_movies.py
dotnet test dotnet/SemanticReranker.sln --no-build `
  --filter 'Category=Live&FullyQualifiedName~SemanticReranker.Tests.LiveTests.'
Remove-Item Env:RUN_LIVE_TESTS, Env:ALLOW_FIXTURE_UPSERT
```

Allow role and index propagation before live tests. Launch one app per
terminal, dot-sourcing `. ./.local/azd-env.ps1` in **each** terminal first:

```powershell
# Terminal 1, repository root:
$py = if ($IsWindows) { './python/.venv/Scripts/python.exe' } else { './python/.venv/bin/python' }
& $py -m streamlit run python/src/app/cosmos-app.py --server.port 8501
# Terminal 2, repository root:
dotnet run --no-build --no-launch-profile `
  --project dotnet/src/SemanticReranker.Web --urls http://127.0.0.1:8503
```

Open those URLs, select both indexes and try full-text retrieval/ranking
with reranking off/on. Optional real embedding configuration is described
in each language guide. [Real browser validation](dotnet/README.md#real-browser-validation)
is an additional opt-in check, not implied by a health response.

For switching, stop apps, `azd env select <name>`, `azd env refresh`,
`pwsh scripts/postprovision.ps1`, dot-source again and restart apps.
See [resource inventory, repeat provisioning and cleanup](infra/azd/README.md).
Do not run `azd down` in tests: it destructively removes the selected
environment's dedicated resource group and data. Leave resources for review.

## Alternative: start with .NET against existing resources

Prerequisites: .NET 10 SDK, Azure CLI, PowerShell 7 for setup scripts, an
existing Cosmos account and database with vector/full-text support, and an
available companion inference account. The SDK package is explicitly pinned
to [`Azure.Data.AI` `1.0.0-beta.1`](https://www.nuget.org/packages/Azure.Data.AI/1.0.0-beta.1).
It is a preview dependency.

```powershell
az login
dotnet restore dotnet/SemanticReranker.sln
dotnet build dotnet/SemanticReranker.sln --no-restore
dotnet test dotnet/SemanticReranker.sln --no-build --filter 'Category!=Live'
```

Set the environment described in [dotnet/.env.example](dotnet/.env.example).
The app does not automatically read `.env` files. Export variables in your
shell or deployment environment. Keep actual settings under ignored `.local/`,
not in source control.

Then follow these distinct operations, in order:

1. **Provision explicitly:** [infrastructure guide](infra/README.md). Reuse
   the Cosmos account/database and companion inference service. The container
   script creates only missing dedicated .NET containers, each at manual
   **400 RU/s**, after explicit confirmation.
2. **Load explicitly:** materialize the original synthetic fixture and run
   the guarded loader. It validates all documents and both existing container
   policies before the first upsert. It does not create resources.
3. **Run:** `dotnet run --project dotnet/src/SemanticReranker.Web --urls http://127.0.0.1:8503`.
   Visit that URL. Startup does not access Azure or mutate data.

```powershell
pwsh scripts/New-SyntheticFixture.ps1
dotnet run --project dotnet/tools/DataLoader -- `
  --path .local/fixtures/movies.dotnet.preembedded.json `
  --existing-only --max-items 3 --confirm-write
```

Full-text retrieval, standalone reranking and preembedded fixture loading do
**not** require OpenAI. Vector/hybrid UI searches require a real compatible
embedding deployment. Synthetic fixture vectors validate the schema and
query execution, not semantic embedding quality.

## Validation and deployment

- [Offline, live and browser commands](dotnet/README.md#validation)
- [Existing-resource setup, scoped roles and boundaries](infra/README.md)
- [App Service publishing, runtime and managed identity](dotnet/README.md#app-service-deployment)
- [Agent commands and write boundaries](AGENTS.md)

Tests with doubles are not live-service evidence. Live suites are opt-in and
fail if enabled prerequisites are missing. There is no automatic data deletion or App Service deployment. The azd path
owns only its new environment resources; the existing-resource scripts do
not redeploy a Cosmos account/database or recreate containers.

This is a small sample, not a public multi-user search gateway. Protect a
deployed app with authentication and access restrictions before exposing a
managed identity that can query private data.

## License

[MIT](LICENSE). See [NOTICE.md](NOTICE.md) for source attribution and the
original synthetic fixture notice. No third-party movie corpus is included.