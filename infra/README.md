# Explicit setup with existing Cosmos resources

For a new backend, use the recommended [azd path](azd/README.md).
This document describes only the original existing-resource alternative:
`infra/main.bicep` and `scripts/Initialize-*.ps1`. Those files do not use
`infra/azd/` and their deployment semantics are unchanged.

Nothing in this existing-resource procedure deploys the Cosmos account or database, changes account
capabilities, enables local keys, or deletes data. Use an existing database
in an account already configured for vector and full-text search. The data
and management planes have separate permissions.

PowerShell scripts require PowerShell 7, Azure CLI and an authenticated
operator. Resource group, subscription and names below are operator-supplied
placeholders. Store actual values in `.local/`.

## 1. Check existing resources without writes

```powershell
az login
$subscription = '<subscription-id>'
$resourceGroup = '<resource-group>'
$account = '<existing-cosmos-account>'
$database = '<existing-database>'

pwsh scripts/Initialize-Inference.ps1 `
  -SubscriptionId $subscription -ResourceGroup $resourceGroup `
  -CosmosAccountName $account -DatabaseName $database
```

Default behavior is read-only discovery and reuse. Absence of the companion
is an error, not permission to create it. An authorization/list failure is
not treated as absence.

## 2. Create a missing companion only when needed

The template references Cosmos as `existing`. Its optional child module
creates only `Microsoft.InferenceService/inferenceAccounts`. Availability,
supported region and preview API access must be confirmed for your
subscription. The sample uses API `2026-08-01-preview`; do not infer that all
subscriptions or regions support it.

```powershell
az bicep build --file infra/main.bicep --stdout

# Explicit Azure write, potentially billable. Omit when the companion already exists.
pwsh scripts/Initialize-Inference.ps1 `
  -SubscriptionId $subscription -ResourceGroup $resourceGroup `
  -CosmosAccountName $account -DatabaseName $database `
  -CreateMissingInference -InferenceLocation '<supported-region>' -ConfirmChanges
```

The wrapper refuses `-CreateMissingInference` when the companion exists.
It uses the Cosmos account name for the companion. It does not use a naming
guess to discover the inference endpoint: read the deployed resource:

```powershell
$inferenceId = "/subscriptions/$subscription/resourceGroups/$resourceGroup/providers/Microsoft.InferenceService/inferenceAccounts/$account"
az resource show --ids $inferenceId --api-version 2026-08-01-preview --query properties.endpoint -o tsv
```

When invoking Bicep directly, leave `createInference=false` for existing
resources and use incremental deployment mode, never complete mode.
The raw template does not implement the wrapper's absence check.

## 3. Provision dedicated .NET containers explicitly

```powershell
# Checks existing policies/offers; refuses to create a missing container without confirmation.
pwsh scripts/Initialize-DotnetContainers.ps1 `
  -SubscriptionId $subscription -ResourceGroup $resourceGroup `
  -AccountName $account -DatabaseName $database -ConfirmCreate
```

Only missing `reranker_dotnet_qflat` and `reranker_dotnet_diskann` are created.
Each has a dedicated manual offer of **400 RU/s**, for **800 RU/s total**
when both are created. Storage and other service charges are additional.
This requires a provisioned-throughput account compatible with dedicated
offers; a serverless/shared-throughput mismatch fails rather than changing
the account/database.

Both container policies:

| Setting | Value |
| --- | --- |
| Partition key | `/id`, Hash, version 2 |
| Vector path | `/embedding` |
| Vector dimensions/data type/distance | `1536`, `float32`, `cosine` |
| Vector index | `quantizedFlat` for qflat, `diskANN` for diskann |
| Full-text path/language | `/text`, `en-US` |
| Indexing | automatic, consistent; full-text index on `/text` |
| Scalar vector array indexing | `/embedding/*` excluded |

The script inspects all existing targets before any creation. Unexpected
existing policies or throughput cause failure. It never changes existing
throughput, recreates containers, or touches the Python containers. A
concurrent administrator changing resources is outside the script's
transaction boundary; do not run competing provisioning operations.

ARM is used intentionally. Entra data-plane permissions for an application
do not grant database/container DDL. Normal app/loader identities need no
management-plane provisioning privilege.

## 4. Grant scoped permissions separately

An operator assigning roles needs sufficient role-assignment permissions.
Use object IDs. Do not pass an application registration/client ID where
a principal object ID is required.

```powershell
$userObjectId = az ad signed-in-user show --query id -o tsv

# Contributor is for the human/automation identity running the bounded loader.
pwsh scripts/Initialize-Inference.ps1 `
  -SubscriptionId $subscription -ResourceGroup $resourceGroup `
  -CosmosAccountName $account -DatabaseName $database `
  -AssignRoles -PrincipalId $userObjectId -PrincipalType User `
  -CosmosAccess Contributor -ConfirmChanges
```

Assignments are:

- Cosmos DB Built-in Data Contributor (`...0002`) for loaders or Built-in
  Data Reader (`...0001`) for the web app, scoped separately to each of the
  two existing .NET containers.
- **Semantic Reranker User**, discovered by role name, scoped to the companion
  inference account. The script refuses to substitute a broad role if the
  expected role cannot be resolved.

For App Service, repeat with `-PrincipalType ServicePrincipal`,
`-PrincipalId <managed-identity-principalId>` and `-CosmosAccess Reader`.
No Cosmos keys are needed. Allow RBAC propagation before testing.
OpenAI, if used, needs its own appropriate data-plane permission such as
Cognitive Services OpenAI User on the existing OpenAI resource. This setup
does not provision OpenAI or grant its roles.

## 5. Load and run separately

Continue with the [loader and app instructions](../dotnet/README.md).
Container provisioning does not load documents. The loader cannot create
containers. The app cannot create resources or load data.

All setup body/parameter files containing your resource context stay under
ignored `.local/provisioning/`. Review what-if for manually customized
templates. No cleanup script is provided; these samples deliberately leave
bounded fixtures in place.
