[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("test", "production")]
    [string] $Environment,

    [string] $Location = "uksouth",

    [string] $SubscriptionId,

    [string] $ApplicationName = "invoicemanager",

    [switch] $PlanOnly,

    [switch] $AutoApprove,

    [switch] $ClearDatabase,

    # Skips straight to clearing invoice-records/freeagent-interventions (data-plane deletes,
    # invoice-configurations untouched) against an already-provisioned environment, then exits -
    # no GitHub auth, terraform plan/apply, FreeAgent credential check, or AdminWeb local config.
    # Only reconfigures the Terraform backend (so the right environment's Cosmos account is
    # resolved) before invoking the seeder. Mutually exclusive with -ClearDatabase and -PlanOnly.
    # For winding a Test environment's run history back to empty between manual test cycles - see
    # docs/deployment.md's "Resetting a Test environment's run history" section.
    [switch] $ClearRecordsOnly,

    # GitHub-less apply: pass -var=manage_github=false and skip every gh interaction (tool
    # check, auth/token, owner/repo/reviewer derivation, and stale-variable deletion). For
    # operators who can provision Azure but cannot administer GitHub. The Terraform-owned CI
    # identity, deploy environment, secrets, and variables are then NOT managed.
    [switch] $SkipGitHubManagement,

    # Build and push the real admin website image before applying, so the Container App is
    # created against a genuine image (a better apply test than the bootstrap reference).
    [switch] $PublishAdminWebImage,

    # Use an already-published admin website image reference (no build/push). Mutually
    # exclusive with -PublishAdminWebImage; use when the image was pushed out-of-band.
    [string] $AdminWebImage,

    # FreeAgent has no Terraform provider (its OAuth app is registered manually in FreeAgent's
    # developer dashboard), so its client ID/secret cannot be provisioned the way
    # MicrosoftAuthorization's are. By default this script prompts for each value only when it
    # is missing from the target environment's Key Vault, consistent with the rest of the
    # script's "ensure everything is in place" behaviour. These two flags force re-entry of one
    # value even when it is already present - separate switches because the client secret
    # rotates far more often than the client ID.
    [switch] $PromptFreeAgentClientId,

    [switch] $PromptFreeAgentClientSecret
)

if ($PublishAdminWebImage -and $AdminWebImage) {
    throw "-PublishAdminWebImage and -AdminWebImage are mutually exclusive."
}

if ($ClearDatabase -and $ClearRecordsOnly) {
    throw "-ClearDatabase and -ClearRecordsOnly are mutually exclusive."
}

if ($PlanOnly -and $ClearRecordsOnly) {
    throw "-PlanOnly and -ClearRecordsOnly are mutually exclusive."
}

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Section {
    param([string] $Message)

    Write-Host ""
    Write-Host "== $Message =="
}

function Test-Command {
    param([string] $Name)

    $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Show-TerraformInstallHelp {
    Write-Host "Terraform is required but was not found on PATH."
    Write-Host ""
    Write-Host "Install Terraform from:"
    Write-Host "  https://developer.hashicorp.com/terraform/install"
    Write-Host ""
    Write-Host "Common options:"
    Write-Host "  Windows: winget install Hashicorp.Terraform"
    Write-Host "  macOS:   brew tap hashicorp/tap && brew install hashicorp/tap/terraform"
    Write-Host "  Linux:   follow the HashiCorp package repository instructions for your distribution"
}

function Show-AzureCliInstallHelp {
    Write-Host "Azure CLI is required but was not found on PATH."
    Write-Host ""
    Write-Host "Install Azure CLI from:"
    Write-Host "  https://learn.microsoft.com/cli/azure/install-azure-cli"
    Write-Host ""
    Write-Host "Common options:"
    Write-Host "  Windows: winget install Microsoft.AzureCLI"
    Write-Host "  macOS:   brew install azure-cli"
    Write-Host "  Linux:   follow the Microsoft package repository instructions for your distribution"
}

function Show-GitHubCliInstallHelp {
    Write-Host "GitHub CLI (gh) is required but was not found on PATH."
    Write-Host ""
    Write-Host "Terraform now owns the GitHub deploy environment, its CI identity secrets, and the"
    Write-Host "deploy-target variables, so an authenticated gh is required to supply GITHUB_TOKEN."
    Write-Host ""
    Write-Host "Install GitHub CLI from:"
    Write-Host "  https://cli.github.com/"
    Write-Host ""
    Write-Host "Common options:"
    Write-Host "  Windows: winget install GitHub.cli"
    Write-Host "  macOS:   brew install gh"
    Write-Host "  Linux:   follow the GitHub CLI package repository instructions for your distribution"
    Write-Host ""
    Write-Host "Then authenticate with 'gh auth login' (needs repo + environment admin scope)."
}

function Invoke-JsonCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Command
    )

    $arguments = @($Command | Select-Object -Skip 1)
    $output = & $Command[0] @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $($Command -join ' ')"
    }

    if ([string]::IsNullOrWhiteSpace($output)) {
        return $null
    }

    return $output | ConvertFrom-Json
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Command
    )

    $arguments = @($Command | Select-Object -Skip 1)
    & $Command[0] @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $($Command -join ' ')"
    }
}

