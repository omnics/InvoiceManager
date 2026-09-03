environment = "test"

redirect_uris = [
  "https://localhost:5001/signin-oidc"
]

# The test environment always authorizes against the FreeAgent sandbox company (see
# locals.freeagent_environment) - this is that company's own web-app subdomain.
freeagent_subdomain = "omnicssandbox"
