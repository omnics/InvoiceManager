[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Write-Host 'Authentication pop-ups might appear while Azure CLI or Microsoft Graph refreshes authentication.'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI (az) is required. Install it, then run az login before running this script.'
}

$accountJson = & az account show --only-show-errors --output json
if ($LASTEXITCODE -ne 0) {
    throw 'Azure CLI is not signed in. Run az login for the tenant that owns the OneDrive and billing accounts.'
}

$account = $accountJson | ConvertFrom-Json
$tenantId = [string]$account.tenantId
$accountName = [string]$account.user.name
$accountType = [string]$account.user.type

if ([string]::IsNullOrWhiteSpace($tenantId) -or [string]::IsNullOrWhiteSpace($accountName)) {
    throw 'Azure CLI did not return a tenant ID and signed-in account name.'
}

if (-not [string]::Equals($accountType, 'user', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Azure CLI must be signed in with a user account because Microsoft Graph /me/drive requires delegated authentication.'
}

if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {
    if (-not (Get-Command Install-Module -ErrorAction SilentlyContinue)) {
        throw 'Install-Module is required to install Microsoft.Graph.Authentication.'
    }

    Write-Host 'Installing Microsoft.Graph.Authentication for the current user.'
    Install-Module `
        -Name Microsoft.Graph.Authentication `
        -Scope CurrentUser `
        -Repository PSGallery `
        -Force `
        -AllowClobber
}

Import-Module Microsoft.Graph.Authentication

function Test-GraphContext {
    param(
        [AllowNull()]
        [object] $Context,

        [Parameter(Mandatory)]
        [string] $ExpectedTenantId,

        [Parameter(Mandatory)]
        [string] $ExpectedAccount
    )

    if ($null -eq $Context) {
        return $false
    }

    if (-not [string]::Equals(
            [string]$Context.TenantId,
            $ExpectedTenantId,
            [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    if (-not [string]::Equals(
            [string]$Context.Account,
            $ExpectedAccount,
            [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $sufficientScopes = @(
        'Files.Read',
        'Files.Read.All',
        'Files.ReadWrite',
        'Files.ReadWrite.All',
        'Sites.Read.All',
        'Sites.ReadWrite.All'
    )

    return @($Context.Scopes | Where-Object { $_ -in $sufficientScopes }).Count -gt 0
}

$graphContext = Get-MgContext
if (-not (Test-GraphContext `
        -Context $graphContext `
        -ExpectedTenantId $tenantId `
        -ExpectedAccount $accountName)) {
    Write-Host "Connecting to Microsoft Graph as $accountName with Files.Read permission."
    Connect-MgGraph `
        -TenantId $tenantId `
        -Scopes 'Files.Read' `
        -ContextScope Process `
        -NoWelcome

    $graphContext = Get-MgContext
}

if (-not (Test-GraphContext `
        -Context $graphContext `
        -ExpectedTenantId $tenantId `
        -ExpectedAccount $accountName)) {
    throw "Microsoft Graph must be connected as $accountName in tenant $tenantId with Files.Read permission."
}

$drive = Invoke-MgGraphRequest `
    -Method GET `
    -Uri 'https://graph.microsoft.com/v1.0/me/drive?$select=id,name'
$driveId = [string]$drive.id
$driveName = [string]$drive.name
if ([string]::IsNullOrWhiteSpace($driveName)) {
    $driveName = 'OneDrive'
}

if ([string]::IsNullOrWhiteSpace($driveId)) {
    throw 'Microsoft Graph did not return an ID for the signed-in user''s default OneDrive.'
}

function Get-OneDriveFolderItemId {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    # Escape each path segment individually so structural "/" separators survive —
    # escaping the whole path (e.g. via [Uri]::EscapeDataString($Path)) turns them into
    # "%2F", which Graph's root:/{path} addressing does not treat as a separator.
    $encodedPath = ($Path -split '/' | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    try {
        $item = Invoke-MgGraphRequest `
            -Method GET `
            -Uri "https://graph.microsoft.com/v1.0/me/drive/root:/$($encodedPath)`?`$select=id"
    }
    catch {
        throw "Could not find a OneDrive folder at '/$Path'. Create it (or ask whoever owns the real Bills folders to) before seeding. ($($_.Exception.Message))"
    }

    if ([string]::IsNullOrWhiteSpace([string]$item.id)) {
        throw "Microsoft Graph did not return an item ID for '/$Path'."
    }

    return [string]$item.id
}

# Production configurations address the real "Bills" folders directly; test configurations
# address a separate, isolated "Test/Bills" tree in the same drive so a test seed/run never
# reads or writes the real production files (see tools/InvoiceManager.Seeder/Program.cs).
$microsoft365FolderItemId = Get-OneDriveFolderItemId -Path 'Bills/Microsoft 365'
$azureFolderItemId = Get-OneDriveFolderItemId -Path 'Bills/Azure + Visual Studio'
$microsoft365TestFolderItemId = Get-OneDriveFolderItemId -Path 'Test/Bills/Microsoft 365'
$azureTestFolderItemId = Get-OneDriveFolderItemId -Path 'Test/Bills/Azure + Visual Studio'

$billingAccountsUrl =
    'https://management.azure.com/providers/Microsoft.Billing/billingAccounts?api-version=2024-04-01'

function Get-SingleBillingAccountName {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Individual', 'Business')]
        [string] $AccountType
    )

    # Only Active accounts are candidates: a disabled account (e.g. one replaced by a new
    # agreement) stays listed but can never receive new invoices.
    $query = "value[?properties.accountType == '$AccountType' && properties.accountStatus == 'Active'].name"
    $output = @(
        & az rest `
            --method get `
            --url $billingAccountsUrl `
            --query $query `
            --output tsv `
            --only-show-errors
    )
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        throw "Failed to list Azure Billing accounts while selecting AccountType '$AccountType'."
    }

    $names = @($output | ForEach-Object { $_.Trim() } | Where-Object { $_ })

    if ($names.Count -ne 1) {
        throw "Expected exactly one Active Azure Billing account with AccountType '$AccountType', but found $($names.Count)."
    }

    return $names[0]
}

$microsoft365BillingAccountId = Get-SingleBillingAccountName -AccountType 'Business'
$azureBillingAccountId = Get-SingleBillingAccountName -AccountType 'Individual'

$values = [ordered]@{
    InvoiceManager__Seed__DriveId                        = $driveId
    InvoiceManager__Seed__DriveName                      = $driveName
    InvoiceManager__Seed__Microsoft365FolderItemId       = $microsoft365FolderItemId
    InvoiceManager__Seed__AzureFolderItemId              = $azureFolderItemId
    InvoiceManager__Seed__Microsoft365TestFolderItemId   = $microsoft365TestFolderItemId
    InvoiceManager__Seed__AzureTestFolderItemId          = $azureTestFolderItemId
    InvoiceManager__Seed__BillingAccountId               = $microsoft365BillingAccountId
    InvoiceManager__Seed__AzureBillingAccountId          = $azureBillingAccountId
}

foreach ($entry in $values.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'User')
    Set-Item -Path "Env:$($entry.Key)" -Value $entry.Value
    Write-Host "Set $($entry.Key) in the current process and User environment."
}

Write-Host 'Seed environment variables are current for this process and the User environment.'
Write-Host 'Restart Visual Studio to refresh the variables inherited by the AppHost.'
