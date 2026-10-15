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

- App startup and web requests: retrieval and inference only; no DDL or writes.
- Loader: explicit `--existing-only --max-items 3 --confirm-write`; stable
  document IDs, no more than the allowed bound, no deletes. Leave live data.
- Dedicated .NET containers: `reranker_dotnet_qflat` and
  `reranker_dotnet_diskann`. Do not write Python fixtures/containers.
- Azure setup requires an operator's explicit authorization. Scripts may
  create missing sample containers at 400 RU/s each, never change an existing
  offer/policy. The existing-resource scripts never deploy a Cosmos account.
- The separate authorized `azure.yaml` / `infra/azd/` path creates a new
  dedicated group, GUID-named Cosmos/inference accounts, database and containers.
  Read [the azd prerequisites](python/infra/azd/README.md) before running it.
  Preserve the persisted letter-leading UUID. Never bypass preprovision:
  inference creation is permitted only after an authenticated HTTP 404.
  Use `azd provision`, then dot-source `. ./.local/azd-env.ps1` for both apps.
  Do not run `azd down` during testing; report every created account, including
  partial deployments. Do not add unrelated resources to the azd-owned group.
- Companion creation and scoped role assignments are separate opt-in actions.
- Never deploy paid App Service resources, commit, or push without approval.

## Live verification

Read [dotnet/README.md](dotnet/README.md) before running the loader or live tests.
`RUN_LIVE_TESTS=1` enables real service calls; missing configuration must fail.
Run the loader twice, then test readback, retrieval, standalone reranking and
browser behavior. Report command output and limitations separately from
offline test results. Never substitute a mocked success for service evidence.
