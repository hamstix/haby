using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using k8s.Models;

// Source-bound diagnostic envelope only: no database, Host, subprocess or API.
// Includes every configured assertion and one-off producer, maximum caller
// multiplicities, both bounded UPDATE waits and negative cleanup suffixes.
internal static class ConfiguredReportingControls
{
    internal static void Run()
    {
        var root = Environment.GetEnvironmentVariable("HABY_TEST_SOURCE_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new InvalidOperationException("Explicit source root required.");
        string Source(string file) => File.ReadAllText(Path.Combine(root, "tools/Haby.Kubernetes.Qualification", file + ".cs"));
        if (Regex.Matches(Source("ConfiguredDeploymentCases"), "await WithHost\\(").Count != 6 ||
            Regex.Matches(Source("ConfiguredDeploymentCases"), "await Startup\\(").Count != 4 ||
            !Source("ConfiguredUpdateRetirement").Contains("MaximumRounds: 128", StringComparison.Ordinal))
            throw new InvalidOperationException("Configured caller multiplicities changed; re-inventory reporting bounds.");
        var updateCaller = Source("ConfiguredDeploymentUpdateCases");
        if (!updateCaller.Contains("recipientDiagnostic: diagnostics.Recipient", StringComparison.Ordinal) ||
            !updateCaller.Contains("value => diagnostics.Completion(value.Stage.ToString(), value.Predicate.ToString())", StringComparison.Ordinal))
            throw new InvalidOperationException("Configured reporting observer is no longer bound to the actual caller.");
        var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var original = Console.Out;
        try
        {
            Console.SetOut(text);
            Console.WriteLine("Q243 configured entry start");
            foreach (var file in new[] { "ConfiguredDeploymentCases", "ConfiguredDeploymentUpdateCases", "ConfiguredCorrectiveCases", "ConfiguredUpdateRetirement", "ConfiguredFixtureDrain" })
            {
                var source = Source(file);
                // Every fixed success emission at its actual lexical caller; all
                // alternate failures are exercised separately below. Host builds
                // execute six times, Startup four; other fixed sites once.
                foreach (Match match in Regex.Matches(source, "Console\\.WriteLine\\(\"((?:Q243 configured|PASS configured-)[^\"]+)\"\\)"))
                {
                    var line = match.Groups[1].Value;
                    if (line.EndsWith(" failed", StringComparison.Ordinal) || line.Contains("Expired", StringComparison.Ordinal) ||
                        line.Contains("DrainExhausted", StringComparison.Ordinal) || line.Contains("DrainOwnershipRefused", StringComparison.Ordinal)) continue;
                    var count = line.StartsWith("Q243 configured host-", StringComparison.Ordinal) ? 6 :
                        line.StartsWith("Q243 configured pod-startup", StringComparison.Ordinal) ? 4 : 1;
                    for (var i = 0; i < count; i++) Console.WriteLine(line);
                }
                foreach (Match match in Regex.Matches(source, "\"((?:C|R|A|B|U)[0-9]{2}-[a-zA-Z0-9-]+)\""))
                {
                    var id = match.Groups[1].Value;
                    if (file == "ConfiguredDeploymentCases") ConfiguredDeploymentCases.Check(true, id);
                    else if (file == "ConfiguredDeploymentUpdateCases") ConfiguredDeploymentUpdateCases.Check(true, id);
                    else ConfiguredUpdateRetirement.Check(true, id);
                }
            }
            using (var corrective = new ConfiguredCorrectiveDiagnostics())
            {
                foreach (var phase in Enum.GetValues<ConfiguredCorrectiveDiagnostics.Phase>())
                { corrective.Enter(phase); corrective.Complete(); }
            }
            using (var lifetime = new ConfiguredLifecycle())
            {
                var source = Source("ConfiguredDeploymentCases");
                var cliCalls = Regex.Matches(source, "ConfiguredNativeCli\\.Run\\(").Count;
                for (var i = 0; i < cliCalls; i++)
                {
                    using var phase = lifetime.Start(ConfiguredLifecycle.Kind.NativeCli);
                    ConfiguredCliProcess.WithOwnedConfigurationAsync(() => Task.FromResult("complete"), () => { }, phase.Complete).GetAwaiter().GetResult();
                }
                using (var delivery = lifetime.Start(ConfiguredLifecycle.Kind.Delivery)) delivery.Complete();
                lifetime.Functional.Complete();
                foreach (var kind in new[] { ConfiguredLifecycle.Kind.Drain, ConfiguredLifecycle.Kind.ReleaseCleanup, ConfiguredLifecycle.Kind.NamespaceCleanup })
                { using var phase = lifetime.Start(kind); phase.Complete(); }
                lifetime.Complete();
            }
            var updateSource = Source("ConfiguredDeploymentUpdateCases");
            foreach (Match match in Regex.Matches(updateSource, "(Begin|Require)\\(UpdateStage\\.([A-Za-z0-9]+)"))
            {
                var stage = Enum.Parse<ConfiguredDeploymentUpdateDiagnostics.Stage>(match.Groups[2].Value);
                if (stage == ConfiguredDeploymentUpdateDiagnostics.Stage.ExpectedLoss) continue; // This Require is the failure-only unexpected-return branch.
                Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Progress(stage, ConfiguredDeploymentUpdateDiagnostics.State.Start));
                Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Progress(stage, match.Groups[1].Value == "Require" ?
                    ConfiguredDeploymentUpdateDiagnostics.State.Passed : ConfiguredDeploymentUpdateDiagnostics.State.Completed));
            }
            Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Wire(200, 1));
            Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Progress(ConfiguredDeploymentUpdateDiagnostics.Stage.ExpectedLoss, ConfiguredDeploymentUpdateDiagnostics.State.Observed));
            var confirmed = new InternalActionResult(InternalActionOutcome.Confirmed, "observation-confirmed");
            Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Result(ConfiguredDeploymentUpdateDiagnostics.Stage.Recover, confirmed, 1));
            foreach (var stage in new[] { ConfiguredDeploymentUpdateDiagnostics.Stage.DelegateExecute, ConfiguredDeploymentUpdateDiagnostics.Stage.DelegateObserve })
                Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Delegated(stage, confirmed, 1));
            var output = new ConfiguredRetirementDiagnostics.Output();
            // Two WaitUpdate calls, each bounded to128 rounds by the real caller.
            for (var i = 0; i < 256; i++)
            {
                output.Completion("Begin", "Unknown");
                output.Completion("Recipients", "Unknown");
                output.Recipient(new(1, 1, i < 128 ? 1 : 0, RecipientProjectionFailure.None, RecipientProjectionFailure.None));
                output.Completion("CloseRequest", "Unknown");
            }
            foreach (var stage in Enum.GetValues<UpdateCompletionStage>().Where(x => x is not (UpdateCompletionStage.Unknown or UpdateCompletionStage.PrerequisiteIncomplete)))
                output.Completion(stage.ToString(), "Unknown");
            // Existing Wait wrappers retain returned complete member inventory.
            for (var i = 0; i < 2; i++)
                ConfiguredRetirementDiagnostics.WaitAsync(() => ValueTask.FromResult(new RolloutResult(true, null, null, RolloutStop.Complete,
                    [new("protected-subject", Guid.Empty, DateTimeOffset.MaxValue, RolloutBudget.Active, new(RolloutOutcome.Ready, "ready"))],
                    AppliedGeneration: 2, CompletionReason: "applied")), 2).GetAwaiter().GetResult();
            ConfiguredRetirementDiagnostics.ReadHeldAsync(() => Task.FromResult(new V1Pod())).GetAwaiter().GetResult();
            foreach (var condition in new[] { "U09Ready", "U09NotApplied", "U09OldRecipients", "U09HeldUid", "U09Terminating", "U11Ready", "U11Applied", "U11Generation" })
                Console.WriteLine(ConfiguredRetirementDiagnostics.Condition(condition, true));
            Console.WriteLine(ConfiguredRetirementDiagnostics.Finalizer(null));
            Console.WriteLine(ConfiguredRetirementDiagnostics.Finalizer(true));
            Console.WriteLine(ConfiguredRetirementDiagnostics.Finalizer(null));
            Console.WriteLine(ConfiguredRetirementDiagnostics.Finalizer(false));
            Console.WriteLine("Q243 configured rpc OK");
        }
        finally { Console.SetOut(original); }
        var envelope = text.ToString();
        Console.WriteLine("CASE full"); Console.Write(envelope);
        Console.WriteLine("CASE negative"); Console.Write(envelope);
        // Worst-case suffix: first failure after all repetitive success callbacks,
        // failed release/namespace/catalog plus CLI termination/output categories.
        // Worst-case conservative suffix retains all nine successes plus a rejected failure.
        using (var corrective = new ConfiguredCorrectiveDiagnostics())
        { corrective.Enter(ConfiguredCorrectiveDiagnostics.Phase.OidProof); corrective.Reject(ConfiguredCorrectiveDiagnostics.Result.CopiedProof); }
        var negative = new ConfiguredRetirementDiagnostics.Output();
        for (var i = 0; i < 256; i++)
        {
            negative.Completion("Begin", "Unknown");
            negative.Recipient(new(1, 1, 0, RecipientProjectionFailure.None, RecipientProjectionFailure.None));
        }
        for (var i = 0; i < 256; i++)
        {
            negative.Completion("PrerequisiteIncomplete", "UpdateInventoryMissing");
            negative.Recipient(new(1, 1, 1, RecipientProjectionFailure.Container, RecipientProjectionFailure.Shape));
        }
        foreach (var phase in new[] { "Primary", "DrainCleanup", "ReleaseCleanup", "NamespaceCleanup", "WaitThrew", "HeldReadThrew" })
            Console.WriteLine(ConfiguredRetirementDiagnostics.Failure(phase, new OperationCanceledException("protected-secret")));
        foreach (var phase in new[] { "cleanup-namespace", "cleanup-catalog" }) Console.WriteLine("Q243 configured " + phase + " failed");
        using (var lifetime = new ConfiguredLifecycle())
        { using var cli = lifetime.Start(ConfiguredLifecycle.Kind.NativeCli); cli.NoteIncomplete("Output"); }
        try { ConfiguredDeploymentCases.Check(false, "C07-complete-saved-projection-and-Pod-identity"); }
        catch (InvalidOperationException) { }
        Console.WriteLine("Q243 configured cli-tail Output");
        Console.WriteLine("Q243 configured cli-tail Termination");
        // Full bounded supplemental rejection/read-only diagnosis, rather than
        // testing only its formatter. The real UPDATE caller has one PATCH.
        using var rejected = new HttpResponseMessage(System.Net.HttpStatusCode.UnprocessableEntity);
        var patch = new JsonArray(new JsonObject { ["op"] = "test", ["path"] = "/metadata/uid", ["value"] = "protected-uid" });
        var causes = string.Join(",", Enumerable.Repeat("{\"reason\":\"FieldValueInvalid\",\"message\":\"protected-secret\",\"field\":\"protected-field\"}", 16));
        foreach (var record in ConfiguredDeploymentPatchStatus.Classify(rejected, "{\"reason\":\"Invalid\",\"details\":{\"causes\":[" + causes + "]}}", patch)) Console.WriteLine(record);
        foreach (var predicate in Enum.GetValues<ConfiguredDeploymentUpdateDiagnostics.Predicate>())
            Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Proof(predicate, false, null));
        foreach (var probe in new[] { ConfiguredDeploymentPatchStatus.ProbeCase.Control, ConfiguredDeploymentPatchStatus.ProbeCase.BootstrapValueTest })
            Console.WriteLine(ConfiguredDeploymentPatchStatus.Probe(probe, 422));
        Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Wire(422, 1));
        Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Progress(ConfiguredDeploymentUpdateDiagnostics.Stage.ExpectedLoss, ConfiguredDeploymentUpdateDiagnostics.State.Failed));
        Console.WriteLine(ConfiguredDeploymentUpdateDiagnostics.Failure(ConfiguredDeploymentUpdateDiagnostics.Stage.U06PatchUid, 1));
    }
}