function Get-ShortHash {
    param([string] $Value)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        $hash = $sha.ComputeHash($bytes)
        return -join ($hash[0..2] | ForEach-Object { $_.ToString("x2") })
    }
    finally {
        $sha.Dispose()
    }
}

function Get-EnvironmentSuffix {
    param([string] $Name)

    if ($Name -eq "production") {
        return ""
    }

    return "-$Name"
}

function Get-StorageEnvironmentToken {
    param([string] $Name)

    if ($Name -eq "production") {
        return ""
    }

    return $Name.ToLowerInvariant()
}

function Ensure-AzureLogin {
    $account = $null

    try {
        $account = Invoke-JsonCommand -Command @("az", "account", "show", "--output", "json")
    }
    catch {
        Write-Host "Azure CLI is installed but not logged in. Starting 'az login'."
        Invoke-CheckedCommand -Command @("az", "login")
        $account = Invoke-JsonCommand -Command @("az", "account", "show", "--output", "json")
    }

    if ($SubscriptionId) {
        Invoke-CheckedCommand -Command @("az", "account", "set", "--subscription", $SubscriptionId)
        $account = Invoke-JsonCommand -Command @("az", "account", "show", "--output", "json")
    }

    return $account
}

function Ensure-ResourceGroup {
    param(
        [string] $Name,
        [string] $Location
    )

    $exists = $false
    try {
        $null = Invoke-JsonCommand -Command @("az", "group", "show", "--name", $Name, "--output", "json")
        $exists = $true
    }
    catch {
        $exists = $false
    }

    if ($exists) {
        Write-Host "Resource group exists: $Name"
        return
    }

    Write-Host "Creating resource group: $Name"
    Invoke-CheckedCommand -Command @("az", "group", "create", "--name", $Name, "--location", $Location, "--output", "none")
}

function Ensure-StorageAccount {
    param(
        [string] $Name,
        [string] $ResourceGroup,
        [string] $Location
    )

    $exists = $false
    try {
        $null = Invoke-JsonCommand -Command @("az", "storage", "account", "show", "--name", $Name, "--resource-group", $ResourceGroup, "--output", "json")
        $exists = $true
    }
    catch {
        $exists = $false
    }

    if ($exists) {
        Write-Host "Storage account exists: $Name"
        return
    }

    Write-Host "Creating storage account: $Name"
    Invoke-CheckedCommand -Command @(
        "az", "storage", "account", "create",
        "--name", $Name,
        "--resource-group", $ResourceGroup,
        "--location", $Location,
        "--sku", "Standard_LRS",
        "--kind", "StorageV2",
        "--min-tls-version", "TLS1_2",
        "--allow-blob-public-access", "false",
        "--https-only", "true",
        "--output", "none"
    )
}

function Get-StorageAccountKey {
    param(
        [string] $Name,
        [string] $ResourceGroup
    )

    $keys = Invoke-JsonCommand -Command @(
        "az", "storage", "account", "keys", "list",
        "--account-name", $Name,
        "--resource-group", $ResourceGroup,
        "--output", "json"
    )

    return $keys[0].value
}

function Ensure-StorageContainer {
    param(
        [string] $Name,
        [string] $StorageAccount,
        [string] $StorageKey
    )

    Write-Host "Ensuring storage container exists: $Name"
    Invoke-CheckedCommand -Command @(
        "az", "storage", "container", "create",
        "--name", $Name,
        "--account-name", $StorageAccount,
        "--account-key", $StorageKey,
        "--public-access", "off",
        "--output", "none"
    )
}

function Set-ProjectUserSecret {
    param(
        [string] $ProjectPath,
        [string] $Key,
        [string] $Value
    )

    Invoke-CheckedCommand -Command @(
        "dotnet", "user-secrets", "set",
        $Key,
        $Value,
        "--project",
        $ProjectPath
    )
}

function Test-KeyVaultSecretExists {
    param(
        [string] $VaultName,
        [string] $SecretName
    )

    $null = az keyvault secret show --vault-name $VaultName --name $SecretName --output json 2>$null
    $exists = $LASTEXITCODE -eq 0
    $global:LASTEXITCODE = 0
    return $exists
}

