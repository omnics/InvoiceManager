environment = "production"

redirect_uris = [
  "https://localhost:5001/signin-oidc"
]

# Default when Deploy-Infra.ps1 is run without -EnableGenerationTimer:true|false. Held off until
# unattended processing is deliberately switched on (go-live step 11); change this to true then,
# so later production runs don't need the parameter to keep it on.
functions_generation_timer_enabled = false
