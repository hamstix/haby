// Fixed, bounded diagnostics only: never format exceptions, payloads or identities.
internal sealed class ConfiguredCorrectiveDiagnostics : IDisposable
{
    internal enum Phase { Setup, Preflight, InitialPlanCommit, ExecuteResource, BadSubmitWait, CorrectivePlanCommit, SubmitWait, Retirement, OidProof }
    internal enum Result { Preflight, InitialPlan, InitialCommit, ResourceProof, BadSubmit, BadWait, CorrectivePlan, CorrectiveCommit, CorrectiveSubmit, RetirementProof, OriginalEffects, CopiedProof }
    private Phase phase;
    private bool complete;
    internal void Enter(Phase value) { phase = value; complete = false; }
    internal void Complete() { Console.WriteLine($"Q243 configured corrective {phase} Complete"); complete = true; }
    internal void Reject(Result value) => Console.WriteLine($"Q243 configured corrective {phase} Rejected {value}");
    public void Dispose() { if (!complete) Console.WriteLine($"Q243 configured corrective {phase} Failed"); }
}
