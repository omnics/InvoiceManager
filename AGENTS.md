# Agent Instructions

This file is the shared instruction entry point for coding agents working in
this repository, including Codex, Claude Code, and GitHub Copilot.

Before making implementation changes, read:

- [docs/product.md](docs/product.md)
- [docs/architecture.md](docs/architecture.md)
- [docs/domain-model.md](docs/domain-model.md)
- [docs/data-model.md](docs/data-model.md)
- [docs/coding-standards.md](docs/coding-standards.md) — C# conventions: unions over exceptions, `Option<T>` over null, strong typing
- [docs/deployment.md](docs/deployment.md) — deployment strategy, CI/CD pipeline, and infrastructure as code

## Project Intent

InvoiceManager is a C# service for retrieving company invoices from external
systems, saving them to OneDrive, and attaching them to FreeAgent bills. It is
intended to run unattended in Azure and locally through Aspire.

## Technical Direction

- Use C# wherever appropriate.
- Use Azure Functions isolated worker for the hosted background service.
- Use Aspire for local development orchestration.
- Use Azure Cosmos DB for NoSQL in serverless mode for persistent storage.
- Use Azure Key Vault for secrets.
- Use Application Insights and Azure Monitor for observability.
- Use xUnit.net for unit tests.

## Architecture Rules

- Keep provider-specific invoice retrieval logic behind integration interfaces.
- Do not put OpenAI, Microsoft 365, Azure, OneDrive, or FreeAgent-specific logic
  in the core workflow unless the architecture document explicitly allows it.
- Treat the core workflow as responsible for deciding what invoice is expected,
  whether it has been retrieved, where it should be saved, and what FreeAgent
  action is required.
- Keep integrations focused on external system behavior such as fetching an
  invoice, saving a file, or uploading an attachment.
- Do not hard-code secrets, credentials, tenant IDs, API keys, or personal data.
- Update the relevant documentation when changing architectural decisions,
  storage shape, integration behavior, or domain terminology.
