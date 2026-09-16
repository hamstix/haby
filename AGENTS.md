# Agent instructions

These instructions apply to the entire repository.

## Read first

1. Read [CONTRIBUTING.md](CONTRIBUTING.md) before changing code or public documentation.
2. Read the relevant milestone in [ROADMAP.md](ROADMAP.md) for architectural or cross-cutting work.
3. Inspect the real repository and Git status before proposing or editing. Preserve user changes and do not rewrite unrelated files.

User instructions override this file. A more specific nested `AGENTS.md`, if added later, overrides this file only for its directory subtree.

## Non-negotiable rules

- Keep this public repository product-neutral. Do not introduce private organization, product or customer names, packages, endpoints or operational conventions.
- Never commit credentials, tokens, private connection strings or production configuration.
- Do not perform destructive Git operations or discard user changes.
- Do not create commits, push branches, open pull requests or change remote settings unless the user explicitly requests it.
- For regressions, add a focused failing test before changing production code.
- Keep changes scoped to one coherent outcome; avoid opportunistic refactors and bulk formatting.
- Write source code, public documentation, branch suggestions, commit messages and pull-request text in English.

## Working procedure

1. Identify applicable instructions and inspect nearby code, contracts and tests.
2. Reproduce the behavior or establish the baseline before editing.
3. Make the smallest complete change.
4. Run focused validation, then all applicable solution validation.
5. Run `git diff --check` and inspect the final diff and Git status.
6. Report code failures separately from environment, restore, network or permission blockers.

For persisted EF Core behavior, follow the database-test isolation rules in [CONTRIBUTING.md](CONTRIBUTING.md): arrange, act and assert with separate `DbContext` instances unless tracking behavior is explicitly under test.

Use the solution-level commands documented in [CONTRIBUTING.md](CONTRIBUTING.md). If restore or build cannot complete, do not claim that the code failed without a final compiler or test result proving it.

## Architecture constraints

- Domain and application logic must not depend on ASP.NET Core hosting, EF Core, UI packages or concrete plugins.
- Public contracts and plugin abstractions require compatibility consideration and focused tests.
- External side effects must be cancellation-aware and designed for idempotency and retry.
- Secrets must be redacted from logs, errors, snapshots and test fixtures.
- Keep PostgreSQL as the default source of truth unless an accepted ADR changes that decision.
- Treat Kubernetes support as an integration or optional mode, not as an implicit mandatory runtime.
- Use MudBlazor for generic UI primitives when the UI roadmap milestone begins; create local components only for Haby-specific semantics.

## Repository workflow

- Use short-lived topic branches named according to [CONTRIBUTING.md](CONTRIBUTING.md); do not introduce a long-lived `develop` branch.
- Use English Conventional Commit titles.
- Design pull requests for squash merge into a linear default branch history.
- Do not mix roadmap milestones in one pull request unless the user explicitly changes the scope.
