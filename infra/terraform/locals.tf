locals {
  environment_suffix = var.environment == "production" ? "" : "-${var.environment}"

  application_display_name = "${var.display_name}${local.environment_suffix}"
  admin_group_display_name = "InvoiceManager-Administrators${local.environment_suffix}"
  resource_name_prefix     = "${var.application_name}${local.environment_suffix}"
  resource_group_name      = "rg-${local.resource_name_prefix}"
  key_vault_name           = "${local.resource_name_prefix}-kv"
  cosmos_account_name      = "${local.resource_name_prefix}-cosmos"
  cosmos_database_name     = "invoicemanager"

  # FreeAgent has exactly two hosts (see FreeAgentHosts in Infrastructure); production always
  # talks to the real FreeAgent company, every other environment talks to the sandbox.
  freeagent_environment = var.environment == "production" ? "Production" : "Sandbox"

  # Per-environment CI identity display name (e.g. InvoiceManager-GitHubActions-test;
  # unsuffixed for production).
  github_actions_app_display_name = "InvoiceManager-GitHubActions${local.environment_suffix}"

  # Entra app registration fronting the Functions app for Easy Auth (the token audience).
  functions_app_registration_display_name = "InvoiceManager-Functions${local.environment_suffix}"

  # Stable id for the Functions "Invoke" app role (must be a fixed GUID so it is not
  # churned on every apply).
  functions_invoke_app_role_id = "b6a7c0e2-3f14-4d5b-9a8c-2e1f7d6c5b40"

  log_analytics_name        = "${local.resource_name_prefix}-logs"
  application_insights_name = "${local.resource_name_prefix}-appi"

  functions_identity_name = "${local.resource_name_prefix}-functions-id"
  adminweb_identity_name  = "${local.resource_name_prefix}-adminweb-id"

  functions_app_name  = "${local.resource_name_prefix}-functions"
  functions_plan_name = "${local.resource_name_prefix}-functions-plan"
  # Storage account names allow only lowercase letters/digits, max 24 chars.
  functions_storage_name = substr(replace("${local.resource_name_prefix}fnstg", "-", ""), 0, 24)

  # Cognitive Services account names allow only letters/digits/hyphens, max 64 chars.
  document_intelligence_name = "${local.resource_name_prefix}-docintel"

  container_app_environment_name = "${local.resource_name_prefix}-cae"
  # A stable app name lets us precompute the ingress FQDN for the OIDC redirect URI
  # from the environment default domain, avoiding a dependency cycle with the app
  # registration (see main.tf, azuread_application redirect_uris).
  adminweb_container_app_name = "adminweb"

  # The built-in serverless profile of a workload-profiles Container Apps environment.
  container_app_workload_profile_name = "Consumption"
  # Precomputed from the environment default domain (not the app resource) to avoid a
  # dependency cycle with the app registration that consumes it as a redirect URI.
  adminweb_fqdn                  = "${local.adminweb_container_app_name}.${azurerm_container_app_environment.main.default_domain}"
  adminweb_signin_redirect_uri   = "https://${local.adminweb_fqdn}/signin-oidc"
  adminweb_workflow_redirect_uri = "https://${local.adminweb_fqdn}/workflow-signin-oidc"
  workflow_redirect_uris         = [for uri in var.redirect_uris : replace(uri, "/signin-oidc", "/workflow-signin-oidc")]

  # Built-in Cosmos DB SQL data-plane role definition ids (fixed GUIDs on every account).
  cosmos_data_contributor_role_id = "00000000-0000-0000-0000-000000000002"

  api_permissions = {
    azure_resource_manager = {
      application_id = "797f4846-ba00-4fd7-ba43-dac1f8f63013"
      scopes = {
        user_impersonation = "41094075-9dad-400e-a0bd-54e686782033"
      }
    }

    microsoft_graph = {
      application_id = "00000003-0000-0000-c000-000000000000"
      scopes = {
        user_read = "e1fe6dd8-ba31-4d61-89e7-88639da4683d"
        # Delegated Mail.Read: lets the Functions app search the signed-in admin's
        # mailbox for the Microsoft365Email invoice source (see docs/workflow.md).
        mail_read           = "570282fd-fa5c-430d-a7fd-fc8dc98a9dca"
        files_readwrite_all = "863451e7-0667-486c-a5d6-d135439485f0"
      }
    }
  }
}
