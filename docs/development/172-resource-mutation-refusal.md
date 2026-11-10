# Unsupported Resource mutation refusal

Issue #172 audits the bounded ADR-045 update safety checkpoint. This is not a
migration, rotation, rebinding or adoption capability. The integrated P2 update
rehearsal remains separate.

## Evidence matrix

The initial audit found broad status assertions, conditional import acceptance,
and missing original-grant/sentinel observations in refusal tests. These gaps are
closed by strengthening existing scenarios and adding the exact Provider-switch
scenario. No production bypass was confirmed and no production behavior changed.
Test names below identify actual methods. Execution counts and source/CI identity
belong in the PR evidence, not in a claim that this is a manual P2 rehearsal.

| Boundary | Production guard | Exact test and outcome | Evidence disposition |
| --- | --- | --- | --- |
| Same-key database name or login schema | `PostgreSqlDatabaseCreateAdmission.CanReuse`, `PostgreSqlPairResourceAdmission.CanReuse` / `SameResource`; persisted `PostgreSqlDurableDeploymentPlanner.ReuseGraphAsync` | `Resource_reuse_unsupported_declaration_changes_refuse_without_effects` (`database`, `schema`) -> import accepted, planning `UnsupportedShape` | Exact status; original delivery/data and accepted-state comparison across repeat/restart. |
| Login databaseRef / ownership transfer | Pair `TryPair`, `HasSingleOwner`, exact peer equality | Active test (`database-ref`) and `Retained_pair_invalid_closures_and_foreign_claims_refuse_before_acceptance` (`swap`) -> import accepted, `UnsupportedShape` | Added active semantic case; strengthened retained case. |
| Privilege policy / invalid references | `PostgreSqlApplicationLoginContract.Definition` const, dependency metadata and `ParseJson` | `Invalid_properties_are_refused_by_metadata_or_adapter` -> `ManifestCandidateResult.InvalidManifest`; retained-pair `policy`, `half-db`, `dangling` -> `ImportReply.InvalidManifest` | These public shapes intentionally cannot reach planning. The test also directly checks typed property parsing returns null for invalid policy/schema/fields; production import remains strict. |
| Resource contract / deletion intent | Registered exact admission key; adapter `SameResource` and retention policy | `Retained_incompatible_reintroductions_and_name_claims_refuse_without_acceptance` (`type`, `delete`), retained-pair `delete`, active `delete` -> import accepted, `UnsupportedShape` | Known registered contract substitution uses valid properties; unknown properties and dangling references separately assert import `InvalidManifest`. |
| Provider instance / exact revision | `ReuseGraphAsync` compares baseline and every original source | `Provider_switch_stales_saved_plan_and_refuses_new_reuse_without_changing_original_delivery` -> new planning `UnsupportedShape`; retained-pair `provider-revision` same result | New real authenticated Host/generated alias administration, including byte-identical revision and different instance. Alias operation performs real preflight. |
| Configuration contract / ImplementationId | Exact adapter key and codec; pair `TryPair`; immutable Provider revisions | `Unsupported_pair_relationships_and_exact_configuration_fail_closed` (`configuration`, `implementation`) -> typed admission `UnsupportedShape` | Strengthened exact adapter oracle. Production PostgreSQL exposes one registered configuration/implementation; unsupported registration is not a valid alternative rebinding. `Second_typed_adapter_persists_reloads_and_missing_handler_keeps_metadata_available` proves administration `UnsupportedConfiguration` for absent support. No fake successful preflight or catalog rewrite creates a public pathway. |
| Saved Plan alias changes | `PostgreSqlDurableDeploymentPlanner.CheckPlanAsync` exact alias snapshot; atomic committer rechecks | Provider-switch test -> Check `PlanStale`, Commit `PlanStale`, unchanged saved Plan; precondition test (`alias-stale`) same statuses | Repeated after Host restart; accepted generation, old document and credential stay pinned. Creating a Provider revision alone does not mutate them. |
| Generated owner/purpose/SecretRef | `ResourceContinuity.ValidateOutputsAsync`, `PostgreSqlGeneratedCredentialSlots.ReadAsync`, adapter `ValidateReuseAsync` | `Resource_reuse_missing_corrupt_and_foreign_original_evidence_never_advances` -> planning `PlanInputUnavailable`, execution `InputUnavailable`; `Retained_pair_missing_or_foreign_original_evidence_refuses_without_repair` (`purpose`, `foreign-password`, `slot`) same outcomes | Reuse adequate exact evidence cases. No manifest password or rotation endpoint exists. Fault injection tests corrupt disposable evidence, not supported update APIs. |
| Generated profile/slot and rotation | Generated-slot API validation, immutable owner/version relation, `ck_generated_slot_shape` | `Distinct_owner_dimensions_are_isolated_and_invalid_owners_profiles_slots_leave_no_partial_rows` -> `ProfileConflict` / `InvalidOwner`, exact original reference/material; `Inconsistent_saved_winner_never_regenerates_or_discloses_material` -> `MaterialUnavailable` | Reuse existing persisted tests. Alternative profile/slot cannot be stored through the admitted contract; generated versions cannot be rotated through administrative Secret APIs. No secret bytes/digests enter public diagnostics. |
| Missing original source/attempt/output/support | `ResourceContinuity.EvidenceAsync`, `ValidateOutputsAsync`, registered creation graph validation | Above evidence tests; `Retained_pair_restart_after_commit_requires_both_policies_and_exact_original_support` -> execution `InputUnavailable` | Reuse these exact typed assertions. |
| Half/last/all pair removal, rename/name claim, multiple additions, login on managed database | PostgreSQL pair closure, `CanRetainPair`, `CanAddPair`, standalone policy | Active declaration test (`resource-remove`, `resource-rename`, `resource-add`, `login-add`, `two-pairs-add`, `multiple-additions`) and import-valid retained-pair cases -> `UnsupportedShape` | Strengthened existing tests. Half-database removal with dangling login is instead an explicit import refusal. |
| Compatible retain/return and independent additions | Adapter policies, immutable membership and direct source resolution | `Independent_pair_native_runtime_direct_sources_configuration_only_restart_restore_and_old_exact_grants`, `Retained_absent_noop_and_real_restore_keep_membership_and_allow_direct_original_reintroduction`, `Independent_pairs_coexist_with_standalone_addition_retention_and_reintroduction` | Reuse positive Plan/Commit/Execute/materialize/delivery, original sentinels, old grants and real dump/restore evidence. |
| Acceptance atomicity | Deployment/alias locks and continuity revalidation in desired-state committer | `Resource_reuse_competing_commits_wait_on_real_database_lock_then_have_one_atomic_winner` -> one `Committed`, one `ChangeSetStale`; `Resource_reuse_cancellation_before_acceptance_rolls_back_and_after_confirmed_commit_keeps_receipt` | Reuse lock-observed tests and retained membership variants; no synchronization change. |
| Neutral dispatch | `DurableActionAdmission`, shared continuity and output ports | `Resource_reuse_synthetic_typed_family_uses_common_provenance_without_database_or_login_contracts` | Reuse typed synthetic family; PostgreSQL policy stays in its adapter. |

