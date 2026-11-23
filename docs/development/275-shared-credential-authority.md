# Shared credential authority (275-C)

275-B is merged in PR #291. This slice implements protected shared-credential
intake and revision-bound authority. Qualification is in progress. 275-D Reference
configuration materialization/delivery and Reference retirement remain pending;
neither issue #275 nor P3 is complete. No production Reference registration is
introduced here.

## Protected named intake

A trusted operator-admin can create an Environment-owned named secret through
CreateSecret with purpose `SharedApplicationCredential`, or through the existing
API-only CLI:

```text
habyctl secret create app-login --environment staging --purpose shared-application-credential
```

Interactive input stays hidden; `--stdin` preserves exact bytes. Material remains
write-only and bounded to 1..65,536 bytes. No value argument is accepted. Retain the
RequestId for uncertain outcomes and retry with identical Environment, name,
purpose and bytes. Changed bytes or purpose conflict. Get/list return identifiers,
purpose and version metadata, never material or a verifier. Creation alone issues
no use bindings and grants no delivery permission.

ProviderAdministrator remains exclusively administrative. Configuration remains
exclusive to static Profile projections. Resource-owned ApplicationLogin remains
the generated/imported credential boundary. Shared credentials cannot enter those
paths by relabeling a reference. Rotation is outside this slice.

## Closed typed Provider selections

The PostgreSQL adapter accepts these optional slots in `spec.secretRefs`:

```json
{
  "administrator-password": { "secretName": "pg-admin", "version": 1 },
  "system-access-credential": { "secretName": "app-login", "version": 1 },
  "application-credential": { "secretName": "app-login", "version": 1 }
}
```

The required administrator slot still selects ProviderAdministrator. The optional
slots require SharedApplicationCredential and respectively issue SystemAccess and
ApplicationDelivery authority. Either optional slot can be omitted independently.
The same exact physical Secret/version may back both uses; no per-Resource or
per-Component material is copied. Other adapters must explicitly register their
own closed typed slot/use contract; caller-supplied purpose/use flags cannot mint
Provider grants.

Apply resolves names and positive version numbers once in the exact Environment.
The typed snapshot, exact SecretRefs, actor/time/scope bindings and revision seal
commit atomically. Failure or cancellation before commit rolls everything back.
Equivalent concurrent/restarted retries return the accepted revision without new
authority; changed selections conflict. The internal compatibility revision-write
API cannot issue shared grants without trusted operator declaration intake.

Existing PostgreSQL administrative create/import/preflight paths keep their
administrator-purpose credentials. This slice supplies the uncached adapter-only
SystemAccess resolver; future adapter checks must use its exact scope. It requires
Environment, Provider instance/revision, slot, use, SecretId and SecretVersionId,
then checks committed issuance and revocation before bounded material resolution.
An arbitrary SecretRef, another revision or the ApplicationDelivery use cannot
substitute for SystemAccess. Cancellation propagates. An operation already
authorized when revocation occurs may finish.

## Inspection, dependency scope and revocation

Protected generated Operator RPC GetSharedCredentialBindings lists safe bindings
for an exact Environment/Provider/revision, including independent uses of the
same Secret/version and issuance/revocation actors and UTC times. This is the
current dependency summary: each entry identifies its exact Provider revision,
slot, use and material identity. No consumer/document dependencies are created in
275-C; 275-D must bind and inspect those separately through its accepted pipeline.
No material, material length, hash or verifier is returned.

RevokeSharedCredentialUse selects that full exact scope and uses the authenticated
technical actor. It appends one irreversible revocation. Repeat/restart/concurrent
requests return AlreadyRevoked and preserve the original actor/time. Revoke both
uses by explicitly revoking each scope. The other use and other Provider revisions
remain independent, even when they refer to the same physical version. Revocation
changes no external password, privilege, ownership or stored material.

Bindings and revocations cannot be updated or deleted. A new Provider revision
may issue new explicit grants, but retrying it or the old revision cannot clear an
old revocation. There is no un-revoke operation or positive authorization cache.

## Delivery and catalog boundary

ApplicationDelivery issuance is stored for the next slice; no material resolver,
raw password endpoint or production Reference delivery path is opened for it.
275-D must enforce exact document, current association, frozen projection and
this exact unrevoked use binding on every static-token/runtime request, preserving
whole-response failure. This slice does not implement retirement or rotation.

The additive migration adds generic binding/revocation tables and extends the
existing typed secret constraint. It performs no grant backfill and rewrites no
existing revision, document or material. Existing snapshots without shared slots
retain their current representation. Downgrade refuses while shared secrets
exist. Existing ADR-033 catalog-reset guards continue to apply to superseded
planning histories; this migration never resets persistent data.
