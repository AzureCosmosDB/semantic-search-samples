# Azure Cosmos DB semantic search samples

Run Python Streamlit and .NET Razor locally against Azure Cosmos DB for NoSQL,
with real Azure OpenAI embeddings and optional standalone semantic reranking.
The sample includes ten short, original movie-themed records, not a third-party corpus.

## Prerequisites

- Git, Azure CLI, Azure Developer CLI (azd baseline 1.25.6), PowerShell 7
  on Windows/macOS/Linux, Python 3.10+ with venv/pip, and .NET 10 SDK.
  On Linux/macOS the bootstrap uses `python3`; on Windows it uses `python`.
- Public PyPI/NuGet and Azure network access with trusted TLS.
  No Docker or hosted application compute is needed.
- Sign into **both** `az login` and `azd auth login` as the same Entra user.
  Select the same tenant/subscription. Tool installation and sign-in are not automated.
- Permissions to create resources and assign scoped roles. Register
  `Microsoft.DocumentDB`, `Microsoft.InferenceService`, and
  `Microsoft.CognitiveServices`. Confirm inference preview access and Cosmos quota.
- Available Azure OpenAI quota for `text-embedding-3-small`, version `1`,
  **GlobalStandard**, capacity `30`, in **UKSouth**. Availability varies by
  subscription. This is pay-per-token capacity, not provisioned throughput.
  GlobalStandard may process requests globally, not only in UKSouth.
- Accept ongoing charges: four containers at **400 RU/s each**, storage,
  inference usage and embedding tokens. Failed deployments may leave paid
  resources. Nothing automatically deletes resources.

See [all prerequisites and recovery](python/infra/azd/README.md) for details.

## Deploy and start

From the cloned repository root:

```powershell
azd up
pwsh ./scripts/start.ps1
```

Choose a fresh, unique environment name and the intended subscription when azd
prompts. Default Cosmos/inference region is UKSouth. Use only lowercase letters,
digits and hyphens in the environment name. Preserve ignored `.azure/` state.
No prior `azd init` or `azd env new` is required. For explicit selection, pass
`--environment <name> --subscription <id> --location uksouth` directly to `azd up`.

The launcher automatically loads generated root config and stays in the foreground:

- Streamlit: <http://127.0.0.1:8501>
- Razor: <http://127.0.0.1:8503>

Ctrl+C stops only the launcher's owned processes. Busy ports cause an error,
not a process kill. A child exit stops its sibling and fails the launcher.
Use `-PythonPort` and `-DotnetPort` to select other ports. Startup never seeds data.

Try **SPECTRE spy**, **undercover agent satellite codes**, or **astronaut rescue**.
For both indexes, try vector, full-text all/any, full-text ranking and hybrid,
with reranking off and on. The UI layout is unchanged.

## What azd up does

1. `preup` bootstraps dependencies and persists/reuses the account GUID.
   It resets inference creation to `false` without authentication or Azure calls.
   The service-free workflow then runs only native `azd provision`, not
   package/deploy steps. Mandatory `preprovision` uses azd's resolved
   subscription/location for the authenticated inference GET. Only HTTP 404
   authorizes creation. No early hook guesses CLI flags or uses the CLI's
   default subscription. Existing venv and incremental .NET outputs are reused.
2. Bicep provisions keyless Cosmos/inference and Azure OpenAI backends with
   scoped user roles. `postprovision` writes only private local configuration:
   `python/.env`, `python/.local/azd-env.ps1`, and `.local/azd-env.ps1`.
3. `postup` batches real embeddings from the deployed model, validates every
   vector and target policy, and upserts ten namespaced original records into
   each sample container. All search modes use this same embedding deployment.

`azd provision` refreshes infrastructure/config **without seeding**. Only
`azd up` runs the seed hook automatically. Repeating `azd up` keeps account names
and stable record IDs, regenerates real embeddings, and never deletes unrelated
documents. An occupied seed ID without the sample ownership marker fails closed.
Provenance records model, version, deployment, dimensions and source-text hash.
The bounded seed retries explicit role-propagation 403 responses, then fails.
Fix the cause and repeat `azd up`; a partial seed is safe to resume.

No keys, fake-vector fallback, hosted services, manual loader commands or
endpoint copying are required for this default path.

## Configuration and advanced workflows

Before provisioning, use `azd env set <variable> <value>`:

| Variable | Default |
| --- | --- |
| `AZURE_OPENAI_LOCATION` | `uksouth` |
| `EMBEDDING_MODEL` | `text-embedding-3-small` |
| `EMBEDDING_VERSION` | `1` |
| `EMBEDDING_DEPLOYMENT` | `text-embedding-3-small` |
| `EMBEDDING_SKU` | `GlobalStandard` |
| `EMBEDDING_CAPACITY` | `30` |

`text-embedding-3-large` is also supported with explicitly requested 1536 dimensions.
Choose a supported model/version/region/SKU combination and available quota.
Standard and DataZoneStandard are configurable where supported; their processing
boundaries differ from GlobalStandard. ARM errors for unavailable models or quota
are fatal. No automatic region change or throughput purchase occurs.
Changing the model requires rerunning `azd up` to re-embed sample records.
Do not mix embedding models in an existing dataset.

- [Standalone Python](python/README.md)
- [Existing-resource and manual integration workflows](README.manual.md)
- [.NET details and browser checks](dotnet/README.md)
- [Configuration switching and resource contract](infra/azd/README.md)

Stop apps before switching environments. Run `azd env select <name>`,
`azd env refresh`, `pwsh ./scripts/postprovision.ps1`, then restart the launcher.
Never commit generated configuration. Do not run `azd down` as a test.

## Verification

After `azd up`, in PowerShell:

```powershell
. ./.local/azd-env.ps1
$py = if ($IsWindows) { './python/.venv/Scripts/python.exe' } else { './python/.venv/bin/python' }
& $py -m pytest -q python/tests/unit python/tests/ui
dotnet test dotnet/SemanticReranker.sln --no-build --filter 'Category!=Live'
$env:RUN_LIVE_TESTS = '1'
& $py -m pytest -q python/tests/live/test_original_movies.py
dotnet test dotnet/SemanticReranker.sln --no-build --filter 'FullyQualifiedName~OriginalMovieLiveTests'
Remove-Item Env:RUN_LIVE_TESTS
```

These live suites do not inject synthetic query vectors. They cover both indexes,
all modes and rerank toggles, plus a modest SPECTRE top-three assertion for ranked
results. Allow index propagation, then investigate failures rather than disabling
modes. Repeat `azd up` and these checks to verify idempotency. Older synthetic
integration suites remain available through the manual workflow, under separate IDs.

For real browser verification, keep the launcher running in one terminal. In
another terminal with generated config loaded:

```powershell
& $py -m pip install -r python/requirements-browser.txt
& $py -m playwright install chromium
$env:RUN_LIVE_TESTS = '1'
& $py python/scripts/verify_browser.py --include-dotnet
Remove-Item Env:RUN_LIVE_TESTS
```

This visits both already-running apps, exercises the full mode/toggle matrix,
prints each successful case and saves screenshots only under
`python/.local/validation/original-movies/`. It neither starts nor stops apps.
Browser tooling is optional and is not installed during `azd up`.
