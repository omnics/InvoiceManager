data "azuread_client_config" "current" {}
data "azurerm_client_config" "current" {}

provider "azurerm" {
  features {}
}

# Authenticates via GITHUB_TOKEN (Deploy-Infra.ps1 sources it from `gh auth token`).
# Only exercised when var.manage_github is true (see github.tf).
provider "github" {
  owner = var.github_owner
}

resource "azurerm_resource_group" "invoice_manager" {
  name     = local.resource_group_name
  location = var.location
}

resource "azuread_application" "invoice_manager" {
  display_name            = local.application_display_name
  sign_in_audience        = "AzureADMyOrg"
  group_membership_claims = ["SecurityGroup"]

  web {
    # Append the deployed admin website callback to any caller-supplied URIs (e.g. the
    # local https://localhost:5001/signin-oidc). The URL is derived from the Container Apps
    # environment default domain plus the fixed app name, NOT from the container app
    # resource itself: the container app depends on this app registration (for ClientId),
    # so referencing it here would create a cycle. The environment has no such dependency.
    redirect_uris = concat(
      var.redirect_uris,
      local.workflow_redirect_uris,
      [local.adminweb_signin_redirect_uri, local.adminweb_workflow_redirect_uri]
    )
  }

  required_resource_access {
    resource_app_id = local.api_permissions.azure_resource_manager.application_id

    resource_access {
      id   = local.api_permissions.azure_resource_manager.scopes.user_impersonation
      type = "Scope"
    }
  }

  required_resource_access {
    resource_app_id = local.api_permissions.microsoft_graph.application_id

    resource_access {
      id   = local.api_permissions.microsoft_graph.scopes.user_read
      type = "Scope"
    }

    resource_access {
      id   = local.api_permissions.microsoft_graph.scopes.mail_read
      type = "Scope"
    }

    resource_access {
      id   = local.api_permissions.microsoft_graph.scopes.files_readwrite_all
      type = "Scope"
    }
  }
}

resource "azuread_group" "adminweb_administrators" {
  display_name     = local.admin_group_display_name
  security_enabled = true
}

# Manage only the deploying operator's direct membership. Additional direct
# members added in Entra remain untouched by Terraform.
resource "azuread_group_member" "deploying_operator_admin" {
  count = var.function_invoker_user_object_id == "" ? 0 : 1

  group_object_id  = azuread_group.adminweb_administrators.object_id
  member_object_id = var.function_invoker_user_object_id
}

resource "azuread_service_principal" "invoice_manager" {
  client_id = azuread_application.invoice_manager.client_id
}

resource "time_rotating" "admin_web_password" {
  rotation_days = 365
}

resource "azuread_application_password" "admin_web" {
  application_id = azuread_application.invoice_manager.id
  display_name   = "InvoiceManager Admin Web"
  end_date       = time_rotating.admin_web_password.rotation_rfc3339
  rotate_when_changed = {
    application_object_id = azuread_application.invoice_manager.object_id
    rotation              = time_rotating.admin_web_password.id
  }
}

