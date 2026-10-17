# Agent instructions

## Scope

- Keep language implementations independent: `dotnet/` and `python/`.
- Shared public setup lives in `infra/`, `scripts/`, and root documentation.
- Never change another agent's language subtree without explicit handoff.
- Keep endpoints and identities only in ignored `.azure/`, generated `.env`,
  or `.local/`; store logs, generated fixtures and receipts under `.local/`.
  Never commit secrets or private package feeds.
- Preserve MIT attribution. Synthetic fixtures must be original and bounded.

## Commands from the repository root

```powershell
dotnet restore dotnet/SemanticReranker.sln
dotnet build dotnet/SemanticReranker.sln --no-restore
dotnet test dotnet/SemanticReranker.sln --no-build --filter 'Category!=Live'
az bicep build --file infra/main.bicep --stdout
git diff --check
```

Use a package source override only per command when required by the host.
Never add private feeds to tracked configuration or generated lockfiles.
The public standalone package is `Azure.Data.AI`, not `Azure.AI.Inference`
and not a method on `Microsoft.Azure.Cosmos.Container`.

## Authorized writes

### Default azd lifecycle

- `preup`: bootstrap dependencies, persist/reuse `AZURE_COSMOS_ACCOUNT_NAME`,
  and reset `AZURE_CREATE_INFERENCE=false`. No target guessing, authentication,
  or Azure requests in this phase.
- `preprovision`: reset stale authorization, then use azd's resolved subscription
  and location for the mandatory authenticated inference GET. Only HTTP 404
  permits creation. Existing compatible accounts must not receive another PUT.
- `postprovision`: generate private local configuration only.
- `postup`: generate real embeddings and seed the bounded original dataset.
  Azure OpenAI is provisioned by the default infrastructure, not an optional
  manual prerequisite for this path.

- Default `azd up` explicitly authorizes `postup` to seed only the ten tracked
  original movie records in `python/sample-data/movies.original.json` into all
  four dedicated sample containers with real embeddings. Stable
  `original-movies-v1-` IDs and ownership markers protect unrelated records.
  No deletes or synthetic replacements. `postprovision` remains config-only.
  Root `pwsh ./scripts/start.ps1` supervises both local apps without data writes.
  This default path supersedes the older manual quickstart below.

- App startup and web requests: retrieval and inference only; no DDL or writes.
- Legacy manual loader: explicit `--existing-only --max-items 3 --confirm-write`; stable
  document IDs, no more than the allowed bound, no deletes. Leave live data.
- Dedicated .NET containers: `reranker_dotnet_qflat` and
  `reranker_dotnet_diskann`. Do not write Python fixtures/containers.
- Azure setup requires an operator's explicit authorization. Scripts may
  create missing sample containers at 400 RU/s each, never change an existing
  offer/policy. The existing-resource scripts never deploy a Cosmos account.
- The separate authorized `azure.yaml` / `infra/azd/` path creates a new
  dedicated group, GUID-named Cosmos/inference accounts, derived-name keyless
  OpenAI account/deployment, database and containers.
  Read [the azd prerequisites](python/infra/azd/README.md) before running it.
  Preserve the persisted letter-leading UUID. Never bypass preprovision:
  inference creation is permitted only after an authenticated HTTP 404.
  Default: `azd up`, then `pwsh ./scripts/start.ps1`. No manual seed is required.
  Advanced `azd provision` refreshes infrastructure/config without seeding.
  Do not run `azd down` during testing; report every created account, including
  partial deployments. Do not add unrelated resources to the azd-owned group.
- In the existing-resource manual path, companion creation and scoped role
  assignments are separate opt-in actions.
- Never deploy paid App Service resources, commit, or push without approval.

## Live verification

Read [dotnet/README.md](dotnet/README.md) before running the loader or live tests.
`RUN_LIVE_TESTS=1` enables real service calls; missing configuration must fail.
For the default path, repeat `azd up`, verify effective ARM `createInference`
is true on fresh creation and false on repeat, then run the real-embedding
matrices and browser harness. For the separate legacy fixture tests, run only
their guarded loader twice against the intended legacy environment.
Report command output and limitations separately from
offline test results. Never substitute a mocked success for service evidence.
