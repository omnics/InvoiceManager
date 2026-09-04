variable "environment" {
  description = "Deployment environment. Production uses unsuffixed resource names."
  type        = string

  validation {
    condition     = contains(["test", "production"], var.environment)
    error_message = "Environment must be either test or production."
  }
}

variable "display_name" {
  description = "Human-readable application display name."
  type        = string
  default     = "InvoiceManager"
}

variable "application_name" {
  description = "Lowercase application name used in Azure resource names."
  type        = string
  default     = "invoicemanager"

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,14}[a-z0-9]$", var.application_name))
    error_message = "Application name must be 3-16 lowercase letters, numbers, or hyphens, start with a letter, and end with a letter or number."
  }
}

variable "location" {
  description = "Azure region for environment resources."
  type        = string
  default     = "uksouth"
}

variable "redirect_uris" {
  description = "Additional allowed web redirect URIs (e.g. https://localhost:5001/signin-oidc for local admin auth). The deployed Container Apps callback is appended automatically."
  type        = list(string)
  default     = []
}

variable "adminweb_image" {
  description = "Container image for the admin website, published to a public ghcr.io package. CI updates the running tag out-of-band, so this is only the initial/bootstrap reference."
  type        = string
  default     = "mcr.microsoft.com/azuredocs/aci-helloworld:latest"
}

variable "functions_runtime_version" {
  description = "dotnet-isolated stack version for the Flex Consumption Functions app. Flex supports 8.0/9.0/10.0 (not net11.0 yet), so the app is published as net10.0 from its multi-targeted project."
  type        = string
  default     = "10.0"
}

variable "function_invoker_user_object_id" {
  description = "Entra object ID of the operator granted the Functions 'Invoke' app role (so they can call the endpoint directly). Deploy-Infra.ps1 derives this from the signed-in user (az ad signed-in-user show); no account identity is hardcoded. Empty for a CI/service-principal apply, which manages no operator assignment."
  type        = string
  default     = ""
}

# ---------------------------------------------------------------------------
# GitHub CI consolidation
#
# No account identity is hardcoded here: Deploy-Infra.ps1 derives these from the
# authenticated `gh` context and passes them as -var. The empty-string defaults
# only exist so a manage_github = false apply is not forced to supply them.
# ---------------------------------------------------------------------------

variable "github_owner" {
  description = "GitHub account/organization that owns the repository. Must be provided by Deploy-Infra.ps1 (derived from `gh repo view`); required when manage_github is true."
  type        = string
  default     = ""
}

variable "github_owner_id" {
  description = "Numeric id of the GitHub account/organization from github_owner. Must be provided by Deploy-Infra.ps1 (derived from `gh api repos/{owner}/{repo}`); required when manage_github is true. Used in the OIDC federated credential's immutable subject so a repo/org rename or transfer can't be replayed by a new owner of the recycled name."
  type        = string
  default     = ""
}

variable "github_repository" {
  description = "GitHub repository name. Must be provided by Deploy-Infra.ps1 (derived from `gh repo view`); required when manage_github is true."
  type        = string
  default     = ""
}

variable "github_repository_id" {
  description = "Numeric id of the GitHub repository from github_repository. Must be provided by Deploy-Infra.ps1 (derived from `gh api repos/{owner}/{repo}`); required when manage_github is true. Used in the OIDC federated credential's immutable subject; see github_owner_id."
  type        = string
  default     = ""
}

variable "production_reviewer" {
  description = "GitHub login of the required reviewer for the production deploy environment. Must be provided by Deploy-Infra.ps1 (derived from `gh api user`) for production applies."
  type        = string
  default     = ""
}

variable "manage_github" {
  description = "When true, Terraform owns the GitHub deploy environment, its protection rules, the CI identity secrets, and the deploy-target variables. Set false for a GitHub-less apply."
  type        = bool
  default     = true
}
