namespace Haby.Persistence.PostgreSql;

// Value-free diagnostic observation only; not persisted authority or another validator.
internal enum UpdateCompletionStage { Unknown, Begin, Recipients, ClosureSource, CaptureSubmitted, ObsoleteProgress, RetirementMarker, CurrentRuntime, ConfigurationComplete, FreshSamples, RetirementStart, Revoke, Publications, CatalogHash, SamplesHash, AppliedCas, CloseRequest, PrerequisiteIncomplete }
internal enum UpdateCompletionPredicate { Unknown, InternalUPDATECompletionRequiresAnUpdateInventory, UPDATEInventoryRequired, UpdateExpectedAppliedChanged, UpdateCompletionExceedsCheckpointBound, UpdateAppliedCompareAndSwapFailed, UpdateInventoryMissing, UpdateGenerationSuperseded, UpdateAttemptsChanged, ObsoleteInventoryChanged, UnretiredPublicationChanged, RetirementProgressChanged, RetirementStartMismatch, RetirementStartRecipientEvidenceInvalid, ConfigurationAuthorityRequired, RetirementNotStarted, FrozenObsoletePermissionMismatch, ObsoletePublicationHasAnActiveLiveReference, RetirementProgressMismatch, CurrentObsoletePermissionNoLongerMatchesInventory, FrozenCompletionInventoryRequired, CompletionProofUnavailable, CompletionProofCorrupt, CompletionProofOriginMismatch, CompletionReadinessEvidenceChanged, CompletionPrerequisiteEvidenceMismatch, CompletionRequestUnavailable, CompletionRequestOutcomeMismatch, ReadinessSampleExceedsBound, ReadinessSampleCoverageMismatch, FreshCompleteSetReadinessRequired, FreshReadinessSampleOriginMismatch, CompletionCheckpointReferenceMismatch, BindingBoundExceeded, PublicationBoundExceeded, CurrentPublicationPrerequisiteUnavailable, CompletionPrerequisiteTextExceedsBound, UnexpectedActionInventory, CompleteActionEvidenceRequired, ManagedInventoryBoundScopeMismatch, PreparationInventoryBoundExceeded, CompletionPublicationCoverageMismatch, PublicationOriginRequired, RetainedPublicationEvidenceMismatch, CompletionEvidenceExceedsBound, FrozenUpdateRequired, UpdateCompletionCorrupt, UpdateCompletionMissing, UpdateCompletionOriginMismatch, UpdateCompletionPrerequisitesChanged, UpdateCompletionRequestMismatch, CurrentObjectEvidenceExceedsBound, CompleteUpdateCurrentObjectEvidenceRequired, CurrentObjectReceiptMismatch, RetirementStartEvidenceMismatch, RetirementStartOriginMismatch, ExactObsoleteRevocationProofMismatch }
internal sealed record UpdateCompletionDiagnostic(UpdateCompletionStage Stage, UpdateCompletionPredicate Predicate = UpdateCompletionPredicate.Unknown);
internal static class UpdateCompletionDiagnostics
{
    internal static UpdateCompletionPredicate Predicate(InvalidDataException error) => error.Message switch
    {
        "Internal UPDATE completion requires an update inventory." => UpdateCompletionPredicate.InternalUPDATECompletionRequiresAnUpdateInventory,
        "UPDATE inventory required." => UpdateCompletionPredicate.UPDATEInventoryRequired,
        "Update expected Applied changed." => UpdateCompletionPredicate.UpdateExpectedAppliedChanged,
        "Update completion exceeds checkpoint bound." => UpdateCompletionPredicate.UpdateCompletionExceedsCheckpointBound,
        "Update Applied compare-and-swap failed." => UpdateCompletionPredicate.UpdateAppliedCompareAndSwapFailed,
        "Update inventory missing." => UpdateCompletionPredicate.UpdateInventoryMissing,
        "Update generation superseded." => UpdateCompletionPredicate.UpdateGenerationSuperseded,
        "Update attempts changed." => UpdateCompletionPredicate.UpdateAttemptsChanged,
        "Obsolete inventory changed." => UpdateCompletionPredicate.ObsoleteInventoryChanged,
        "Unretired publication changed." => UpdateCompletionPredicate.UnretiredPublicationChanged,
        "Retirement progress changed." => UpdateCompletionPredicate.RetirementProgressChanged,
        "Retirement-start mismatch." => UpdateCompletionPredicate.RetirementStartMismatch,
        "Retirement-start recipient evidence invalid." => UpdateCompletionPredicate.RetirementStartRecipientEvidenceInvalid,
        "Configuration authority required." => UpdateCompletionPredicate.ConfigurationAuthorityRequired,
        "Retirement not started." => UpdateCompletionPredicate.RetirementNotStarted,
        "Frozen obsolete permission mismatch." => UpdateCompletionPredicate.FrozenObsoletePermissionMismatch,
        "Obsolete publication has an active live reference." => UpdateCompletionPredicate.ObsoletePublicationHasAnActiveLiveReference,
        "Retirement progress mismatch." => UpdateCompletionPredicate.RetirementProgressMismatch,
        "Current obsolete permission no longer matches inventory." => UpdateCompletionPredicate.CurrentObsoletePermissionNoLongerMatchesInventory,
        "Frozen completion inventory required." => UpdateCompletionPredicate.FrozenCompletionInventoryRequired,
        "Completion proof unavailable." => UpdateCompletionPredicate.CompletionProofUnavailable,
        "Completion proof corrupt." => UpdateCompletionPredicate.CompletionProofCorrupt,
        "Completion proof origin mismatch." => UpdateCompletionPredicate.CompletionProofOriginMismatch,
        "Completion readiness evidence changed." => UpdateCompletionPredicate.CompletionReadinessEvidenceChanged,
        "Completion prerequisite evidence mismatch." => UpdateCompletionPredicate.CompletionPrerequisiteEvidenceMismatch,
        "Completion request unavailable." => UpdateCompletionPredicate.CompletionRequestUnavailable,
        "Completion request outcome mismatch." => UpdateCompletionPredicate.CompletionRequestOutcomeMismatch,
        "Readiness sample exceeds bound." => UpdateCompletionPredicate.ReadinessSampleExceedsBound,
        "Readiness sample coverage mismatch." => UpdateCompletionPredicate.ReadinessSampleCoverageMismatch,
        "Fresh complete-set readiness required." => UpdateCompletionPredicate.FreshCompleteSetReadinessRequired,
        "Fresh readiness sample origin mismatch." => UpdateCompletionPredicate.FreshReadinessSampleOriginMismatch,
        "Completion checkpoint reference mismatch." => UpdateCompletionPredicate.CompletionCheckpointReferenceMismatch,
        "Binding bound exceeded." => UpdateCompletionPredicate.BindingBoundExceeded,
        "Publication bound exceeded." => UpdateCompletionPredicate.PublicationBoundExceeded,
        "Current publication prerequisite unavailable." => UpdateCompletionPredicate.CurrentPublicationPrerequisiteUnavailable,
        "Completion prerequisite text exceeds bound." => UpdateCompletionPredicate.CompletionPrerequisiteTextExceedsBound,
        "Unexpected action inventory." => UpdateCompletionPredicate.UnexpectedActionInventory,
        "Complete action evidence required." => UpdateCompletionPredicate.CompleteActionEvidenceRequired,
        "Managed inventory bound/scope mismatch." => UpdateCompletionPredicate.ManagedInventoryBoundScopeMismatch,
        "Preparation inventory bound exceeded." => UpdateCompletionPredicate.PreparationInventoryBoundExceeded,
        "Completion publication coverage mismatch." => UpdateCompletionPredicate.CompletionPublicationCoverageMismatch,
        "Publication origin required." => UpdateCompletionPredicate.PublicationOriginRequired,
        "Retained publication evidence mismatch." => UpdateCompletionPredicate.RetainedPublicationEvidenceMismatch,
        "Completion evidence exceeds bound." => UpdateCompletionPredicate.CompletionEvidenceExceedsBound,
        "Frozen update required." => UpdateCompletionPredicate.FrozenUpdateRequired,
        "Update completion corrupt." => UpdateCompletionPredicate.UpdateCompletionCorrupt,
        "Update completion missing." => UpdateCompletionPredicate.UpdateCompletionMissing,
        "Update completion origin mismatch." => UpdateCompletionPredicate.UpdateCompletionOriginMismatch,
        "Update completion prerequisites changed." => UpdateCompletionPredicate.UpdateCompletionPrerequisitesChanged,
        "Update completion request mismatch." => UpdateCompletionPredicate.UpdateCompletionRequestMismatch,
        "Current-object evidence exceeds bound." => UpdateCompletionPredicate.CurrentObjectEvidenceExceedsBound,
        "Complete update current-object evidence required." => UpdateCompletionPredicate.CompleteUpdateCurrentObjectEvidenceRequired,
        "Current-object receipt mismatch." => UpdateCompletionPredicate.CurrentObjectReceiptMismatch,
        "Retirement-start evidence mismatch." => UpdateCompletionPredicate.RetirementStartEvidenceMismatch,
        "Retirement-start origin mismatch." => UpdateCompletionPredicate.RetirementStartOriginMismatch,
        "Exact obsolete revocation proof mismatch." => UpdateCompletionPredicate.ExactObsoleteRevocationProofMismatch,
        _ => UpdateCompletionPredicate.Unknown
    };
    internal static void Emit(Action<UpdateCompletionDiagnostic>? observer, UpdateCompletionStage stage,
        UpdateCompletionPredicate predicate = UpdateCompletionPredicate.Unknown)
    {
        // Diagnostics cannot change acceptance, transaction or cancellation behavior.
        try { observer?.Invoke(new(stage, predicate)); } catch { }
    }
}