Production anchors: [planner](../../src/Haby.Persistence.PostgreSql/PostgreSqlDurableDeploymentPlanner.cs),
[atomic Commit](../../src/Haby.Persistence.PostgreSql/PostgreSqlDurableDesiredStateCommitter.cs),
[continuity/evidence](../../src/Haby.Persistence.PostgreSql/ResourceContinuity.cs),
[database policy](../../src/Haby.PostgreSql/PostgreSqlDatabaseCreateAction.cs),
[pair policy/import metadata](../../src/Haby.PostgreSql/PostgreSqlPairPlanning.cs),
[connection evidence](../../src/Haby.PostgreSql/PostgreSqlApplicationConnection.cs),
[credential slots](../../src/Haby.Persistence.PostgreSql/PostgreSqlGeneratedCredentialSlots.cs).

Test anchors: [active refusal/evidence](../../tests/Haby.PostgreSql.IntegrationTests/GenerationResourceReuseSafetyTests.cs),
[Provider switch and preservation oracle](../../tests/Haby.PostgreSql.IntegrationTests/ResourceMutationRefusalTests.cs),
[retained pairs](../../tests/Haby.PostgreSql.IntegrationTests/RetainedPairSafetyTests.cs),
[retained standalone](../../tests/Haby.PostgreSql.IntegrationTests/RetainedResourceSafetyTests.cs),
[typed admission/import](../../tests/Haby.PostgreSql.IntegrationTests/PostgreSqlPairPlanningTests.cs),
[generated credentials](../../tests/Haby.PostgreSql.IntegrationTests/GeneratedCredentialSlotTests.cs),
[positive controls](../../tests/Haby.PostgreSql.IntegrationTests/IndependentPairGenerationTests.cs),
[neutral dispatch](../../tests/Haby.PostgreSql.IntegrationTests/SyntheticResourceReuseTests.cs).

