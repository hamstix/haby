using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Haby.Kubernetes.Qualification;

internal enum QualificationMilestone
{
    DeliveryProvenanceStart, DeliveryProvenanceCompleted, DeliveryPodStart, DeliveryPodCompleted, DeliveryTokenStart, DeliveryTokenCompleted, DeliveryHostFirstStart, DeliveryHostFirstStarted, DeliveryHostFirstDisposeStart, DeliveryHostFirstDisposeCompleted, DeliveryHostRestartStart, DeliveryHostRestartStarted, DeliveryHostRestartDisposeStart, DeliveryHostRestartDisposeCompleted, DeliveryFirstRequestStart, DeliveryFirstRequestCompleted, DeliveryPodCleanupStart, DeliveryPodCleanupCompleted, DeliveryPodCleanupFailed,
    DeliveryAfterStateStart, DeliveryAfterStateCompleted, DeliveryAfterStateFailed,
    DeliveryResultCheckStart, DeliveryResultCheckCompleted, DeliveryResultCheckFailed,
    DeliveryReviewRefusalStart, DeliveryReviewRefusalCompleted, DeliveryReviewRefusalFailed,
    DeliveryReviewRepublishStart, DeliveryReviewRepublishCompleted, DeliveryReviewRepublishFailed,
    DeliveryAuthorityCallStart, DeliveryAuthorityCallCompleted, DeliveryAuthorityCallFailed,
    DeliveryAuthorityRefusalStart, DeliveryAuthorityRefusalCompleted, DeliveryAuthorityRefusalFailed,
    DeliveryAuthorityRepublishStart, DeliveryAuthorityRepublishCompleted, DeliveryAuthorityRepublishFailed,
    DeliveryStartupStart, DeliveryStartupCompleted, DeliveryStartupFailed,
    DeliveryHostStopStart, DeliveryHostStopCompleted, DeliveryHostStopFailed,
    DeliveryHostDisposeStart, DeliveryHostDisposeCompleted, DeliveryHostDisposeFailed,
    DeliveryCatalogVerifyStart, DeliveryCatalogVerifyCompleted, DeliveryCatalogVerifyFailed,
    StartupPodStart, StartupPodCompleted, StartupPodFailed,
    StartupTokenStart, StartupTokenCompleted, StartupTokenFailed,
    StartupFileStart, StartupFileCompleted, StartupFileFailed,
    StartupFingerprintsStart, StartupFingerprintsCompleted, StartupFingerprintsFailed,
    StartupBeforeStateStart, StartupBeforeStateCompleted, StartupBeforeStateFailed,
    StartupFirstFetchStart, StartupFirstFetchCompleted, StartupFirstFetchFailed,
    StartupAfterStateStart, StartupAfterStateCompleted, StartupAfterStateFailed,
    StartupRestartFetchStart, StartupRestartFetchCompleted, StartupRestartFetchFailed,
    StartupRevokeStart, StartupRevokeCompleted, StartupRevokeFailed,
    StartupDenialProbeStart, StartupDenialProbeCompleted, StartupDenialProbeFailed,
    StartupRepublishStart, StartupRepublishCompleted, StartupRepublishFailed,
    StartupPodCleanupStart, StartupPodCleanupCompleted, StartupPodCleanupFailed,
    StartupDirectoryCleanupStart, StartupDirectoryCleanupCompleted, StartupDirectoryCleanupFailed,
    DurableEntry, CatalogInitStart, CatalogInitCompleted, NamespaceCreateStart, NamespaceCreateCompleted,
    DeliveryHostFirstBuilt, DeliveryHostFirstCredentialReady, DeliveryHostRestartBuilt, DeliveryHostRestartCredentialReady,
    WorkloadActionsStart, WorkloadActionsCompleted, AcceptedSetupStart, AcceptedSetupCompleted, RestoreStart, RestoreCompleted, IdentityActionsStart, IdentityActionsCompleted, RuntimeSetupStart, RuntimeSetupCompleted, MixedBaselinePrepared,
    DurableSuiteStart, RuntimeAuthStart, InitialDeliveryStart, MixedDeliveryStart,
    MixedPreparationCompleted, MixedDocumentsCompleted, MixedIdentityCompleted, MixedPublicationCompleted, MixedDeliveryCompleted,
    NamespaceCleanupStart, NamespaceAbsenceConfirmed, CatalogDisposeStart, CatalogDisposeCompleted, RuntimeAuthCompleted,
    RuntimeFinalRevokeStart, RuntimeFinalRevokeCompleted, RuntimeFinalHistoryStart, RuntimeFinalHistoryCompleted,
    RuntimeFinalAuthorizationStart, RuntimeFinalAuthorizationCompleted, RuntimeCandidateDisposeStart, RuntimeCandidateDisposeCompleted,
    RuntimeCandidateDisposeFailed, InnerCleanupStart, InnerCleanupCompleted, InnerCleanupFailed, NamespaceCleanupFailed, CatalogDisposeFailed
}