resource "azurerm_cosmosdb_account" "invoice_manager" {
  name                = local.cosmos_account_name
  location            = azurerm_resource_group.invoice_manager.location
  resource_group_name = azurerm_resource_group.invoice_manager.name
  offer_type          = "Standard"
  kind                = "GlobalDocumentDB"

  consistency_policy {
    consistency_level = "Session"
  }

  geo_location {
    location          = azurerm_resource_group.invoice_manager.location
    failover_priority = 0
  }

  capabilities {
    name = "EnableServerless"
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_cosmosdb_sql_database" "invoice_manager" {
  name                = local.cosmos_database_name
  resource_group_name = azurerm_cosmosdb_account.invoice_manager.resource_group_name
  account_name        = azurerm_cosmosdb_account.invoice_manager.name

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_cosmosdb_sql_container" "invoice_configurations" {
  name                = "invoice-configurations"
  resource_group_name = azurerm_cosmosdb_account.invoice_manager.resource_group_name
  account_name        = azurerm_cosmosdb_account.invoice_manager.name
  database_name       = azurerm_cosmosdb_sql_database.invoice_manager.name
  partition_key_paths = ["/partitionKey"]

  # The partition key is intentionally replaceable while the application is in testing.
  lifecycle {
    prevent_destroy = false
  }
}

resource "azurerm_cosmosdb_sql_container" "invoice_records" {
  name                = "invoice-records"
  resource_group_name = azurerm_cosmosdb_account.invoice_manager.resource_group_name
  account_name        = azurerm_cosmosdb_account.invoice_manager.name
  database_name       = azurerm_cosmosdb_sql_database.invoice_manager.name
  partition_key_paths = ["/configurationId"]

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_cosmosdb_sql_container" "freeagent_interventions" {
  name                = "freeagent-interventions"
  resource_group_name = azurerm_cosmosdb_account.invoice_manager.resource_group_name
  account_name        = azurerm_cosmosdb_account.invoice_manager.name
  database_name       = azurerm_cosmosdb_sql_database.invoice_manager.name
  partition_key_paths = ["/recordId"]

  lifecycle {
    prevent_destroy = true
  }
}

# Grants the deploying identity Cosmos DB data-plane write access so the seeder
# can run immediately after terraform apply without a separate auth step.
resource "azurerm_cosmosdb_sql_role_assignment" "deployer_data_contributor" {
  resource_group_name = azurerm_resource_group.invoice_manager.name
  account_name        = azurerm_cosmosdb_account.invoice_manager.name
  role_definition_id  = "${azurerm_cosmosdb_account.invoice_manager.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
  principal_id        = data.azurerm_client_config.current.object_id
  scope               = azurerm_cosmosdb_account.invoice_manager.id
}

resource "azurerm_key_vault" "invoice_manager" {
  name                       = local.key_vault_name
  location                   = azurerm_resource_group.invoice_manager.location
  resource_group_name        = azurerm_resource_group.invoice_manager.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  soft_delete_retention_days = 7
  rbac_authorization_enabled = true

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_role_assignment" "terraform_key_vault_secrets_officer" {
  scope                = azurerm_key_vault.invoice_manager.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_key_vault_secret" "microsoft_authorization_client_secret" {
  name         = "MicrosoftAuthorization--ClientSecret"
  value        = azuread_application_password.admin_web.value
  key_vault_id = azurerm_key_vault.invoice_manager.id
  content_type = "Entra application password for InvoiceManager Admin Web"

  depends_on = [
    azurerm_role_assignment.terraform_key_vault_secrets_officer
  ]

  lifecycle {
    prevent_destroy = true
  }
}

# ---------------------------------------------------------------------------
# Managed identities
#
# One user-assigned identity per app. User-assigned (rather than system-assigned)
# lets us grant Key Vault / Cosmos / Storage RBAC BEFORE the apps exist, which
# matters because each app must already have Key Vault read access the first time
# it resolves secrets, and it keeps a stable principal across app recreation.
# ---------------------------------------------------------------------------

resource "azurerm_user_assigned_identity" "functions" {
  name                = local.functions_identity_name
  location            = azurerm_resource_group.invoice_manager.location
  resource_group_name = azurerm_resource_group.invoice_manager.name
}

resource "azurerm_user_assigned_identity" "adminweb" {
  name                = local.adminweb_identity_name
  location            = azurerm_resource_group.invoice_manager.location
  resource_group_name = azurerm_resource_group.invoice_manager.name
}

# ---------------------------------------------------------------------------
# Observability: shared Log Analytics workspace + workspace-based App Insights.
# The workspace also backs the Container Apps environment.
# ---------------------------------------------------------------------------

resource "azurerm_log_analytics_workspace" "main" {
  name                = local.log_analytics_name
  location            = azurerm_resource_group.invoice_manager.location
  resource_group_name = azurerm_resource_group.invoice_manager.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_application_insights" "main" {
  name                = local.application_insights_name
  location            = azurerm_resource_group.invoice_manager.location
  resource_group_name = azurerm_resource_group.invoice_manager.name
  workspace_id        = azurerm_log_analytics_workspace.main.id
  application_type    = "web"
}

# ---------------------------------------------------------------------------
# Document Intelligence (PDF invoice field extraction for the
# Microsoft365Email source — see docs/workflow.md#source-matching)
# ---------------------------------------------------------------------------

resource "azurerm_cognitive_account" "document_intelligence" {
  name                = local.document_intelligence_name
  location            = azurerm_resource_group.invoice_manager.location
  resource_group_name = azurerm_resource_group.invoice_manager.name
  kind                = "FormRecognizer"
  sku_name            = "S0"

  # AAD token auth requires a custom subdomain - without one, Entra ID auth always
  # fails with 401 regardless of role assignments, even though the endpoint hostname
  # looks subdomain-shaped by default.
  custom_subdomain_name = local.document_intelligence_name

  # RBAC-only data-plane access (see role assignment below); no key-based auth needed.
  local_auth_enabled = false
}

# ---------------------------------------------------------------------------
# Functions app (Flex Consumption) + host storage
# ---------------------------------------------------------------------------

resource "azurerm_storage_account" "functions" {
  name                            = local.functions_storage_name
  location                        = azurerm_resource_group.invoice_manager.location
  resource_group_name             = azurerm_resource_group.invoice_manager.name
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_container" "functions_deployment" {
  name                  = "deployment"
  storage_account_id    = azurerm_storage_account.functions.id
  container_access_type = "private"

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_service_plan" "functions" {
  name                = local.functions_plan_name
  location            = azurerm_resource_group.invoice_manager.location
  resource_group_name = azurerm_resource_group.invoice_manager.name
  os_type             = "Linux"
  sku_name            = "FC1"
}

resource "azurerm_function_app_flex_consumption" "functions" {
  name                = local.functions_app_name
  location            = azurerm_resource_group.invoice_manager.location
  resource_group_name = azurerm_resource_group.invoice_manager.name
  service_plan_id     = azurerm_service_plan.functions.id

  storage_container_type            = "blobContainer"
  storage_container_endpoint        = "${azurerm_storage_account.functions.primary_blob_endpoint}${azurerm_storage_container.functions_deployment.name}"
  storage_authentication_type       = "UserAssignedIdentity"
  storage_user_assigned_identity_id = azurerm_user_assigned_identity.functions.id

  runtime_name    = "dotnet-isolated"
  runtime_version = var.functions_runtime_version

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.functions.id]
  }

  site_config {
    application_insights_connection_string = azurerm_application_insights.main.connection_string
  }

  app_settings = {
    # AZURE_CLIENT_ID pins DefaultAzureCredential to this app's user-assigned identity
    # (both apps use a parameterless DefaultAzureCredential that cannot otherwise choose).
    AZURE_CLIENT_ID = azurerm_user_assigned_identity.functions.client_id

    # Identity-based AzureWebJobsStorage (no account key). The functions identity
    # already holds Storage Blob Data Owner + Storage Queue Data Contributor on this
    # account (see role assignments below), so the host secrets repository, timer
    # schedule state and singleton locks authenticate via the user-assigned identity
    # rather than a shared key. Without these, the platform falls back to a shared-key
    # connection string whose key goes stale on any storage recreate/rotation and the
    # host then fails with 403 AuthenticationFailed on the azure-webjobs-secrets
    # container, which stops all triggers (including the timer) from initializing.
    AzureWebJobsStorage__accountName = azurerm_storage_account.functions.name
    AzureWebJobsStorage__credential  = "managedidentity"
    AzureWebJobsStorage__clientId    = azurerm_user_assigned_identity.functions.client_id

    CosmosEndpoint = azurerm_cosmosdb_account.invoice_manager.endpoint
    CosmosDatabase = local.cosmos_database_name

    MicrosoftAuthorization__TenantId = data.azurerm_client_config.current.tenant_id
    MicrosoftAuthorization__ClientId = azuread_application.invoice_manager.client_id
    KeyVault__Uri                    = azurerm_key_vault.invoice_manager.vault_uri

    DocumentIntelligence__Endpoint = azurerm_cognitive_account.document_intelligence.endpoint

    # Non-secret: which FreeAgent host (sandbox/production) the unattended workflow calls.
    # Must match whichever FreeAgent app AdminWeb authorized, or every FreeAgent call fails.
    FreeAgent__Environment = local.freeagent_environment
  }

  # App Service Authentication (Easy Auth). Requires a valid Entra token on every
  # request and returns 401 otherwise; only bearer-token validation is needed, so no
  # client secret is configured. /api/health is excluded so the AdminWeb liveness probe
  # stays anonymous. Authorization to specific principals is enforced by the app role +
  # assignment_required on azuread_service_principal.functions (see functions_auth.tf).
  auth_settings_v2 {
    auth_enabled           = true
    require_authentication = true
    unauthenticated_action = "Return401"
    excluded_paths         = ["/api/health"]

    active_directory_v2 {
      client_id            = azuread_application.functions.client_id
      tenant_auth_endpoint = "https://login.microsoftonline.com/${data.azurerm_client_config.current.tenant_id}/v2.0"
      allowed_audiences    = ["api://${azuread_application.functions.client_id}"]
    }

    login {
      token_store_enabled = false
    }
  }

  depends_on = [
    azurerm_role_assignment.functions_storage_blob_owner,
    azurerm_role_assignment.functions_storage_queue_contributor,
  ]
}

