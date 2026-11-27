# Bounded Provider connection status

Authenticated operator administrators can observe the current selections:

```text
habyctl provider status --environment staging
habyctl provider status primary --environment staging -o json
```

Both commands use generated `GetProviderConnectionStatus` Host RPC. Names resolve
on the Host; the CLI performs no external I/O or secret resolution. Existing CLI
context and output-envelope conventions apply. There is no new revision selector.

The server captures one bounded SQL snapshot of Provider identities and their
current alias-bound revisions. Each result retains that exact identity, name,
alias, revision and observation time even if an alias changes during the probe.
Unbound Providers return `notSelected`; no highest-revision fallback occurs.
An empty Environment returns an empty observed list; an unknown selected name
returns `notFound`. All results remain visible when one Provider fails. Text and
JSON return nonzero if any observation is unsuccessful or the request fails.

Status proves only connectivity, configured TLS and SYSTEM authentication at
that time. It does not prove management privilege, Resource/database/schema/dbNum
health, CREATE or UPDATE capability, application delivery rights or workload
readiness. It neither changes aliases nor creates preflight receipts, histories
or cached authorization. Existing stronger `provider check` and alias-switch
preflight are unchanged.

## Supported probes and limits

- PostgreSQL opens and disposes a nonpooled connection to the configured
  maintenance database. It issues no SQL management check. A LOGIN without
  CREATEDB can be connected while management preflight refuses it. Configured
  `system-access-credential` uses exact Environment/Provider/revision/slot
  `SystemAccess` authority; refusal or revocation never falls back to another
  credential. Without that explicit shared slot, the ordinary exact
  Environment-owned ProviderAdministrator credential remains the SYSTEM login.
- Kubernetes uses the existing client factory and exact TLS policy to issue a
  disposable `SelfSubjectReview`, which creates no persisted Kubernetes object.
  A nonanonymous authenticated identity is required; 401 is authentication
  failure and 403 means authentication could not be established by this probe.
  It never probes a namespace or management capability.

Transport/TLS, authentication, unavailable credential, configuration, unsupported
probe and deadline outcomes are distinct safe enums. Raw exceptions, endpoint
diagnostics and credentials never enter output. Explicitly disabled certificate
validation is reported as a warning. Status is an observation, not a promise of
current health after the observation time.

`Haby:ProviderStatus:MaximumProviders` defaults to 128 (1–1024),
`ProbeTimeout` to `00:00:10` (positive, at most 30 seconds), and `RequestTimeout`
to `00:00:30` (positive, at most two minutes). Requests use at most one concurrent
probe each; four requests may probe concurrently. Excess requests return `capacityUnavailable` without
an application queue. Enumeration beyond the cap refuses before any probe.
Per-probe deadlines retain other results; an exhausted request deadline marks
remaining selected entries `deadlineExceeded`, never healthy. Caller cancellation
propagates. Catalog failures before a snapshot produce no partial inventory.

## Downstream registration

Trusted additive Host composition registers a singleton codec through
`IProviderConfigurationCodec` and a separate `IProviderConnectionProbe`, normally
by deriving `ProviderConnectionProbe<TConfiguration>`. `Keys` explicitly names
each supported Provider-kind/configuration-contract/implementation combination;
`Readers` contains the exact registered codec singleton instances. Duplicate
keys, private codecs and inconsistent readers fail Host construction. Missing
probe support for persisted history returns `unsupported` without startup
failure. Configuration readers alone do not imply status support.

`ProbeTypedAsync(ProviderConnectionContext, TConfiguration, CancellationToken)`
receives engine-selected exact Environment, Provider and revision. A downstream
shared credential resolver must construct `SharedCredentialScope` using those
identities, its declared exact slot/reference and `SystemAccess`; caller-supplied
identity and ApplicationDelivery are not substitutes. The probe must cooperate
with cancellation, dispose clients/material and return safe enums. Registration
does not grant external lifecycle or application delivery authority. No upstream
Redis implementation, plugin loader or generalized health service is included.

This additive slice requires no catalog migration or reset. Passing local
focused evidence is distinct from hosted exact-candidate merge qualification and
release Final.
