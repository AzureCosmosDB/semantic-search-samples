# Combined Python and .NET azd project

Run from the repository root with [the root quickstart](../../README.md).
This subscription-scope entry point calls the shared Python backend module
with `includeDotnet: true`, creating both Python and .NET container pairs.

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
