using System.Text;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ConfigurationProfileValidationTests
{
    [Fact]
    public void Validation_reuses_portable_parser_and_honors_cancellation_without_dependencies()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"layout","revision":1},"spec":{}}
            """);
        var result = ValidateConfigurationProfile.Execute(bytes);
        Assert.True(result.IsSuccess);
        Assert.Equal(ProfilePayloadParser.ParseProfile(bytes).Declaration, result.Declaration);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ValidateConfigurationProfile.Execute(bytes, canceled.Token));
    }
}