function Set-KeyVaultSecretFromPrompt {
    param(
        [string] $VaultName,
        [string] $SecretName,
        [string] $PromptText,
        [switch] $AsSecureString
    )

    if ($AsSecureString) {
        $secureValue = Read-Host -Prompt $PromptText -AsSecureString
        # SecureStringToBSTR always produces a UTF-16, length-prefixed BSTR - PtrToStringBSTR is
        # the correct decode (respects the length prefix rather than scanning for a null
        # terminator, so it can't mis-decode or truncate). ZeroFreeBSTR then zeroes and frees the
        # unmanaged buffer so the secret doesn't linger in memory longer than necessary.
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
        try {
            $plainValue = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        }
        finally {
            [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
    else {
        $plainValue = Read-Host -Prompt $PromptText
    }

    if ([string]::IsNullOrWhiteSpace($plainValue)) {
        throw "$SecretName was not provided. Re-run and enter a value, or set it directly with 'az keyvault secret set'."
    }

    # --value would place the secret literally in az's process command line, visible to any
    # process-listing/monitoring tool for as long as the child process runs. --file instead
    # passes only a path; the temp file is written with no trailing newline/BOM and removed
    # immediately after.
    $tempFile = [System.IO.Path]::GetTempFileName()
    try {
        # Not Set-Content -Encoding utf8: under Windows PowerShell 5.1 (still the default
        # `powershell.exe` on Windows) that writes a UTF-8 BOM, which az would then store as part
        # of the secret value. UTF8Encoding($false) is BOM-less on every PowerShell version.
        $utf8NoBom = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($tempFile, $plainValue, $utf8NoBom)

        # Not Invoke-CheckedCommand: its failure path echoes the full command line, which would
        # put $plainValue - the secret itself - into the thrown exception and any terminal/CI log
        # that captures it. Build the failure message without the command array instead.
        az keyvault secret set --vault-name $VaultName --name $SecretName --file $tempFile --output none
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set Key Vault secret '$SecretName' in vault '$VaultName' (az exited with code $LASTEXITCODE)."
        }
    }
    finally {
        Remove-Item -Path $tempFile -Force -ErrorAction SilentlyContinue
        $plainValue = $null
    }
}

function Set-FreeAgentClientCredentials {
    param(
        [string] $TerraformRoot,
        [string] $Environment,
        [switch] $ForcePromptClientId,
        [switch] $ForcePromptClientSecret
    )

    Write-Section "Ensuring FreeAgent client credentials"

    Push-Location $TerraformRoot
    try {
        $outputs = Invoke-JsonCommand -Command @("terraform", "output", "-json")
    }
    finally {
        Pop-Location
    }

    $keyVaultName = $outputs.key_vault_name.value
    $appName = if ($Environment -eq "production") { "Omnics InvoiceManager" } else { "Omnics InvoiceManager Sandbox" }

    $clientIdSecretName = "FreeAgentAuthorization--ClientId"
    $clientSecretSecretName = "FreeAgentAuthorization--ClientSecret"

    $needsClientId = $ForcePromptClientId -or -not (Test-KeyVaultSecretExists -VaultName $keyVaultName -SecretName $clientIdSecretName)
    $needsClientSecret = $ForcePromptClientSecret -or -not (Test-KeyVaultSecretExists -VaultName $keyVaultName -SecretName $clientSecretSecretName)

    if (-not $needsClientId -and -not $needsClientSecret) {
        Write-Host "FreeAgent client ID and client secret already present in Key Vault '$keyVaultName'."
        return
    }

    Write-Host "Register the '$appName' app at https://dev.freeagent.com/ (OAuth identifier and secret) before continuing, if you have not already."

    if ($needsClientId) {
        Set-KeyVaultSecretFromPrompt -VaultName $keyVaultName -SecretName $clientIdSecretName `
            -PromptText "FreeAgent OAuth identifier for '$appName'"
    }

    if ($needsClientSecret) {
        Set-KeyVaultSecretFromPrompt -VaultName $keyVaultName -SecretName $clientSecretSecretName `
            -PromptText "FreeAgent OAuth secret for '$appName'" -AsSecureString
    }

    Write-Host "FreeAgent client credentials stored in Key Vault '$keyVaultName'."
}

function Set-TestAdminWebLocalConfiguration {
    param(
        [string] $TerraformRoot,
        [string] $RepoRoot
    )

    if (-not (Test-Command "dotnet")) {
        throw ".NET SDK is required to configure local admin website user secrets."
    }

    Write-Section "Configuring local user secrets"

    Push-Location $TerraformRoot
    try {
        $outputs = Invoke-JsonCommand -Command @("terraform", "output", "-json")
    }
    finally {
        Pop-Location
    }

    # These non-secret values are read both by the admin website (when run directly) and by
    # the AppHost, which reads its OWN user-secrets store and forwards them to the Functions
    # and admin website resources. Set them for both projects so a clean setup needs no manual
    # step. The client secret is never stored here; it stays in Key Vault.
    $adminWebProject = Join-Path $RepoRoot "src/InvoiceManager.AdminWeb/InvoiceManager.AdminWeb.csproj"
    $appHostProject = Join-Path $RepoRoot "src/InvoiceManager.AppHost/InvoiceManager.AppHost.csproj"

    foreach ($project in @($adminWebProject, $appHostProject)) {
        Set-ProjectUserSecret -ProjectPath $project -Key "MicrosoftAuthorization:TenantId" -Value $outputs.tenant_id.value
        Set-ProjectUserSecret -ProjectPath $project -Key "MicrosoftAuthorization:ClientId" -Value $outputs.application_client_id.value
        Set-ProjectUserSecret -ProjectPath $project -Key "KeyVault:Uri" -Value $outputs.key_vault_uri.value
        Set-ProjectUserSecret -ProjectPath $project -Key "AdminAuthorization:GroupObjectId" -Value $outputs.adminweb_admin_group_object_id.value
        # Always Sandbox here: this whole block only runs for the "test" environment.
        Set-ProjectUserSecret -ProjectPath $project -Key "FreeAgent:Environment" -Value "Sandbox"
    }

    # Only the AppHost forwards this to the Functions project (see AppHost/Program.cs); the
    # admin website never uses Document Intelligence, so it doesn't need the secret.
    Set-ProjectUserSecret -ProjectPath $appHostProject -Key "DocumentIntelligence:Endpoint" -Value $outputs.document_intelligence_endpoint.value

    Write-Host "Local user secrets configured for the test environment (admin website + AppHost)."
    Write-Host "Client secret remains in Key Vault as MicrosoftAuthorization--ClientSecret."
}

function Invoke-ConfigurationSeeder {
    param(
        [string] $TerraformRoot,
        [string] $RepoRoot,
        [string] $Environment,
        [switch] $ClearDatabase,
        [switch] $ClearRecordsOnly
    )

    Write-Section $(if ($ClearRecordsOnly) { "Clearing invoice records" } else { "Seeding invoice configurations" })

    Push-Location $TerraformRoot
    try {
        $outputs = Invoke-JsonCommand -Command @("terraform", "output", "-json")
    }
    finally {
        Pop-Location
    }

    # The backend (resource group/storage account/state key) is selected from -Environment
    # before terraform init ever runs, so it should already hold this environment's state -
    # but for the destructive modes, don't just trust that: if the selected state was ever
    # applied against the wrong tfvars (e.g. production vars written under the test state key),
    # its own "environment" output is the authoritative record of what it actually contains.
    # Cross-check it before deleting anything, rather than deleting whatever Cosmos account the
    # state happens to point at under the assumption it must be the requested one.
    $stateEnvironment = $outputs.environment.value
    if (($ClearDatabase -or $ClearRecordsOnly) -and $stateEnvironment -ne $Environment) {
        throw "Terraform state reports environment '$stateEnvironment' but -Environment '$Environment' was requested. Refusing to run a destructive clear against a possibly mismatched backend."
    }

    $cosmosEndpoint = $outputs.cosmos_endpoint.value
    $cosmosDatabase = $outputs.cosmos_database_name.value
    $seedFile = Join-Path $RepoRoot "data/seed/invoice-configurations.json"
    $seederProject = Join-Path $RepoRoot "tools/InvoiceManager.Seeder/InvoiceManager.Seeder.csproj"

    # Remove the Terraform state storage key from the seeder's environment so it
    # is not visible to the seeder process or any diagnostic output it produces.
    $savedArmKey = $env:ARM_ACCESS_KEY
    Remove-Item Env:\ARM_ACCESS_KEY -ErrorAction SilentlyContinue

    # Use the same configuration keys the seeder (via CosmosClientFactory) reads.
    # CosmosEndpoint + DefaultAzureCredential authenticates against the real account;
    # the schema already exists (Terraform owns it) so --ensure-schema is deliberately
    # not passed here.
    $env:CosmosEndpoint = $cosmosEndpoint
    $env:CosmosDatabase = $cosmosDatabase

    # Forward the environment so the seeder nests test downloads under a "Test" folder, and
    # optionally clear the containers first for a clean re-seed - or, for -ClearRecordsOnly,
    # clear invoice-records/freeagent-interventions and exit without touching the seed file or
    # invoice-configurations at all.
    $seederArgs = @("--environment", $Environment)
    if ($ClearRecordsOnly) {
        $seederArgs += "--clear-records-only"
        Write-Host "Clearing invoice-records and freeagent-interventions only (--clear-records-only); invoice-configurations untouched."
    }
    else {
        $seederArgs = @($seedFile) + $seederArgs
        if ($ClearDatabase) {
            $seederArgs += "--clear-database"
            Write-Host "Clearing database contents before seeding (--clear-database)."
        }
    }

    try {
        # Build once so every retry runs the pre-compiled binary rather than
        # triggering a fresh compilation on each attempt.
        Invoke-CheckedCommand -Command @("dotnet", "build", $seederProject)

        # Cosmos DB data-plane RBAC can take up to ~60 s to propagate after
        # terraform apply creates the role assignment. The seeder exits 2 on a
        # transient 403; all other non-zero exits are permanent failures.
        $maxAttempts = 5
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            & dotnet run --project $seederProject --no-build -- @seederArgs
            $exitCode = $LASTEXITCODE

            if ($exitCode -eq 0) { return }

            # Exit code 2 = Cosmos 403 (RBAC propagation delay) — retry.
            # Any other non-zero exit is a permanent failure; surface immediately.
            if ($exitCode -ne 2 -or $attempt -ge $maxAttempts) {
                throw "Seeder failed with exit code $exitCode (attempt $attempt/$maxAttempts)."
            }

            Write-Host "Seeder attempt $attempt/${maxAttempts}: Cosmos 403 (RBAC propagation delay). Retrying in 30 s..."
            Start-Sleep -Seconds 30
        }
    }
    finally {
        Remove-Item Env:\CosmosEndpoint -ErrorAction SilentlyContinue
        Remove-Item Env:\CosmosDatabase -ErrorAction SilentlyContinue
        if ($null -ne $savedArmKey) { $env:ARM_ACCESS_KEY = $savedArmKey }
    }
}

function Remove-InjectedFunctionStorageConnectionString {
    param(
        [string] $TerraformRoot
    )

    Write-Section "Removing provider-injected Function App storage connection strings"

    # The azurerm provider's azurerm_function_app_flex_consumption resource silently
    # re-injects empty-key AzureWebJobsStorage and DEPLOYMENT_STORAGE_CONNECTION_STRING
    # app settings on every create/update, even though neither is in our Terraform
    # app_settings and neither appears in the plan (hashicorp/terraform-provider-azurerm
    # #29149, #29993 — both open as of azurerm 4.80.0). The blank-key AzureWebJobsStorage
    # scalar shadows the identity-based AzureWebJobsStorage__* settings, so the host falls
    # back to shared-key auth with an empty key and fails with 403 AuthenticationFailed on
    # the azure-webjobs-secrets container, which stops the timer listener from starting.
    # Deleting them restarts the host onto the managed-identity settings. Idempotent: runs
    # every deploy and converges regardless of the provider bug. Remove once the upstream
    # issues are fixed (see docs/deployment.md).
    Push-Location $TerraformRoot
    try {
        $outputs = Invoke-JsonCommand -Command @("terraform", "output", "-json")
    }
    finally {
        Pop-Location
    }

    $functionAppName = $outputs.functions_app_name.value
    $resourceGroup = $outputs.resource_group_name.value
    $injectedSettings = @("AzureWebJobsStorage", "DEPLOYMENT_STORAGE_CONNECTION_STRING")

    $present = Invoke-JsonCommand -Command @(
        "az", "functionapp", "config", "appsettings", "list",
        "--name", $functionAppName,
        "--resource-group", $resourceGroup,
        "--query", "[?name=='AzureWebJobsStorage' || name=='DEPLOYMENT_STORAGE_CONNECTION_STRING'].name",
        "-o", "json")

    if (-not $present -or @($present).Count -eq 0) {
        Write-Host "No injected storage connection strings present on '$functionAppName'; nothing to remove."
        return
    }

    Write-Host "Removing injected setting(s) on '$functionAppName': $(@($present) -join ', ')"
    $deleteCommand = @(
        "az", "functionapp", "config", "appsettings", "delete",
        "--name", $functionAppName,
        "--resource-group", $resourceGroup,
        "--setting-names"
    ) + $injectedSettings + @("-o", "none")
    Invoke-CheckedCommand -Command $deleteCommand

    Write-Host "Removed injected storage connection string(s); the host will restart onto identity-based storage."
}

# The five deploy-target Environment variables Terraform now owns
# (github_actions_environment_variable in github.tf).
$script:DeployTargetVariableNames = @(
    "FUNCTIONS_APP_NAME",
    "FUNCTIONS_DEFAULT_HOSTNAME",
    "ADMINWEB_CONTAINER_APP_NAME",
    "ADMINWEB_FQDN",
    "AZURE_RESOURCE_GROUP"
)

function Remove-StaleDeployTargetVariables {
    param([string] $Environment)

    # One-time migration: the retired Publish-GitHubEnvironmentVariables step POST-created
    # these five variables out-of-band. On the first apply after consolidation they exist in
    # GitHub but not in Terraform state, so the github provider's POST-create would 409.
    # Delete them first so the create succeeds. This needs no Terraform state and therefore no
    # `terraform import`. The caller only invokes this when the variables are NOT yet managed
    # by Terraform, so a TF-managed variable is never deleted (which would cause drift).
    Write-Section "Removing stale deploy-target variables (one-time migration)"

    foreach ($name in $script:DeployTargetVariableNames) {
        # gh exits non-zero when the variable/environment does not exist; that is expected on
        # a fresh environment, so tolerate it and only report actual deletions.
        gh variable delete $name --env $Environment 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Deleted stale variable $name from environment '$Environment'."
        }
    }

    # Tolerated gh failures above leave $LASTEXITCODE non-zero; reset so the caller does not
    # mistake an expected "not found" for a deploy failure.
    $global:LASTEXITCODE = 0
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$terraformRoot = Join-Path $repoRoot "infra/terraform"

# Saved so the script's own GITHUB_TOKEN and ARM_ACCESS_KEY (the latter set below to the
# Terraform backend storage key) can be removed on exit without clobbering values the caller's
# shell already had set. Invoke-ConfigurationSeeder's own save/restore of ARM_ACCESS_KEY only
# covers the backend key this script just assigned, not whatever the caller had before that.
$savedGithubToken = $env:GITHUB_TOKEN
$savedCallerArmKey = $env:ARM_ACCESS_KEY

# Everything below is wrapped in this try/finally (not just the Terraform-running block further
# down) so the caller's GITHUB_TOKEN/ARM_ACCESS_KEY are restored even when this script exits
# early - e.g. a failed gh/az check, or -ClearRecordsOnly's own backend-existence guards, all of
# which can run (and throw, or `exit`) before Terraform is ever touched. PowerShell still runs
# `finally` for an `exit` reached inside `try`.
try {

Write-Section "Checking tools"

if (-not (Test-Command "terraform")) {
    Show-TerraformInstallHelp
    exit 1
}

if (-not (Test-Command "az")) {
    Show-AzureCliInstallHelp
    exit 1
}

if (-not $SkipGitHubManagement -and -not $ClearRecordsOnly -and -not (Test-Command "gh")) {
    Show-GitHubCliInstallHelp
    exit 1
}

Write-Host "Terraform: $(terraform version -json | ConvertFrom-Json | Select-Object -ExpandProperty terraform_version)"
Write-Host "Azure CLI: $(az version --query '\"azure-cli\"' --output tsv)"

Write-Section "Checking Azure login"
$account = Ensure-AzureLogin
$activeSubscriptionId = $account.id
$tenantId = $account.tenantId

Write-Host "Subscription: $($account.name) ($activeSubscriptionId)"
Write-Host "Tenant: $tenantId"

$functionInvokerUserObjectId = ""

if ($ClearRecordsOnly) {
    Write-Section "Skipping operator identity derivation (-ClearRecordsOnly)"
    Write-Host "No terraform plan/apply will run, so no operator Invoke assignment is needed here."
}
else {
    Write-Section "Deriving operator identity"

    # The signed-in user is granted the Functions "Invoke" app role so they can call the
    # endpoint directly. Derived from the authenticated context, not hardcoded (see
    # [[feedback-no-hardcoded-account-identity]]). A service-principal login (e.g. CI) has no
    # signed-in user, so this stays empty and Terraform manages no operator assignment.
    $functionInvokerUserObjectId = az ad signed-in-user show --query id --output tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($functionInvokerUserObjectId)) {
        $functionInvokerUserObjectId = ""
        $global:LASTEXITCODE = 0
        Write-Host "No signed-in user (service-principal login?); no operator Invoke assignment will be managed."
    }
    else {
        $functionInvokerUserObjectId = $functionInvokerUserObjectId.Trim()
        Write-Host "Operator object id: $functionInvokerUserObjectId"
    }
}

if ($ClearRecordsOnly) {
    Write-Section "Skipping GitHub management (-ClearRecordsOnly)"
    Write-Host "No terraform plan/apply will run, so no gh interaction occurs."
}
elseif ($SkipGitHubManagement) {
    Write-Section "Skipping GitHub management (-SkipGitHubManagement)"
    Write-Host "Terraform will run with manage_github=false: the CI identity, deploy environment,"
    Write-Host "secrets, and variables are NOT managed, and no gh interaction occurs."
}
else {
    Write-Section "Checking GitHub authentication"

    # The github Terraform provider owns the deploy environment, its CI identity secrets, and the
    # deploy-target variables, so it needs an authenticated gh to source GITHUB_TOKEN from.
    # Operators without GitHub admin access can opt out entirely with -SkipGitHubManagement.
    try {
        Invoke-CheckedCommand -Command @("gh", "auth", "status")
    }
    catch {
        throw "GitHub CLI is not authenticated. Run 'gh auth login' (needs repo + environment admin scope), or re-run with -SkipGitHubManagement for a GitHub-less apply."
    }

    $env:GITHUB_TOKEN = (gh auth token)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        throw "Could not obtain a GitHub token from 'gh auth token'."
    }

    # Derive the GitHub identity vars from the authenticated context so no account name is
    # hardcoded in Terraform (see [[feedback-no-hardcoded-account-identity]]).
    Push-Location $repoRoot
    try {
        $repoInfo = Invoke-JsonCommand -Command @("gh", "api", "repos/{owner}/{repo}")
        $currentUser = Invoke-JsonCommand -Command @("gh", "api", "user")
    }
    finally {
        Pop-Location
    }

    $githubOwner = $repoInfo.owner.login
    $githubOwnerId = $repoInfo.owner.id
    $githubRepository = $repoInfo.name
    $githubRepositoryId = $repoInfo.id
    $productionReviewer = $currentUser.login

    Write-Host "GitHub repository: $githubOwner/$githubRepository"
    Write-Host "Production reviewer (this user): $productionReviewer"
}

