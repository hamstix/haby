using System.Security.Cryptography;
using System.Text;
using Haby.Domain;

namespace Haby.Application;

/// <summary>A test catalog and atomic desired-state boundary. It executes no Provider operation.</summary>
public sealed class InMemoryDesiredStateStore(IManifestRevisionReader revisions, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<EnvironmentRecordId, EnvironmentRecord> _environments = [];
    private readonly Dictionary<(EnvironmentRecordId, ProviderAlias), ProviderResolution> _aliases = [];
    private readonly Dictionary<DeploymentChangeSetId, DeploymentChangeSet> _changeSets = [];
    private readonly Dictionary<DeploymentPlanId, DeploymentPlan> _plans = [];
    private readonly Dictionary<DeploymentChangeSetId, ChangeSetCommit> _commits = [];
    private readonly Dictionary<(EnvironmentRecordId, ApplicationRecordId), ApplicationDeployment> _deployments = [];
    private readonly List<ApplicationDeploymentGeneration> _generations = [];

    public IReadOnlyList<ApplicationDeployment> Deployments
    {
        get { lock (_gate) return Array.AsReadOnly(_deployments.Values.ToArray()); }
    }
    public IReadOnlyList<ApplicationDeploymentGeneration> Generations
    {
        get { lock (_gate) return Array.AsReadOnly(_generations.ToArray()); }
    }
    public IReadOnlyList<DeploymentChangeSet> ChangeSets
    {
        get { lock (_gate) return Array.AsReadOnly(_changeSets.Values.ToArray()); }
    }
    public IReadOnlyList<DeploymentPlan> Plans
    {
        get { lock (_gate) return Array.AsReadOnly(_plans.Values.ToArray()); }
    }

    public EnvironmentRecord AddEnvironment(EnvironmentId id, string displayName)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(displayName);
        lock (_gate)
        {
            if (_environments.Values.Any(item => item.Id == id)) throw new ArgumentException("Environment key already exists.", nameof(id));
            var record = new EnvironmentRecord(EnvironmentRecordId.New(), id, displayName, 1);
            _environments.Add(record.RecordId, record);
            return record;
        }
    }

    /// <summary>Changes only the fake Environment catalog; accepted snapshots stay fixed.</summary>
    public void SetProviderAlias(EnvironmentRecordId environment, ProviderAlias alias, ProviderResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(resolution);
        if (string.IsNullOrWhiteSpace(resolution.ProviderInstanceId) || resolution.ConfigurationRevision <= 0 ||
            string.IsNullOrWhiteSpace(resolution.ImplementationId))
            throw new ArgumentException("An exact Provider selection is required.", nameof(resolution));
        lock (_gate)
        {
            if (!_environments.ContainsKey(environment)) throw new ArgumentException("Environment not found.", nameof(environment));
            _aliases[(environment, alias)] = resolution;
        }
    }

    public CreateChangeSetResult CreateChangeSet(EnvironmentRecordId environment, ApplicationRecordId application,
        ManifestRevisionRef revision, DeploymentPrecondition precondition, string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(precondition);
        CheckActor(actorId);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_environments.ContainsKey(environment)) return new CreateChangeSetResult.EnvironmentNotFound();
            var found = revisions.Find(revision);
            if (found is null) return new CreateChangeSetResult.RevisionNotFound();
            if (revision.ApplicationRecordId != application) return new CreateChangeSetResult.RevisionNotOwnedByApplication();
            var member = new DeploymentChangeSetMember(application, revision, precondition);
            var changeSet = new DeploymentChangeSet(DeploymentChangeSetId.New(), environment, member,
                clock.GetUtcNow(), actorId);
            _changeSets.Add(changeSet.Id, changeSet);
            return new CreateChangeSetResult.Created(changeSet);
        }
    }

    public PlanChangeSetResult Plan(DeploymentChangeSetId changeSetId, string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSetId);
        CheckActor(actorId);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_changeSets.TryGetValue(changeSetId, out var changeSet)) return new PlanChangeSetResult.ChangeSetNotFound();
            if (_commits.TryGetValue(changeSetId, out var commit)) return new PlanChangeSetResult.AlreadyCommitted(commit);
            var actual = Current(changeSet);
            if (!Matches(changeSet.Member.Precondition, actual))
                return new PlanChangeSetResult.ChangeSetStale(changeSet.Member, actual?.DesiredGeneration);
            var inputs = Resolve(changeSet);
            if (inputs is PlanChangeSetResult blocked) return blocked;
            var exact = (DeploymentPlanningInputs)inputs;
            var plan = new DeploymentPlan(DeploymentPlanId.New(), changeSetId, changeSet.EnvironmentRecordId,
                changeSet.Member, Array.AsReadOnly([changeSet.Member.ApplicationRecordId]), exact,
                Fingerprint(exact), clock.GetUtcNow(), actorId);
            _plans.Add(plan.Id, plan);
            return new PlanChangeSetResult.Planned(plan);
        }
    }

    public CommitDesiredStateResult Commit(DeploymentChangeSetId changeSetId, DeploymentPlanId planId,
        string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSetId);
        ArgumentNullException.ThrowIfNull(planId);
        CheckActor(actorId);
        lock (_gate)
        {
            // An acknowledged prior commit wins over a different or stale Plan, including after cancellation.
            if (_commits.TryGetValue(changeSetId, out var previous)) return new CommitDesiredStateResult.Committed(previous);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_changeSets.TryGetValue(changeSetId, out var changeSet)) return new CommitDesiredStateResult.ChangeSetNotFound();
            if (!_plans.TryGetValue(planId, out var plan)) return new CommitDesiredStateResult.PlanNotFound();
            if (plan.ChangeSetId != changeSetId) return new CommitDesiredStateResult.PlanDoesNotBelongToChangeSet();
            var actual = Current(changeSet);
            if (!Matches(changeSet.Member.Precondition, actual))
                return new CommitDesiredStateResult.ChangeSetStale(changeSet.Member, actual?.DesiredGeneration);
            var currentInputs = Resolve(changeSet);
            if (currentInputs is not DeploymentPlanningInputs exact ||
                Fingerprint(exact) != plan.PlanningFingerprint || !exact.Equals(plan.Inputs))
                return new CommitDesiredStateResult.PlanStale();
            cancellationToken.ThrowIfCancellationRequested();
            var prior = actual is null ? null : _generations.Single(item =>
                item.DeploymentId == actual.Id && item.Generation == actual.DesiredGeneration);
            var noOp = prior is not null && prior.ManifestRevisionRef == plan.Member.ManifestRevisionRef &&
                prior.Inputs.Equals(plan.Inputs);
            var generation = noOp ? actual!.DesiredGeneration :
                DeploymentGeneration.Create(actual is null ? 1 : checked(actual.DesiredGeneration.Value + 1));
            var acceptedAt = clock.GetUtcNow();
            if (!noOp)
            {
                var deployment = actual is null
                    ? new ApplicationDeployment(ApplicationDeploymentId.New(), changeSet.EnvironmentRecordId,
                        changeSet.Member.ApplicationRecordId, generation, null, 1)
                    : actual with { DesiredGeneration = generation, ConcurrencyToken = checked(actual.ConcurrencyToken + 1) };
                var snapshot = new ApplicationDeploymentGeneration(deployment.Id, generation, DesiredPresence.Present,
                    plan.Member.ManifestRevisionRef, plan.Inputs, plan.Id, changeSetId, acceptedAt, actorId);
                _generations.Add(snapshot);
                _deployments[(deployment.EnvironmentRecordId, deployment.ApplicationRecordId)] = deployment;
            }
            var committed = new ChangeSetCommit(changeSetId, planId, generation, acceptedAt, actorId, noOp);
            _commits.Add(changeSetId, committed);
            return new CommitDesiredStateResult.Committed(committed);
        }
    }

    private ApplicationDeployment? Current(DeploymentChangeSet changeSet) =>
        _deployments.GetValueOrDefault((changeSet.EnvironmentRecordId, changeSet.Member.ApplicationRecordId));

    private static bool Matches(DeploymentPrecondition precondition, ApplicationDeployment? actual) =>
        precondition switch
        {
            DeploymentPrecondition.ExpectedAbsence => actual is null,
            DeploymentPrecondition.ExpectedGeneration expected => actual?.DesiredGeneration == expected.Generation,
            _ => false
        };

    private object Resolve(DeploymentChangeSet changeSet)
    {
        var revision = revisions.Find(changeSet.Member.ManifestRevisionRef);
        if (revision is null || revision.Reference.ApplicationRecordId != changeSet.Member.ApplicationRecordId)
            return new PlanChangeSetResult.UnsupportedDependency();
        var declaration = revision.Declaration;
        // These inputs need the ADR-018 export planner or ADR-019 parameter resolver.
        if (declaration.Spec.Parameters.Count != 0 || declaration.Spec.Components.Values.Any(component =>
                component.Bindings.Values.Any(binding => binding.Target is NormalizedApplicationExportTarget)))
            return new PlanChangeSetResult.UnsupportedDependency();
        var aliases = declaration.Spec.Resources.Values.Select(item => item.ProviderRef)
            .Concat(declaration.Spec.Components.Values.SelectMany(component => component.Workloads.Values.Select(item => item.ProviderRef)))
            .Distinct().OrderBy(item => item.Value, StringComparer.Ordinal);
        var resolved = new List<ResolvedProviderAlias>();
        foreach (var alias in aliases)
        {
            if (!_aliases.TryGetValue((changeSet.EnvironmentRecordId, alias), out var provider))
                return new PlanChangeSetResult.ProviderAliasNotResolved(alias);
            resolved.Add(new ResolvedProviderAlias(alias, provider));
        }
        var environment = _environments[changeSet.EnvironmentRecordId];
        return new DeploymentPlanningInputs(environment.RecordId, environment.Id, revision.Reference, resolved);
    }

    private static string Fingerprint(DeploymentPlanningInputs inputs)
    {
        var text = new StringBuilder("haby-plan-inputs/v1\n");
        text.Append(inputs.EnvironmentRecordId).Append('\n').Append(inputs.EnvironmentId.Value).Append('\n')
            .Append(inputs.ManifestRevisionRef.ApplicationRecordId).Append('\n')
            .Append(inputs.ManifestRevisionRef.RevisionNumber.Value).Append('\n');
        foreach (var provider in inputs.Providers)
            text.Append(provider.Alias.Value.Length).Append(':').Append(provider.Alias.Value)
                .Append(provider.Resolution.ProviderInstanceId.Length).Append(':').Append(provider.Resolution.ProviderInstanceId)
                .Append(provider.Resolution.ConfigurationRevision).Append(':')
                .Append(provider.Resolution.ImplementationId.Length).Append(':').Append(provider.Resolution.ImplementationId).Append('\n');
        return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void CheckActor(string actorId)
    {
        ArgumentNullException.ThrowIfNull(actorId);
        if (actorId.Length is 0 or > 256 || actorId.Any(char.IsControl))
            throw new ArgumentException("Actor identity must be bounded and nonempty.", nameof(actorId));
    }
}
