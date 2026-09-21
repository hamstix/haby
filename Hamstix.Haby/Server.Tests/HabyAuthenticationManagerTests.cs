using Hamstix.Haby.Server.Authentication;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hamstix.Haby.Server.Tests;

public class HabyAuthenticationManagerTests
{
    [Fact]
    public void ValidateToken_AcceptsConfiguredToken()
    {
        var manager = CreateManager("expected-token");

        var result = manager.ValidateToken("expected-token");

        Assert.True(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("wrong-token")]
    public void ValidateToken_RejectsInvalidToken(string token)
    {
        var manager = CreateManager("expected-token");

        var result = manager.ValidateToken(token);

        Assert.False(result);
    }

    static HabyAuthenticationManager CreateManager(string token)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecureToken"] = token,
            })
            .Build();

        return new HabyAuthenticationManager(configuration);
    }
}