# ---------------------------------------------------------------------------
# Admin website (Azure Container Apps, public ghcr image)
# ---------------------------------------------------------------------------

resource "azurerm_container_app_environment" "main" {
  name                       = local.container_app_environment_name
  location                   = azurerm_resource_group.invoice_manager.location
  resource_group_name        = azurerm_resource_group.invoice_manager.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
}

resource "azurerm_container_app" "adminweb" {
  name                         = local.adminweb_container_app_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.invoice_manager.name
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.adminweb.id]
  }

  # No registry/secret block: the ghcr package is public, so the pull is anonymous.

  template {
    min_replicas = 0
    max_replicas = 1

    container {
      name   = "adminweb"
      image  = var.adminweb_image
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.adminweb.client_id
      }
      env {
        # ASP.NET Core listens here; matches the ingress target_port below.
        name  = "ASPNETCORE_HTTP_PORTS"
        value = "8080"
      }
      env {
        name  = "CosmosEndpoint"
        value = azurerm_cosmosdb_account.invoice_manager.endpoint
      }
      env {
        name  = "CosmosDatabase"
        value = local.cosmos_database_name
      }
      env {
        name  = "MicrosoftAuthorization__TenantId"
        value = data.azurerm_client_config.current.tenant_id
      }
      env {
        name  = "MicrosoftAuthorization__ClientId"
        value = azuread_application.invoice_manager.client_id
      }
      env {
        name  = "KeyVault__Uri"
        value = azurerm_key_vault.invoice_manager.vault_uri
      }
      env {
        name  = "FreeAgent__Environment"
        value = local.freeagent_environment
      }
      env {
        name  = "AdminAuthorization__GroupObjectId"
        value = azuread_group.adminweb_administrators.object_id
      }
      env {
        name  = "Functions__BaseUrl"
        value = "https://${azurerm_function_app_flex_consumption.functions.default_hostname}"
      }
      env {
        # Audience the AdminWeb managed identity requests a token for when calling the
        # Easy Auth-protected Functions app. The ".default" scope yields an app-only
        # token carrying the assigned "Invoke" role.
        name  = "Functions__Scope"
        value = "api://${azuread_application.functions.client_id}/.default"
      }
      env {
        name  = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        value = azurerm_application_insights.main.connection_string
      }
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  lifecycle {
    # CI deploys concrete image tags via `az containerapp update --image ...:<sha>`.
    # Ignore image drift so Terraform does not revert to the bootstrap reference.
    ignore_changes = [template[0].container[0].image]
  }
}

