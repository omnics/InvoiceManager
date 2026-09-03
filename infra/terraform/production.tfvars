environment = "production"

redirect_uris = [
  "https://localhost:5001/signin-oidc"
]

# TODO: set to the production FreeAgent company's own web-app subdomain (the part before
# .freeagent.com when browsing FreeAgent) once that account exists - see
# locals.freeagent_environment and variables.freeagent_subdomain. Left empty rather than
# guessed; AdminWeb's "Open FreeAgent bill" action stays hidden until this is set.
freeagent_subdomain = ""