// A private bounded journal records closed progress and managed elapsed time, never runtime payloads.
internal sealed class QualificationDiagnosticJournal : IDisposable
{
    private readonly FileStream stream;
    private readonly Stopwatch managedClock;
    private int sequence;
    internal bool Failed { get; private set; }

    internal QualificationDiagnosticJournal(string directory)
    {
        var owned = new DirectoryInfo(Path.GetFullPath(directory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(owned.Parent?.FullName, Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), comparison) ||
            owned.Name.Length != 20 || !owned.Name.StartsWith("haby179-", StringComparison.Ordinal) ||
            owned.Name.AsSpan(8).ContainsAnyExcept("0123456789abcdef") ||
            (owned.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Diagnostic directory ownership refused.");
        var path = Path.Combine(owned.FullName, "diagnostic-progress.txt");
        var file = new FileInfo(path);
        if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || file.Length != 0)
            throw new IOException("Diagnostic journal ownership refused.");
        // The launcher exclusively created the empty file; only this writer can modify it while open.
        stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        if (stream.Length != 0) { stream.Dispose(); throw new IOException("Diagnostic journal is not empty."); }
        // Separate from the parent command clock: compilation and startup have already happened.
        managedClock = Stopwatch.StartNew();
    }

    internal void Mark(QualificationMilestone milestone)
    {
        if (Failed) return;
        try
        {
            var id = milestone switch
            {
                QualificationMilestone.DeliveryProvenanceStart => "delivery-provenance-start",
                QualificationMilestone.DeliveryProvenanceCompleted => "delivery-provenance-completed",
                QualificationMilestone.DeliveryPodStart => "delivery-pod-start",
                QualificationMilestone.DeliveryPodCompleted => "delivery-pod-completed",
                QualificationMilestone.DeliveryTokenStart => "delivery-token-start",
                QualificationMilestone.DeliveryTokenCompleted => "delivery-token-completed",
                QualificationMilestone.DeliveryHostFirstStart => "delivery-host-first-start",
                QualificationMilestone.DeliveryHostFirstBuilt => "delivery-host-first-built",
                QualificationMilestone.DeliveryHostFirstCredentialReady => "delivery-host-first-credential-ready",
                QualificationMilestone.DeliveryHostFirstStarted => "delivery-host-first-started",
                QualificationMilestone.DeliveryHostFirstDisposeStart => "delivery-host-first-dispose-start",
                QualificationMilestone.DeliveryHostFirstDisposeCompleted => "delivery-host-first-dispose-completed",
                QualificationMilestone.DeliveryHostRestartStart => "delivery-host-restart-start",
                QualificationMilestone.DeliveryHostRestartBuilt => "delivery-host-restart-built",
                QualificationMilestone.DeliveryHostRestartCredentialReady => "delivery-host-restart-credential-ready",
                QualificationMilestone.DeliveryHostRestartStarted => "delivery-host-restart-started",
                QualificationMilestone.DeliveryHostRestartDisposeStart => "delivery-host-restart-dispose-start",
                QualificationMilestone.DeliveryHostRestartDisposeCompleted => "delivery-host-restart-dispose-completed",
                QualificationMilestone.DeliveryFirstRequestStart => "delivery-first-request-start",
                QualificationMilestone.DeliveryFirstRequestCompleted => "delivery-first-request-completed",
                QualificationMilestone.DeliveryPodCleanupStart => "delivery-pod-cleanup-start",
                QualificationMilestone.DeliveryPodCleanupCompleted => "delivery-pod-cleanup-completed",
                QualificationMilestone.DeliveryPodCleanupFailed => "delivery-pod-cleanup-failed",
                QualificationMilestone.DurableEntry => "durable-entry",
                QualificationMilestone.CatalogInitStart => "catalog-init-start",
                QualificationMilestone.CatalogInitCompleted => "catalog-init-completed",
                QualificationMilestone.NamespaceCreateStart => "namespace-create-start",
                QualificationMilestone.NamespaceCreateCompleted => "namespace-create-completed",
                QualificationMilestone.DurableSuiteStart => "durable-suite-start",
                QualificationMilestone.WorkloadActionsStart => "workload-actions-start",
                QualificationMilestone.WorkloadActionsCompleted => "workload-actions-completed",
                QualificationMilestone.AcceptedSetupStart => "accepted-setup-start",
                QualificationMilestone.AcceptedSetupCompleted => "accepted-setup-completed",
                QualificationMilestone.RestoreStart => "restore-start",
                QualificationMilestone.RestoreCompleted => "restore-completed",
                QualificationMilestone.IdentityActionsStart => "identity-actions-start",
                QualificationMilestone.IdentityActionsCompleted => "identity-actions-completed",
                QualificationMilestone.RuntimeSetupStart => "runtime-setup-start",
                QualificationMilestone.RuntimeSetupCompleted => "runtime-setup-completed",
                QualificationMilestone.MixedBaselinePrepared => "mixed-baseline-prepared",
                QualificationMilestone.RuntimeAuthStart => "runtime-auth-start",
                QualificationMilestone.RuntimeAuthCompleted => "runtime-auth-completed",
                QualificationMilestone.RuntimeFinalRevokeStart => "runtime-final-revoke-start",
                QualificationMilestone.RuntimeFinalRevokeCompleted => "runtime-final-revoke-completed",
                QualificationMilestone.RuntimeFinalHistoryStart => "runtime-final-history-start",
                QualificationMilestone.RuntimeFinalHistoryCompleted => "runtime-final-history-completed",
                QualificationMilestone.RuntimeFinalAuthorizationStart => "runtime-final-authorization-start",
                QualificationMilestone.RuntimeFinalAuthorizationCompleted => "runtime-final-authorization-completed",
                QualificationMilestone.RuntimeCandidateDisposeStart => "runtime-candidate-dispose-start",
                QualificationMilestone.RuntimeCandidateDisposeCompleted => "runtime-candidate-dispose-completed",
                QualificationMilestone.RuntimeCandidateDisposeFailed => "runtime-candidate-dispose-failed",
                QualificationMilestone.InnerCleanupStart => "inner-cleanup-start",
                QualificationMilestone.InnerCleanupCompleted => "inner-cleanup-completed",
                QualificationMilestone.InnerCleanupFailed => "inner-cleanup-failed",
                QualificationMilestone.NamespaceCleanupFailed => "namespace-cleanup-failed",
                QualificationMilestone.CatalogDisposeFailed => "catalog-dispose-failed",
                QualificationMilestone.InitialDeliveryStart => "initial-delivery-start",
                QualificationMilestone.MixedDeliveryStart => "mixed-delivery-start",
                QualificationMilestone.MixedPreparationCompleted => "mixed-preparation-completed",
                QualificationMilestone.MixedDocumentsCompleted => "mixed-documents-completed",
                QualificationMilestone.MixedIdentityCompleted => "mixed-identity-completed",
                QualificationMilestone.MixedPublicationCompleted => "mixed-publication-completed",
                QualificationMilestone.MixedDeliveryCompleted => "mixed-delivery-completed",
                QualificationMilestone.NamespaceCleanupStart => "namespace-cleanup-start",
                QualificationMilestone.NamespaceAbsenceConfirmed => "namespace-absence-confirmed",
                QualificationMilestone.CatalogDisposeStart => "catalog-dispose-start",
                QualificationMilestone.CatalogDisposeCompleted => "catalog-dispose-completed",
                QualificationMilestone.DeliveryAfterStateStart => "delivery-after-state-start",
                QualificationMilestone.DeliveryAfterStateCompleted => "delivery-after-state-completed",
                QualificationMilestone.DeliveryAfterStateFailed => "delivery-after-state-failed",
                QualificationMilestone.DeliveryResultCheckStart => "delivery-result-check-start",
                QualificationMilestone.DeliveryResultCheckCompleted => "delivery-result-check-completed",
                QualificationMilestone.DeliveryResultCheckFailed => "delivery-result-check-failed",
                QualificationMilestone.DeliveryReviewRefusalStart => "delivery-review-refusal-start",
                QualificationMilestone.DeliveryReviewRefusalCompleted => "delivery-review-refusal-completed",
                QualificationMilestone.DeliveryReviewRefusalFailed => "delivery-review-refusal-failed",
                QualificationMilestone.DeliveryReviewRepublishStart => "delivery-review-republish-start",
                QualificationMilestone.DeliveryReviewRepublishCompleted => "delivery-review-republish-completed",
                QualificationMilestone.DeliveryReviewRepublishFailed => "delivery-review-republish-failed",
                QualificationMilestone.DeliveryAuthorityCallStart => "delivery-authority-call-start",
                QualificationMilestone.DeliveryAuthorityCallCompleted => "delivery-authority-call-completed",
                QualificationMilestone.DeliveryAuthorityCallFailed => "delivery-authority-call-failed",
                QualificationMilestone.DeliveryAuthorityRefusalStart => "delivery-authority-refusal-start",
                QualificationMilestone.DeliveryAuthorityRefusalCompleted => "delivery-authority-refusal-completed",
                QualificationMilestone.DeliveryAuthorityRefusalFailed => "delivery-authority-refusal-failed",
                QualificationMilestone.DeliveryAuthorityRepublishStart => "delivery-authority-republish-start",
                QualificationMilestone.DeliveryAuthorityRepublishCompleted => "delivery-authority-republish-completed",
                QualificationMilestone.DeliveryAuthorityRepublishFailed => "delivery-authority-republish-failed",
                QualificationMilestone.DeliveryStartupStart => "delivery-startup-start",
                QualificationMilestone.DeliveryStartupCompleted => "delivery-startup-completed",
                QualificationMilestone.DeliveryStartupFailed => "delivery-startup-failed",
                QualificationMilestone.DeliveryHostStopStart => "delivery-host-stop-start",
                QualificationMilestone.DeliveryHostStopCompleted => "delivery-host-stop-completed",
                QualificationMilestone.DeliveryHostStopFailed => "delivery-host-stop-failed",
                QualificationMilestone.DeliveryHostDisposeStart => "delivery-host-dispose-start",
                QualificationMilestone.DeliveryHostDisposeCompleted => "delivery-host-dispose-completed",
                QualificationMilestone.DeliveryHostDisposeFailed => "delivery-host-dispose-failed",
                QualificationMilestone.DeliveryCatalogVerifyStart => "delivery-catalog-verify-start",
                QualificationMilestone.DeliveryCatalogVerifyCompleted => "delivery-catalog-verify-completed",
                QualificationMilestone.DeliveryCatalogVerifyFailed => "delivery-catalog-verify-failed",
                QualificationMilestone.StartupPodStart => "startup-pod-start",
                QualificationMilestone.StartupPodCompleted => "startup-pod-completed",
                QualificationMilestone.StartupPodFailed => "startup-pod-failed",
                QualificationMilestone.StartupTokenStart => "startup-token-start",
                QualificationMilestone.StartupTokenCompleted => "startup-token-completed",
                QualificationMilestone.StartupTokenFailed => "startup-token-failed",
                QualificationMilestone.StartupFileStart => "startup-file-start",
                QualificationMilestone.StartupFileCompleted => "startup-file-completed",
                QualificationMilestone.StartupFileFailed => "startup-file-failed",
                QualificationMilestone.StartupFingerprintsStart => "startup-fingerprints-start",
                QualificationMilestone.StartupFingerprintsCompleted => "startup-fingerprints-completed",
                QualificationMilestone.StartupFingerprintsFailed => "startup-fingerprints-failed",
                QualificationMilestone.StartupBeforeStateStart => "startup-before-state-start",
                QualificationMilestone.StartupBeforeStateCompleted => "startup-before-state-completed",
                QualificationMilestone.StartupBeforeStateFailed => "startup-before-state-failed",
                QualificationMilestone.StartupFirstFetchStart => "startup-first-fetch-start",
                QualificationMilestone.StartupFirstFetchCompleted => "startup-first-fetch-completed",
                QualificationMilestone.StartupFirstFetchFailed => "startup-first-fetch-failed",
                QualificationMilestone.StartupAfterStateStart => "startup-after-state-start",
                QualificationMilestone.StartupAfterStateCompleted => "startup-after-state-completed",
                QualificationMilestone.StartupAfterStateFailed => "startup-after-state-failed",
                QualificationMilestone.StartupRestartFetchStart => "startup-restart-fetch-start",
                QualificationMilestone.StartupRestartFetchCompleted => "startup-restart-fetch-completed",
                QualificationMilestone.StartupRestartFetchFailed => "startup-restart-fetch-failed",
                QualificationMilestone.StartupRevokeStart => "startup-revoke-start",
                QualificationMilestone.StartupRevokeCompleted => "startup-revoke-completed",
                QualificationMilestone.StartupRevokeFailed => "startup-revoke-failed",
                QualificationMilestone.StartupDenialProbeStart => "startup-denial-probe-start",
                QualificationMilestone.StartupDenialProbeCompleted => "startup-denial-probe-completed",
                QualificationMilestone.StartupDenialProbeFailed => "startup-denial-probe-failed",
                QualificationMilestone.StartupRepublishStart => "startup-republish-start",
                QualificationMilestone.StartupRepublishCompleted => "startup-republish-completed",
                QualificationMilestone.StartupRepublishFailed => "startup-republish-failed",
                QualificationMilestone.StartupPodCleanupStart => "startup-pod-cleanup-start",
                QualificationMilestone.StartupPodCleanupCompleted => "startup-pod-cleanup-completed",
                QualificationMilestone.StartupPodCleanupFailed => "startup-pod-cleanup-failed",
                QualificationMilestone.StartupDirectoryCleanupStart => "startup-directory-cleanup-start",
                QualificationMilestone.StartupDirectoryCleanupCompleted => "startup-directory-cleanup-completed",
                QualificationMilestone.StartupDirectoryCleanupFailed => "startup-directory-cleanup-failed",
                _ => throw new InvalidOperationException("Unknown diagnostic milestone.")
            };
            var elapsed = Math.Clamp(managedClock.ElapsedMilliseconds, 0L, 3600000L);
            var bytes = Encoding.ASCII.GetBytes((sequence + 1).ToString(CultureInfo.InvariantCulture) + " " + id + " " +
                elapsed.ToString(CultureInfo.InvariantCulture) + "\n");
            if (sequence >= 256 || stream.Position + bytes.Length > 16384) throw new IOException("Diagnostic journal limit exceeded.");
            stream.Write(bytes);
            stream.Flush();
            sequence++;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Failed = true;
        }
    }

    // Caller scopes only: no server callback captures a span or writes a terminal later.
    internal Progress Begin(QualificationMilestone start, QualificationMilestone completed, QualificationMilestone failed)
    {
        Mark(start);
        return new Progress(this, completed, failed);
    }

    internal sealed class Progress(QualificationDiagnosticJournal journal, QualificationMilestone completed, QualificationMilestone failed) : IDisposable
    {
        private bool terminal;

        internal void Complete()
        {
            if (terminal) return;
            terminal = true;
            journal.Mark(completed);
        }

        public void Dispose()
        {
            if (!terminal) journal.Mark(failed);
            terminal = true;
        }
    }

    public void Dispose()
    {
        try { stream.Dispose(); }
        catch (IOException) { Failed = true; }
    }
}