$suffix = Get-EnvironmentSuffix -Name $Environment
$storageEnvironmentToken = Get-StorageEnvironmentToken -Name $Environment
$subscriptionHash = Get-ShortHash -Value $activeSubscriptionId

$stateResourceGroup = "rg-$ApplicationName-tfstate$suffix"
$stateStorageAccount = ("imtfstate$storageEnvironmentToken$subscriptionHash").ToLowerInvariant()
$stateContainer = "tfstate"
$stateKey = "$ApplicationName$suffix.tfstate"

Write-Section "Ensuring Terraform backend"
Write-Host "Resource group: $stateResourceGroup"
Write-Host "Storage account: $stateStorageAccount"
Write-Host "Container: $stateContainer"
Write-Host "State key: $stateKey"

if ($ClearRecordsOnly) {
    # -ClearRecordsOnly is documented as operating on an already-provisioned environment (see
    # docs/deployment.md), unlike every other mode here, which idempotently creates the backend
    # if missing. Never silently stand up a fresh, empty backend for a mistyped -Environment,
    # -ApplicationName, or wrong subscription - fail fast instead.
    $backendExists = $false
    try {
        $null = Invoke-JsonCommand -Command @("az", "storage", "account", "show", "--name", $stateStorageAccount, "--resource-group", $stateResourceGroup, "--output", "json")
        $backendExists = $true
    }
    catch {
        $backendExists = $false
    }

    if (-not $backendExists) {
        throw "Terraform backend storage account '$stateStorageAccount' (resource group '$stateResourceGroup') does not exist. -ClearRecordsOnly only operates on an already-provisioned environment; run Deploy-Infra.ps1 -Environment $Environment (without -ClearRecordsOnly) first to provision it."
    }

    Write-Host "Resource group exists: $stateResourceGroup"
    Write-Host "Storage account exists: $stateStorageAccount"
}
else {
    Ensure-ResourceGroup -Name $stateResourceGroup -Location $Location
    Ensure-StorageAccount -Name $stateStorageAccount -ResourceGroup $stateResourceGroup -Location $Location
}
$stateStorageKey = Get-StorageAccountKey -Name $stateStorageAccount -ResourceGroup $stateResourceGroup