# ---------------------------------------------------------------------------
# RBAC: Key Vault (data plane)
#
# Both identities get Secrets Officer, not the read-only Secrets User: each app
# reads AND writes MicrosoftAuthorization--MsalTokenCache (MSAL silent-refresh
# persists the updated cache back), which requires set permission.
# ---------------------------------------------------------------------------

resource "azurerm_role_assignment" "functions_key_vault_secrets_officer" {
  scope                = azurerm_key_vault.invoice_manager.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = azurerm_user_assigned_identity.functions.principal_id
}

resource "azurerm_role_assignment" "adminweb_key_vault_secrets_officer" {
  scope                = azurerm_key_vault.invoice_manager.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = azurerm_user_assigned_identity.adminweb.principal_id
}

# ---------------------------------------------------------------------------
# RBAC: Cosmos DB (data plane)
#
# Functions reads/writes invoice records -> Data Contributor.
# AdminWeb administers configurations, revisions, and record snapshots -> Data Contributor.
# ---------------------------------------------------------------------------

resource "azurerm_cosmosdb_sql_role_assignment" "functions_data_contributor" {
  resource_group_name = azurerm_resource_group.invoice_manager.name
  account_name        = azurerm_cosmosdb_account.invoice_manager.name
  role_definition_id  = "${azurerm_cosmosdb_account.invoice_manager.id}/sqlRoleDefinitions/${local.cosmos_data_contributor_role_id}"
  principal_id        = azurerm_user_assigned_identity.functions.principal_id
  scope               = azurerm_cosmosdb_account.invoice_manager.id
}

