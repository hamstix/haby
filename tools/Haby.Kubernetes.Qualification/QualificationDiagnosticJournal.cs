using System.Globalization;
using System.Text;

namespace Haby.Kubernetes.Qualification;

internal enum QualificationMilestone
{
    DurableEntry, CatalogInitStart, CatalogInitCompleted, NamespaceCreateStart, NamespaceCreateCompleted,
    DurableSuiteStart, RuntimeAuthStart, InitialDeliveryStart, MixedDeliveryStart,
    MixedPreparationCompleted, MixedDocumentsCompleted, MixedIdentityCompleted, MixedPublicationCompleted, MixedDeliveryCompleted,
    NamespaceCleanupStart, NamespaceAbsenceConfirmed, CatalogDisposeStart, CatalogDisposeCompleted
}

// A private bounded journal survives an outer command timeout. No runtime values enter this channel.
internal sealed class QualificationDiagnosticJournal : IDisposable
{
    private readonly FileStream stream;
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
    }

    internal void Mark(QualificationMilestone milestone)
    {
        if (Failed) return;
        try
        {
            var id = milestone switch
            {
                QualificationMilestone.DurableEntry => "durable-entry",
                QualificationMilestone.CatalogInitStart => "catalog-init-start",
                QualificationMilestone.CatalogInitCompleted => "catalog-init-completed",
                QualificationMilestone.NamespaceCreateStart => "namespace-create-start",
                QualificationMilestone.NamespaceCreateCompleted => "namespace-create-completed",
                QualificationMilestone.DurableSuiteStart => "durable-suite-start",
                QualificationMilestone.RuntimeAuthStart => "runtime-auth-start",
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
                _ => throw new InvalidOperationException("Unknown diagnostic milestone.")
            };
            var bytes = Encoding.ASCII.GetBytes((sequence + 1).ToString(CultureInfo.InvariantCulture) + " " + id + "\n");
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

    public void Dispose()
    {
        try { stream.Dispose(); }
        catch (IOException) { Failed = true; }
    }
}
