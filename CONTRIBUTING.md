# Contributing to Haby

Thank you for helping improve Haby. This guide describes the development workflow for maintainers, occasional contributors and first-time contributors.

## Before you start

- Read [ROADMAP.md](ROADMAP.md) before starting architectural or cross-cutting work.
- Search existing issues and pull requests before opening a duplicate.
- Open an issue before implementing a breaking API, storage or plugin-contract change.
- Keep the public repository product-neutral. Product-specific integrations belong in downstream plugins or distributions.
- Never commit credentials, tokens, connection strings containing secrets or production configuration.

Haby is being modernized incrementally. A pull request should complete one coherent step without mixing unrelated modernization work.

## Branching model

Haby follows GitHub Flow. The default branch must remain buildable and potentially releasable. There is no long-lived `develop` branch.

Create a short-lived branch from the current default branch. Use lowercase names with a type and a concise kebab-case description:

```text
docs/oss-revival-roadmap
chore/m0-reproducible-baseline
build/dotnet-10-migration
fix/42-scope-variable-deletion
feat/57-plugin-manifest
refactor/remove-private-dependencies
```

Recommended branch types:

- `feat/` for user-visible capabilities;
- `fix/` for defects;
- `refactor/` for behavior-preserving restructuring;
- `test/` for test-only changes;
- `build/` for SDK, dependency and build-system changes;
- `ci/` for automation;
- `docs/` for documentation;
- `chore/` for repository maintenance that fits no narrower type.

Do not create milestone-sized branches such as `develop`, `rewrite` or `oss-revival`.

## Development workflow

1. Synchronize the default branch and create a topic branch.
2. Reproduce the current behavior before editing it.
3. For a defect, add a focused failing test before changing production code.
4. Implement the smallest coherent change that satisfies the issue.
5. Run the relevant focused tests while iterating.
6. Run the full applicable validation before opening the pull request.
7. Open a draft pull request early when design feedback would reduce rework.
8. Update documentation when behavior, public contracts or operator procedures change.

Do not hide baseline failures. Clearly distinguish existing failures, environment restrictions and failures introduced by the change.

## Build and validation

The reproducible baseline is being established in milestone M0. Until its commands are automated, use the solution-level .NET workflow:

```powershell
dotnet restore .\HamstixHaby.sln
dotnet build .\HamstixHaby.sln --no-restore
dotnet test .\HamstixHaby.sln --no-build
git diff --check
```

Run narrower project or filtered tests first when that gives faster feedback, but do not substitute them for applicable solution validation in the final report.

Integration tests that need external infrastructure must be explicitly identifiable and documented. Unit tests must not silently depend on a developer's PostgreSQL, Kubernetes, RabbitMQ or other local service.

## Code and architecture expectations

- Keep domain and application logic independent from hosting, persistence, UI and plugin implementations.
- Prefer explicit contracts and dependency injection over global state or runtime service location.
- Preserve cancellation tokens through I/O and long-running operations.
- Provisioning operations must be designed for idempotency and safe retries.
- Do not log secrets or return them in diagnostic error details.
- Validate externally supplied templates and configuration before applying side effects.
- Avoid unrelated formatting, renaming or dependency updates in a focused pull request.
- Add public documentation for public APIs and plugin extension points.
- Write source code, public documentation, issue titles, pull-request titles and commit messages in English.

## Commits

Use Conventional Commit subjects in English:

```text
docs: add OSS revival roadmap
build: establish reproducible .NET 10 baseline
test: cover configuration variable isolation
fix: scope variable deletion to configuration unit
feat: add plugin manifest contract
```

Keep local commits reviewable, but do not spend time manufacturing a perfect branch history. Pull requests are squash-merged, so the pull-request title becomes the permanent commit subject on the default branch.

Do not combine unrelated changes into one commit merely because they were developed in the same session.

## Pull requests

Each pull request should describe:

- the problem and why it matters;
- the chosen solution and important trade-offs;
- tests and validation performed;
- known limitations or follow-up work;
- compatibility or migration impact;
- the linked issue, using `Closes #123` when appropriate.

Use a Conventional Commit title. Keep the pull request limited to one reviewable outcome. Architectural changes should include or reference an Architecture Decision Record before broad implementation begins.

Maintainers use squash merge to keep the default branch linear. Do not force-push after review has started unless rebasing or removing sensitive data makes it necessary; notify reviewers when previously reviewed commits change materially.

## Documentation and decisions

User and contributor documentation belongs in Markdown files in the repository. Significant architectural decisions should be captured in `docs/adr/` once that directory is introduced.

Documentation should explain current behavior, not only intended behavior. Planned features belong in [ROADMAP.md](ROADMAP.md).

## Dependency and UI policy

- Prefer actively maintained dependencies with permissive licenses suitable for open-source and commercial downstream use.
- Record a dependency's purpose; avoid overlapping libraries that solve the same problem.
- Use MudBlazor for generic administration UI components once the UI milestone begins.
- Build local UI components only for recurring Haby domain semantics, not as wrappers around every library primitive.
- Keep product-specific client compatibility and private integrations outside this repository.

## Licensing

By contributing, you agree that your contribution is provided under the repository's Apache License 2.0. Add third-party code only when its license is compatible and preserve any required notices.