resource "azurerm_cosmosdb_sql_role_assignment" "adminweb_data_contributor" {
  resource_group_name = azurerm_resource_group.invoice_manager.name
  account_name        = azurerm_cosmosdb_account.invoice_manager.name
  role_definition_id  = "${azurerm_cosmosdb_account.invoice_manager.id}/sqlRoleDefinitions/${local.cosmos_data_contributor_role_id}"
  principal_id        = azurerm_user_assigned_identity.adminweb.principal_id
  scope               = azurerm_cosmosdb_account.invoice_manager.id
}

moved {
  from = azurerm_cosmosdb_sql_role_assignment.adminweb_data_reader
  to   = azurerm_cosmosdb_sql_role_assignment.adminweb_data_contributor
}

# ---------------------------------------------------------------------------
# RBAC: Functions host storage (identity-based AzureWebJobsStorage + deployment)
# ---------------------------------------------------------------------------

resource "azurerm_role_assignment" "functions_storage_blob_owner" {
  scope                = azurerm_storage_account.functions.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = azurerm_user_assigned_identity.functions.principal_id
}

resource "azurerm_role_assignment" "functions_storage_queue_contributor" {
  scope                = azurerm_storage_account.functions.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_user_assigned_identity.functions.principal_id
}

# ---------------------------------------------------------------------------
# RBAC: Document Intelligence (data plane)
# ---------------------------------------------------------------------------

resource "azurerm_role_assignment" "functions_document_intelligence_user" {
  scope                = azurerm_cognitive_account.document_intelligence.id
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_user_assigned_identity.functions.principal_id
}

# Grants the deploying identity data-plane access so local development (which
# authenticates via DefaultAzureCredential's developer credential fallback,
# not the Functions managed identity) can call Document Intelligence too.
resource "azurerm_role_assignment" "deployer_document_intelligence_user" {
  scope                = azurerm_cognitive_account.document_intelligence.id
  role_definition_name = "Cognitive Services User"
  principal_id         = data.azurerm_client_config.current.object_id
}