if ($ClearRecordsOnly) {
    # As above: the storage account existing doesn't guarantee its tfstate container does (it
    # could have been deleted independently) - check rather than let Ensure-StorageContainer
    # silently create it. Caught and re-thrown with a redacted message on failure: Invoke-JsonCommand
    # includes the full failed command in its exception, and this one carries the storage account
    # key as a plain --account-key argument.
    $containerExists = $false
    try {
        $containerExists = (Invoke-JsonCommand -Command @("az", "storage", "container", "exists", "--name", $stateContainer, "--account-name", $stateStorageAccount, "--account-key", $stateStorageKey, "--output", "json")).exists
    }
    catch {
        throw "Failed to check whether the Terraform backend container '$stateContainer' exists in storage account '$stateStorageAccount'."
    }

    if (-not $containerExists) {
        throw "Terraform backend container '$stateContainer' does not exist in storage account '$stateStorageAccount'. -ClearRecordsOnly only operates on an already-provisioned environment; run Deploy-Infra.ps1 -Environment $Environment (without -ClearRecordsOnly) first to provision it."
    }
    Write-Host "Storage container exists: $stateContainer"
}
else {
    Ensure-StorageContainer -Name $stateContainer -StorageAccount $stateStorageAccount -StorageKey $stateStorageKey
}

