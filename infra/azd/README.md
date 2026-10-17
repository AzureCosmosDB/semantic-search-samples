# Combined Python and .NET azd project

Run from the repository root with [the root quickstart](../../README.md).
This subscription-scope entry point calls the shared Python backend module
with `includeDotnet: true`, creating both Python and .NET container pairs.
It also provisions a keyless Azure OpenAI account named `aoai-<persisted-GUID>`
and the configured embedding deployment. Both local apps and the bounded seed
use the same deployment.

## Default lifecycle

| Phase | Contract |
| --- | --- |
| `preup` | Bootstrap dependencies, persist/reuse `AZURE_COSMOS_ACCOUNT_NAME`, reset `AZURE_CREATE_INFERENCE=false`. No authentication, Azure requests or guessed subscription/location. |
| `preprovision` | Use azd's resolved selection for the mandatory authenticated inference GET. Only HTTP 404 authorizes creation; compatible existing accounts are reused without PUT. |
| `postprovision` | Generate private config only, including embedding settings. |
| `postup` | Batch real embeddings and upsert ten `original-movies-v1-` records into each dedicated container; preserve unrelated data. |

`azd up` performs this default flow. `pwsh ./scripts/start.ps1` loads root config
and supervises both apps without writes. No manual loader or prior `azd init`
or `azd env new` is required. Advanced `azd provision` intentionally does not seed.

Generated embedding settings include `AZURE_OPENAI_ENDPOINT`,
`AZURE_OPENAI_EMBEDDING_DEPLOYMENT`, `AZURE_OPENAI_EMBEDDING_MODEL`,
`AZURE_OPENAI_EMBEDDING_VERSION`, `AZURE_OPENAI_API_VERSION`, and
`AZURE_OPENAI_AUTH_MODE=entra`.

Read the complete [prerequisites, resource contract, configuration, switching
and cleanup guide](../../python/infra/azd/README.md). All prerequisites apply
here, including Python for the config hook and .NET 10 for the .NET app.

`infra/main.bicep` and the original setup scripts remain the separate
existing-resource alternative. They are not invoked by azd.

Use a different azd environment name for root and standalone `python/`.
They are separate projects with separate `.azure/` state. Root provisioning
refreshes `python/.env` as well as root `.local/azd-env.ps1`. Provisioning
from `python/` subsequently switches only Python to that standalone backend;
root config remains on its own backend until explicitly refreshed.
