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
and use the same codec singleton as declaration acceptance and persisted reload.
Overlaps and inconsistent current registrations fail startup. Missing historical
support permits startup and metadata reads; checks fail closed without fallback.
Preflight is point-in-time evidence, not a guarantee of future provisioning
success. Checks and alias changes do not expand execution admission or rewrite
revisions, Plans, generations, endpoint-transfer consent or exact SecretRefs.
