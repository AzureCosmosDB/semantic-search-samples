# Standalone semantic reranker samples

Search Azure Cosmos DB for NoSQL, then optionally rerank the retrieved
documents with a **separate inference client**. Cosmos retrieval and
inference use distinct endpoints and permissions.

| Implementation | Entry point | Setup |
| --- | --- | --- |
| .NET 10, ASP.NET Core Razor Pages | `dotnet/src/SemanticReranker.Web` | [Detailed .NET guide](dotnet/README.md) |
| Python, Streamlit | `python/` | `python/README.md` after the separately verified migration is added |

The .NET app offers vector search, full-text all/any matching, full-text
ranking and hybrid RRF ranking. Each mode can rerank candidates. Results
retain document IDs, titles and text, with retrieval rank, available vector
distance, rerank score, timings and total Cosmos request charge.

## Start with .NET

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
fail if enabled prerequisites are missing. There is no automatic deletion,
rollback, container recreation, Cosmos account redeployment or App Service
deployment.

This is a small sample, not a public multi-user search gateway. Protect a
deployed app with authentication and access restrictions before exposing a
managed identity that can query private data.

## License

[MIT](LICENSE). See [NOTICE.md](NOTICE.md) for source attribution and the
original synthetic fixture notice. No third-party movie corpus is included.