- When a C# type is serialized to JSON and consumed by hand-written JavaScript
  (e.g. `wwwroot/js/*.js` fetching an AJAX handler's `JsonResult`), there is no
  compiler across that boundary. Renaming or reshaping such a type must be
  paired with a search of `wwwroot/js` for that endpoint's consumers in the
  same change, not left for a later bug report.
- See [docs/coding-standards.md](docs/coding-standards.md) for C#-level
  conventions (exceptions vs. union return types, `Option<T>` over null,
  strong typing, enumerating external API failure modes explicitly).

## Domain-Sensitive Behavior

Be careful with:

- Invoice dates and ISO date formatting.
- Currency values and rounding.
- VAT inclusive (`inc`) and VAT exclusive (`exc`) totals.
- OneDrive folder destinations and generated filenames.
- FreeAgent bill totals and attachment URLs.
- Expected invoice frequency and missing-invoice detection.

## Testing Expectations

- Add or update xUnit.net tests for domain logic, workflow decisions, filename
  generation, schedule calculations, and provider-independent behavior.
- Prefer integration tests with fakes or test doubles before calling real
  external services.
- Do not require real secrets or paid cloud resources for unit tests.
- Several suites are tagged `[Trait("Category", "Integration")]` and are
  **excluded from CI** (`ci.yml` runs `dotnet test --filter "Category!=Integration"`)
  because they need local infrastructure or real credentials CI doesn't have:
  `InvoiceManager.Infrastructure.IntegrationTests` (Cosmos emulator),
  `InvoiceManager.AdminWeb.PlaywrightTests` (real signed-in browser session),
  and `InvoiceManager.Integrations.FreeAgent.IntegrationTests` (real FreeAgent
  sandbox). Because none of these run in CI, they are the *only* signal that
  catches regressions in their area — **always run all of them locally before
  pushing any change**, not only changes that obviously touch that area (a
  change elsewhere can still break persistence, the admin UI, or the FreeAgent
  integration in ways that aren't obvious from the diff). `InvoiceManager.AppHost.IntegrationTests`
  boots the real Aspire orchestration end-to-end and should be run alongside them.
  See [docs/deployment.md](docs/deployment.md#local-playwright-auth-state-for-the-admin-website)
  for Playwright auth-state setup/refresh and FreeAgent sandbox prerequisites.
- The Cosmos emulator integration tests require Docker running:
  `dotnet test tests/InvoiceManager.Infrastructure.IntegrationTests`.
- The Playwright suite additionally needs real, Graph-verifiable Microsoft 365
  credentials/IDs (see `tools/dev-setup/Set-SeedEnvironment.ps1`) and a saved
  Playwright storage state (`playwright/.auth/adminweb.json`) — it is the one
  suite most likely to pass locally for the author and still be silently
  broken for CI/other reviewers, so treat a green run of it as informative,
  not authoritative. Don't run the whole suite just to check whether the saved
  session is still valid — that pays the full Aspire-orchestration boot cost
  and then times out repeatedly (30s per test) once every test hits the same
  sign-in redirect, burning several minutes to learn one fact. Instead run the
  cheap two-test smoke check first:
  `dotnet test tests/InvoiceManager.AdminWeb.PlaywrightTests --filter "FullyQualifiedName~AdminWebSignInSmokeTests"`.
  If it fails on an Entra sign-in redirect instead of reaching the admin UI,
  the saved session has expired — re-capture it with
  `dotnet run --project tools/InvoiceManager.PlaywrightAuth` (reuses a
  persistent Edge profile, so if that profile still has an active Microsoft
  sign-in it can complete without any interactive prompt), then re-run the
  smoke test to confirm before running the full suite.
- When you make a previously-permissive code path stricter (e.g. adding a new
  server-side check, live verification call, or discovery-list membership
  requirement to a form submission), grep the test suites for existing tests
  that submit synthetic/fabricated data through that same path — they are
  the most likely thing to silently start failing, and won't show up in CI if
  they live in the Cosmos or Playwright integration projects above.
- Test/dev bootstrap code (module initializers, fixture setup) should fail
  fast with a clear, actionable message when a required *real* external value
  is missing, rather than silently substituting a synthetic placeholder. A
  silent default turns a one-line "set this environment variable" error into
  a confusing failure that only surfaces later, inside whatever external call
  actually needed the real value.
- Related to the point above: a test that grabs "whichever option loaded
  first" from a *shared* real resource (e.g. a discovery-list dropdown backed
  by the tenant's real, small set of billing accounts) rather than a value it
  fully controls can start colliding with seeded data or another test's data
  the moment a validation rule gets stricter, non-deterministically depending
  on API response ordering. Prefer generating a value the test owns
  end-to-end (e.g. a GUID-suffixed string) over selecting an arbitrary
  existing one, and when a shared resource must be used, select the specific
  one the test's assertions actually depend on rather than "the first one."

## UI Changes (`InvoiceManager.AdminWeb`)

A change is not complete just because it builds and the automated suites
pass — `InvoiceManager.AdminWeb.PlaywrightTests` does not cover most pages,
and a passing build says nothing about whether a new element actually
renders correctly or looks right next to its neighbors. Before considering
any change to a `.cshtml`/`.cshtml.cs` file (a new page, button, form field,
or layout element) done:

- **Run it in a browser.** Start Aspire locally (or use an already-running
  instance) and actually exercise the changed page/action as a user would —
  click the button, submit the form, trigger the error path if one exists.
  Confirm it does what it's supposed to, not just that it renders without a
  server error.
- **Check visual consistency**, not just function: spacing/padding, colors,
  fonts, and button/element styling should match the surrounding page. Reuse
  an existing CSS class (e.g. `secondary-action`, `primary-action`,
  `notice`) rather than introduce new ad hoc styling — a new element built by
  copying a neighboring element's markup should visually blend in, and that
  assumption needs to be confirmed by looking at it, not just inferred from
  having copied the right class names.
- **State plainly in the PR description whether this happened.** Do not
  leave an unchecked "not yet verified in a browser" box in a test plan and
  then take no further action on it — either do the check before the PR is
  considered ready, or say explicitly (not just in a checklist) that it
  still needs to happen and who/what will do it.
- If Playwright coverage exists for the touched page, run that suite locally
  per [Testing Expectations](#testing-expectations) above. If it doesn't,
  the manual browser check above is the *only* verification this change
  gets — treat it as mandatory, not optional, for exactly that reason.

## Reviewing a Pull Request

When performing a code review on a PR in this repository (any reviewing
agent, human-triggered or automated):

- Examine existing comment threads on the PR before posting new ones — do not
  add a comment that duplicates an existing thread's point, even if you
  arrived at it independently.
- If you disagree with the stated reason a thread was resolved without a
  code change, re-open that thread and explain why. A resolved thread is
  collapsed by default and excluded by an "unresolved" filter, so a comment
  left on a thread that stays resolved is easy for the PR author to miss
  entirely — do not rely on it as your only signal. Only fall back to a
  fresh comment (explicitly linking back to the original thread) if the
  tooling available to you genuinely cannot unresolve a thread; do not use it
  as a default choice between equally good options.
- Explicitly check new/changed domain and document types against
  [docs/coding-standards.md](docs/coding-standards.md) — in particular "make
  invalid states unrepresentable with strong typing": a bare `string`/`Uri`
  field standing in for something more specific, a boolean paired with a
  sibling value that is only meaningful when the boolean is true (prefer an
  `Option<T>` wrapping a record that carries both), or a nullable field
  without a corresponding `Option<T>` domain type. This check is easy to skip
  when a change otherwise looks correct and well-tested, so treat it as a
  mandatory step rather than something to fall back on only when something
  else looks off.
