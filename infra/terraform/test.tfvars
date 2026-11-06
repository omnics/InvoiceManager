environment = "test"

redirect_uris = [
  "https://localhost:5001/signin-oidc"
]

# Default when Deploy-Infra.ps1 is run without -EnableGenerationTimer:true|false.
functions_generation_timer_enabled = true
