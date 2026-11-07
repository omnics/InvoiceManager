environment = "production"

redirect_uris = [
  "https://localhost:5001/signin-oidc"
]

# Default when Deploy-Infra.ps1 is run without -EnableGenerationTimer:true|false. Unattended
# hourly processing was switched on at go-live step 11.
functions_generation_timer_enabled = true

monthly_budget_amount = 10
