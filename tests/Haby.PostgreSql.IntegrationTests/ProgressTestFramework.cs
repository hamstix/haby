using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("Haby.PostgreSql.IntegrationTests.ProgressTestFramework", "Haby.PostgreSql.IntegrationTests")]

namespace Haby.PostgreSql.IntegrationTests;

// Observe the existing runner's messages; never replace its runner/options/scheduling.
public sealed class ProgressTestFramework(IMessageSink diagnostics) : XunitTestFramework(diagnostics)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new ProgressTestExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
}
internal sealed class ProgressTestExecutor(AssemblyName assembly, ISourceInformationProvider source, IMessageSink diagnostics)
    : XunitTestFrameworkExecutor(assembly, source, diagnostics)
{
    protected override void RunTestCases(IEnumerable<IXunitTestCase> cases, IMessageSink sink, ITestFrameworkExecutionOptions options) =>
        base.RunTestCases(cases, PostgreSqlProgress.Current is { } journal ? new ProgressMessageSink(sink, journal) : sink, options);
}
internal sealed class ProgressMessageSink(IMessageSink inner, PostgreSqlProgressJournal journal) : LongLivedMarshalByRefObject, IMessageSink
{
    private readonly object gate = new();
    private readonly Dictionary<ITest, long> active = new(ReferenceEqualityComparer.Instance);
    public bool OnMessage(IMessageSinkMessage message)
    {
        lock (gate)
        {
            if (message is ITestStarting starting)
            {
                var test = starting.Test;
                var method = test.TestCase.TestMethod.TestClass.Class.Name + "." + test.TestCase.TestMethod.Method.Name;
                var hash = test.DisplayName.Length <= 8192 ? PostgreSqlProgressJournal.CaseHash(method, test.DisplayName) : "";
                var id = journal.StartCase(hash);
                if (id != 0 && !active.TryAdd(test, id)) journal.FinishCase(0);
            }
            else if (message is ITestFinished finished && active.Remove(finished.Test, out var id)) journal.FinishCase(id);
            else if (message is ITestAssemblyFinished) journal.AssemblyFinished();
        }
        return inner.OnMessage(message);
    }
}