After acceptance, [DurableGenerationRunner](../../src/Haby.Application/DurableGenerationRunner.cs)
and the exact executor validate reuse before dispatch. The
[journal](../../src/Haby.Persistence.PostgreSql/PostgreSqlDurableOperationJournal.cs)
rechecks original outputs/material and aggregate completion under the existing
locks. Missing evidence returns `InputUnavailable`, without a replacement effect.
[Document materialization](../../src/Haby.Persistence.PostgreSql/PostgreSqlConfigurationDocumentStore.cs)
resolves the direct original source and validates confirmed output associations;
delivery uses the saved exact artifact and its independently authorized grant.
`Authorized_delivery_production_owner_purpose_material_and_formatter_failures_publish_no_document_or_new_state`
in [delivery tests](../../tests/Haby.PostgreSql.IntegrationTests/ConfigurationDeliveryTests.cs)
retains the owner/purpose and safe no-partial-delivery checks. None of these consumers
can re-resolve a current alias to rotate an accepted credential.

Import and ChangeSet records are permitted audit/intent persistence. They are not
accepted generations or pending executable work. A semantic refusal must preserve
Desired/Applied, commits, management/source identities, operations/attempts/outputs,
slots/versions, reservations and original documents/grants. An external sentinel
must be read before any later write; reseeding cannot prove preservation.

## Operator response

Keep the accepted deployment unchanged. An invalid declaration must be corrected
before import. An import-valid unsupported Resource change requires a supported
declaration retaining its accepted Resource identity and exact Provider binding.
Do not recreate Resources, reset credentials or infer ownership from matching names.

`planStale` means a saved calculation no longer matches its exact inputs. Review
the changed inputs before explicitly planning again; a new Plan cannot authorize
unsupported Provider rebinding. `changeSetStale` requires new intent with the actual
expected generation. Missing original support/evidence is not proof of external
absence and does not authorize regeneration. Inspect the existing generation and
restore the required supported registration/evidence through established recovery
procedures; never fabricate success or rewrite provenance.

No schema/codec change or fresh-catalog requirement is introduced by this audit.
Earlier #158 management and #168 alpha producer boundaries remain in force.
Catalog reset never removes external Resources or grants permission to adopt them.

The strengthened refusal tests retain the original delivered bytes privately and
compare them without printing or hashing sensitive content. They reconnect using
that original credential, SELECT the original sentinel, and compare independent
catalog snapshots and external OID/owner/ACL observations. Retained-pair cases seed
and issue B's grant while generation 2 is active, then keep that exact grant across
retention, refused return and Host restart. They never use a newly issued grant as
the preservation oracle. Existing positive native/restore scenarios remain intact;
no changed persisted behavior requires another dump/restore implementation.
