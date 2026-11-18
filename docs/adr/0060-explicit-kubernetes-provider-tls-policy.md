# ADR-060: Explicit Kubernetes Provider TLS policy

- Status: Accepted
- Date: 2026-11-18
- Implementation: Not implemented by this documentation; tracked in [issue #261](https://github.com/hamstix/haby/issues/261)
- Release order: After issue #259 and test-cycle separation #263 (ADR-061); no concurrent TLS implementation

## Context

Kubernetes Provider clients currently use system trust or an explicit server CA
bundle with Online revocation. Production connection and TokenReview construction
must follow one explicit policy. The separate qualification NoCheck factory is
not an operator option or a reason to change production defaults.

This decision extends the earlier strict-Online requirement as an accepted
operator capability. Omitted policy retains Online; this is not a fixture-only
exception. It changes neither operator listener nor configuration-delivery TLS.

## Policy contract

The Kubernetes-specific Provider declaration accepts an optional typed `tls`
object. When present, `mode` is required. Unknown fields, unknown enum values,
explicit nulls, malformed values and conflicting combinations refuse before
network access.

| Mode | Server CA bundle | Revocation |
| --- | --- | --- |
| `system` | Forbidden | `online` by default; `offline` or `noCheck` explicit |
| `customCa` | Existing explicit `serverCaPem` bundle required | Same validated choices |
| `insecure` | Forbidden | Field forbidden |

When `tls` is omitted, no CA means `system`/`online`; a CA bundle means
`customCa`/`online`. Equivalent omitted and explicit defaults normalize to the
same semantic policy. Explicit `tls` never silently infers a missing mode.

`noCheck` disables certificate revocation checking only. Chain trust, server
name, validity and server-authentication purpose remain checked. `offline` uses
the platform's cached revocation behavior; cache availability and error behavior
must be documented and tested on supported platforms. It is never a fallback to
`noCheck`, and an unavailable result must not be described as verified clean.

`insecure` explicitly skips server certificate validation. HTTPS and configured
authentication remain required. Redirects and cross-origin credential forwarding
are disabled in every mode. Insecure mode produces a safe visible warning without
credentials, certificate contents or CA material. No connection failure can
select a weaker policy automatically.

## Exact capture and production consumers

Persist the normalized policy and exact custom CA bundle in immutable typed
Provider configuration. Accepted inputs, output and intent consumers, execution,
observation and restart use that exact capture rather than current alias values.
A policy change requires a new Provider revision, ordinary successful preflight
and conditional selection; it never mutates accepted history.

System mode captures the choice of operating-system trust source, not immutable
bytes of the operating-system trust store. Operators remain responsible for that
environmental trust source and platform revocation cache.

The policy applies consistently to Provider checks, alias preflight, execution,
observation, TokenReview and every Kubernetes client factory. Source inventory
must cover both existing production construction paths and all frozen consumers;
changing just preflight is insufficient. Shared persistence remains Provider-neutral
under ADR-035. Adapter payloads use source-generated codecs and preserve Native
AOT. Other Providers, runtime identity, SecretRefs and delivery contracts do not
change.

## Saved-format transition

The maintainer explicitly selected a fresh catalog under ADR-033. The implementation
uses Kubernetes Provider configuration format 2 and does not add a legacy format-1
reader. Omitted-policy equivalence describes new format-2 imports, not permission
to rewrite a saved format-1 payload or its hash.

Reject unsupported saved formats safely. Never reset, migrate or backfill a catalog
automatically and never rewrite accepted inputs, receipts or ownership. A fresh
catalog does not adopt existing Deployments, ServiceAccounts, bindings, permissions
or external Resources. Before replacing an installation, explicitly preserve the
old catalog/history and account for its managed external resources and credentials.
An installation with active work must not be silently stranded. Any supported
adoption or migration mechanism requires its own accepted contract.

## Diagnostics and verification

Emit bounded truthful categories when actual evidence distinguishes trust, name,
expiry, revoked certificate, unavailable revocation, DNS/connect, authentication
and authorization. Otherwise use a generic `tls-validation-failed` category rather
than infer a cause. Do not emit raw exception messages, certificate/material dumps,
credentials or private configuration. Extend typed diagnostic metadata only as
needed to carry these safe facts.

Use a small controlled local TLS endpoint and test PKI to verify valid and invalid
trust/name/expiry/revocation cases, Online/Offline/NoCheck platform behavior and
explicit insecure mode. Test authentication and redirect refusal even when
certificate validation is skipped. Tests must not depend on public CRL/OCSP
availability or add a Kubernetes version, load, image or PostgreSQL matrix.

Test exact persistence/restart, defaults and every production factory path,
frozen inputs/outputs/intents, rejection before network and redacted operator
output. Preserve all current mandatory gates and distinguish actual local TLS
execution from static inspection and hosted evidence. Implementation includes
an operator guide with platform limits and the fresh-catalog boundary.

## Sequencing and limits

Issue #261 follows merged #259, test-cycle separation #263 under ADR-061 and this accepted documentation. It does not open
public UPDATE or complete P3. Afterwards retain the successive ordinary UPDATE
proof (initial to update1 Applied to update2 Applied), ADR-052 corrective work,
integrated rehearsal and public operational UPDATE dependencies. ADR-059 CLI
composition still follows operational UPDATE.
