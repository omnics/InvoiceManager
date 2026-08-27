# Claude Code Instructions

Use [AGENTS.md](AGENTS.md) as the primary instruction file for this repository.

Before implementation work, also read:

- [docs/product.md](docs/product.md)
- [docs/architecture.md](docs/architecture.md)
- [docs/domain-model.md](docs/domain-model.md)
- [docs/data-model.md](docs/data-model.md)
- [docs/coding-standards.md](docs/coding-standards.md) — C# conventions: unions over exceptions, `Option<T>` over null, strong typing
- [docs/deployment.md](docs/deployment.md) — deployment strategy, CI/CD pipeline, and infrastructure as code

This project is a C# invoice automation service intended to run as an Azure
Functions isolated worker app, with local development orchestrated by Aspire.

## Pull Request Workflow (Claude Code only)

This workflow is specific to Claude Code. It does not apply to other agents
that read [AGENTS.md](AGENTS.md) (Codex, GitHub Copilot) when working in this
repository directly.

Run this whole loop automatically once a PR is created — do not pause to ask
for confirmation before invoking codex or before starting another review
round. Only stop and ask if genuinely blocked (e.g. codex itself fails to
run, or a comment's correct resolution is ambiguous enough to need a human
call).

When asked to create a pull request:

1. If the change touches any `InvoiceManager.AdminWeb` `.cshtml`/`.cshtml.cs`
   file, complete the browser verification in
   [AGENTS.md's UI Changes section](AGENTS.md#ui-changes-invoicemanageradminweb)
   first — do not open the PR with this only noted as an unchecked test-plan
   item.
2. Commit and push the current changes, then open the PR (`gh pr create`).
3. Ask the `codex` CLI to review the PR and post its findings as inline PR
   review comments (check `codex --help` for the current review/comment
   invocation if unfamiliar with it).
4. Once codex's comments are posted, critically review each one yourself —
   do not apply a change just because codex suggested it.
   - For a comment you agree with, make the corresponding code change in its
     own separate commit, unless that doesn't make sense in context (e.g.
     several comments point at the same root cause and can only be fixed
     together).
   - For a comment you disagree with, reply to it explaining why and close
     it, rather than leaving it unaddressed and unexplained.
5. Push any resulting changes, and resolve/close the comments that are now
   addressed by that push.
6. Ask codex to review again.
7. Repeat steps 4-6 until a codex review pass completes with no further
   comments posted.
