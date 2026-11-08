# ADR-043: Operator access tokens, explicit bootstrap and protected reflection

- Status: Accepted
- Date: 2026-11-08
- Scope: P2 administrative authentication, Host bootstrap and CLI credentials
- Extends: ADR-026/027/028/033/035/042
- Implementation: issue #150; acceptance does not mean implementation is complete

## Context

ADR-042 delivers independently authenticated configuration retrieval on the shared
Host. Its interim operator surface trusts network reachability. Before broader
use, administrative requests need authentication without introducing OAuth, user
management or a general RBAC system. Initial issuance must not depend on an
already authenticated operator RPC or an anonymous bootstrap endpoint.

## Decision

### One administrative permission and mandatory transition

Use static tokens with one installation-wide permission, `operator-admin`.
Protect all administrative RPCs, including reads, validation, provisioning,
recovery and delivery-grant administration, before application work or protected
catalog access. Authentication itself may read the credential verifier store.
Central enforcement must cover newly added operator methods without relying on
individual method authors to remember checks.

The implementation switches the operator surface to mandatory authentication.
There is no anonymous compatibility mode, including loopback or Development.
Host may start without operator tokens, but administrative RPCs then fail closed.
Add authentication storage without resetting the existing catalog. Never generate
an initial token automatically during normal startup or print credentials in logs.
Until issue #150 is implemented, ADR-042's documented interim boundary remains
the actual runtime behavior.

### Explicit local issuance and recovery

First issuance and recovery use an explicit administrative command of the Host,
with the required catalog configuration and local administrative authority. The
command supports a temporary token with explicit expiry. It does not start a
listener, disable authentication or open an unauthenticated RPC. The same explicit
mechanism can issue a replacement after all existing tokens are lost or revoked.
It does not recover the material of an earlier token.

In Kubernetes, authorized operators may invoke it through permitted `exec` or a
separate one-shot Job with the necessary catalog settings. Operations policy
chooses the invocation mechanism. Kubernetes, a Job and stopping the running Host
are not universal architectural prerequisites. Implement and document supported
concurrency and migration behavior; do not assume two processes can run migrations
unsafely. Local invocation is privileged catalog administration, not remote API
authentication or a new operating-system identity service.

`habyctl` remains API-only and never connects directly to the catalog or Providers.
Document a complete first-issuance and emergency-recovery sequence for the actual
Host command, including safe token transfer rather than container log extraction.

### Token lifecycle and audit

Generate at least 256 bits of cryptographically random secret entropy. Use distinct
operator and delivery purposes/formats; client claims cannot choose authority.
Store only a one-way cryptographic verifier, safe identity and lifecycle metadata.
Use constant-time verification. Tokens are reusable and default to no expiry;
allow an optional explicit future UTC expiry without sliding renewal.

Authenticated administration supports create, safe metadata get/list and
idempotent revoke. Grant UUID is the stable audit identity; an optional display
name is not an account or authentication selector. Return raw material only once
through an explicitly selected sensitive output destination. Reuse ADR-042's new
file or explicit token-stdout protections: no overwrite, no implicit disclosure,
and honest guidance if issuance may have committed before output failed. No raw
token recovery, automatic issuance retry, renewal or credential replay receipts.

Rotation is create -> durably transfer/configure -> verify -> revoke. Revoking
the current or last token is permitted; subsequent calls fail and local issuance
provides recovery. Recheck expiry/revocation on every authentication, without a
positive authentication cache. Already-authorized work may finish after revocation.
Infrastructure failures never fall back to anonymous authority.

New authenticated operations use a server-derived stable token identity as actor,
not caller-supplied text or the shared Host technical actor. Local issuance uses a
documented local technical actor. Preserve existing historical audit rows. Do not
introduce a detailed event-history subsystem for every authenticated request.

No token, verifier, Authorization metadata or sensitive response is reproduced in
ordinary output, diagnostics, exception text, traces or logs. Metadata/list/revoke
never return verifier or token material. Current-build restart and backup/restore
preserve authentication state. Restoring an older backup restores that snapshot's
revocation state; it does not guarantee revocations made after the backup.

### Independent authorization purposes and contracts

Retain the shared Host/address and separate generated operator/delivery contracts.
Delivery tokens cannot authorize administrative RPCs, including reflection.
Operator tokens do not authorize resolved configuration retrieval: the runtime
delivery RPC still requires its exact document-scoped delivery grant. Operator
administration can issue such a grant under its existing explicit workflow.
No new permission implicitly bypasses ADR-042's document/owner/purpose checks.

### Selected registry credentials in habyctl

Credentials belong to a named registry instance; contexts selecting that instance
use its credential. Support mutually exclusive `token` and `tokenFile` settings.
Inline storage is plaintext and must be redacted by all ordinary config views.
Resolve a bounded, strictly decoded UTF-8 token file once per invocation using a
documented trailing-newline policy. Never reuse another registry's credential or
silently fall back after invalid selected configuration or token-file input.

Credential setup accepts file/stdin or the existing hidden-input boundary, never
a raw token command-line argument or URL. Preserve fork-configurable paths and
neutral `registry` wording. Config views render the validated redacted model,
never raw YAML or credential-file contents; viewing config does not open token
files. Help, version and local config inspection remain offline. Config mutation
must use an explicit safe write policy and preserve unrelated settings; do not
invent a silent destructive rewrite or weaken existing file handling.

Every operator client channel carries the selected credential, including channels
with larger response budgets. Clients use generated contracts, not reflection.
TLS stays the default on the shared endpoint. Permit remote CLI endpoints through
HTTPS; HTTP is limited to loopback or an explicit isolated-contour opt-in. Never
silently disable certificate validation. Preserve and document any explicitly
selected existing pilot certificate-bypass behavior as an explicit exception.

### Reflection is enabled and administrative

Reflection is enabled by default and requires `operator-admin`, independently of
Development/Production and loopback/network binding. An authorized administrator
can inspect descriptors for both operator and delivery services. This does not
grant permission to call delivery with an operator token.

A configuration setting may disable reflection for contours that do not need it.
Neither habyctl nor the runtime configuration client depends on reflection in
ordinary operation. Cover all registered reflection protocol endpoints with the
same purpose separation; anonymous and delivery-token requests are rejected.

### Architecture and evidence

Common authentication storage and contracts remain Provider-neutral. Business
policy belongs in C#/EF with ADR-026's database constraints; no stored workflow or
mandatory database-role split. Use explicit registration and generated JSON
metadata; gRPC protocol reflection does not authorize CLR reflection discovery.
Do not introduce OAuth, fine-grained RBAC, users, a second Host/listener, runtime
identity integration or compatibility machinery for superseded alpha models.

Required evidence includes complete administrative method coverage, denial before
protected work, wrong-purpose/malformed/duplicate/revoked/expired credentials,
safe failure on storage errors, derived audit actors, first/recovery issuance,
temporary tokens, rotation, selected-instance isolation and redacted config views.
Test reflection separately: anonymous and delivery token fail; operator token
obtains both service descriptions; configured disable removes availability.
Normal generated-client operator and delivery calls work without reflection.

Prove generated client -> real Host -> application -> disposable PostgreSQL and
published Native AOT CLI behavior, restart and real backup/restore. Retain the
existing authorized-delivery path, full solution/PostgreSQL/native validation,
dependency checks and test timing/artifact coverage. No production anonymous test
switch or reduced suite may substitute for authenticated fixture setup.