Write-Section "Running Terraform"

$env:ARM_ACCESS_KEY = $stateStorageKey

Push-Location $terraformRoot
try {
    Invoke-CheckedCommand -Command @(
        "terraform", "init",
        "-reconfigure",
        "-backend-config=resource_group_name=$stateResourceGroup",
        "-backend-config=storage_account_name=$stateStorageAccount",
        "-backend-config=container_name=$stateContainer",
        "-backend-config=key=$stateKey"
    )

    if ($ClearRecordsOnly) {
        Invoke-ConfigurationSeeder -TerraformRoot $terraformRoot -RepoRoot $repoRoot -Environment $Environment -ClearRecordsOnly
        return
    }

    $tfVarsFile = "$Environment.tfvars"

    $planArgs = @(
        "-detailed-exitcode",
        "-var-file=$tfVarsFile",
        "-var=location=$Location",
        "-var=application_name=$ApplicationName",
        "-var=function_invoker_user_object_id=$functionInvokerUserObjectId"
    )

    if ($SkipGitHubManagement) {
        $planArgs += "-var=manage_github=false"
    }
    else {
        $planArgs += "-var=github_owner=$githubOwner"
        $planArgs += "-var=github_owner_id=$githubOwnerId"
        $planArgs += "-var=github_repository=$githubRepository"
        $planArgs += "-var=github_repository_id=$githubRepositoryId"
        $planArgs += "-var=production_reviewer=$productionReviewer"
    }

    # Electively build + push the real admin website image and pin the plan to it, so the
    # Container App is created against a genuine image rather than the bootstrap reference.
    # -AdminWebImage takes an already-published reference and skips the build/push.
    if ($PublishAdminWebImage) {
        Write-Section "Publishing admin website image"
        $publishScript = Join-Path $PSScriptRoot "Publish-AdminWebImage.ps1"
        $imageTag = "$Environment-$(Get-Date -Format yyyyMMddHHmmss)"
        $adminWebImage = & $publishScript -Tag $imageTag -Push | Select-Object -Last 1
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($adminWebImage)) {
            throw "Failed to publish the admin website image."
        }
        Write-Host "Admin website image: $adminWebImage"
        $planArgs += "-var=adminweb_image=$adminWebImage"
    }
    elseif ($AdminWebImage) {
        Write-Host "Using pre-published admin website image: $AdminWebImage"
        $planArgs += "-var=adminweb_image=$AdminWebImage"
    }

    $planArgs += "-out=$Environment.tfplan"

    & terraform plan @planArgs
    $planExitCode = $LASTEXITCODE

    if ($planExitCode -eq 0) {
        Write-Host "Terraform plan completed with no changes."
        if (-not $PlanOnly) {
            Remove-InjectedFunctionStorageConnectionString -TerraformRoot $terraformRoot
            Set-FreeAgentClientCredentials -TerraformRoot $terraformRoot -Environment $Environment `
                -ForcePromptClientId:$PromptFreeAgentClientId -ForcePromptClientSecret:$PromptFreeAgentClientSecret
            Invoke-ConfigurationSeeder -TerraformRoot $terraformRoot -RepoRoot $repoRoot -Environment $Environment -ClearDatabase:$ClearDatabase
            if ($Environment -eq "test") {
                Set-TestAdminWebLocalConfiguration -TerraformRoot $terraformRoot -RepoRoot $repoRoot
            }
        }
        return
    }

    if ($planExitCode -ne 2) {
        throw "Command failed: terraform plan $($planArgs -join ' ')"
    }

    if ($PlanOnly) {
        Write-Host "Plan created at infra/terraform/$Environment.tfplan"
        return
    }

    if (-not $AutoApprove) {
        $confirmation = Read-Host "Apply this Terraform plan? Type 'yes' to continue"
        if ($confirmation -ne "yes") {
            Write-Host "Terraform apply cancelled."
            return
        }
    }

    # Delete the out-of-band deploy-target variables just before apply, but only when Terraform
    # is not already managing them — otherwise the github provider's first POST-create would
    # 409 against the ones the retired publishing step left behind. Once Terraform owns them
    # (present in state) they must NOT be deleted, or the applied plan would leave GitHub in a
    # drifted state. `terraform state list` is the ownership check. Skipped entirely for a
    # GitHub-less apply, which touches no GitHub state.
    if (-not $SkipGitHubManagement) {
        $stateResources = & terraform state list
        $variablesAlreadyManaged = @($stateResources | Where-Object { $_ -match 'github_actions_environment_variable' }).Count -gt 0
        if (-not $variablesAlreadyManaged) {
            Remove-StaleDeployTargetVariables -Environment $Environment
        }
    }

    $applyCommand = @("terraform", "apply")
    $applyCommand += "$Environment.tfplan"

    Invoke-CheckedCommand -Command $applyCommand

    Remove-InjectedFunctionStorageConnectionString -TerraformRoot $terraformRoot

    Set-FreeAgentClientCredentials -TerraformRoot $terraformRoot -Environment $Environment `
        -ForcePromptClientId:$PromptFreeAgentClientId -ForcePromptClientSecret:$PromptFreeAgentClientSecret

    Invoke-ConfigurationSeeder -TerraformRoot $terraformRoot -RepoRoot $repoRoot -Environment $Environment -ClearDatabase:$ClearDatabase

    if ($Environment -eq "test") {
        Set-TestAdminWebLocalConfiguration -TerraformRoot $terraformRoot -RepoRoot $repoRoot
    }
}
finally {
    Pop-Location
}

}
finally {
    # Restores whatever GITHUB_TOKEN/ARM_ACCESS_KEY the caller's shell had before this script
    # ran (or removes them if it had none), regardless of where above this exited from - see the
    # comment where $savedGithubToken/$savedCallerArmKey are captured.
    if ($null -ne $savedCallerArmKey) {
        $env:ARM_ACCESS_KEY = $savedCallerArmKey
    }
    else {
        Remove-Item Env:\ARM_ACCESS_KEY -ErrorAction SilentlyContinue
    }
    if ($null -ne $savedGithubToken) {
        $env:GITHUB_TOKEN = $savedGithubToken
    }
    else {
        Remove-Item Env:\GITHUB_TOKEN -ErrorAction SilentlyContinue
    }
}
