# Provider checks and alias administration (PR design notes)

The alpha commands use registry APIs only. Every revision is explicit.

```text
habyctl provider check NAME --revision N --environment ENV
habyctl provider alias get ALIAS --environment ENV
habyctl provider alias set ALIAS --provider NAME --revision N --expect-absent --environment ENV
habyctl provider alias set ALIAS --provider NAME --revision N --expected-provider OLD --expected-revision M --environment ENV
habyctl provider alias set ALIAS --provider NAME --revision N --expected-instance-id UUID --expected-revision M --environment ENV
```

Set requires exactly one expectation: `--expect-absent`, or an exact previous
binding selected by `--expected-provider` or `--expected-instance-id` plus
`--expected-revision`. The UUID form preserves existing unnamed targets. Names
are resolved server-side in the requested Environment; no names are fabricated.
The proposed target is selected by name, with explicit `--provider-instance-id`
as the alternative for an existing unnamed target. Exactly one selector is required.

Wire: CheckProvider carries Environment, name and revision. GetProviderAlias
carries Environment and alias. SetProviderAlias carries Environment, alias,
a ProviderSelector target and a oneof expectation: expected_absent=true or an
expected ProviderSelector. ProviderSelector contains a oneof name/instance_id
and a positive revision. false absence, missing expectation and incomplete
selectors fail InvalidRequest. The CLI rejects combined expectation flags before
calling the API. Replies contain bounded status/preflight enums
and safe exact identities only, never configuration or secret material.

Set invokes the existing alias store. Preflight repeats outside the catalog
transaction on every request, including repeats; no successful check is cached.
The store preserves its accepted repeat rule: Unchanged requires the exact
current binding as expectation and proposal. Replaying an obsolete expectation
returns StaleExpectation even if the desired target was previously reached.
Audit and binding commit together, and one alias per instance remains enforced.
No new storage workflow procedures, triggers, payload formats or migrations.

Current handlers explicitly list exact kind/contract/implementation combinations
and their accepted typed readers. Handler identity is independent of payload
format/version. A handler can accept several contracts and persisted formats;
only its explicitly listed, registered reader singletons can supply decoded inputs.
The generic typed handler accepts codecs for its configuration CLR type; a handler
bridging different typed DTO versions must explicitly handle those known types.
Current declaration writers must still be the exact registered reload codec and
appear in the selected handler's reader list. `IsCompatibilityReader` marks a codec
as historical read/re-encode only: it cannot accept declarations or prepare new
revisions. This permits several format readers for one CLR type while keeping one
unambiguous writer. The persisted kind/contract/format triple remains unique.
Declaration retry and baseline checks also select the exact persisted reader,
never the current writer for historical payload bytes. Missing or unaccepted
readers fail closed. Historical payloads and associations are never rewritten.
Overlaps and inconsistent current registrations fail startup. Missing historical
support permits startup and metadata reads; checks fail closed without fallback.
Preflight is point-in-time evidence, not a guarantee of future provisioning
success. Checks and alias changes do not expand execution admission or rewrite
revisions, Plans, generations, endpoint-transfer consent or exact SecretRefs.
