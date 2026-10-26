using Haby.Application;
using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class SecretMaterialTests
{
    [Fact]
    public void Material_copies_input_and_output_and_never_formats_bytes()
    {
        var input = new byte[] { 23, 24 };
        var material = new SecretMaterial(input);
        input[0] = 0;
        var output = material.CopyBytes();
        output[1] = 0;
        Assert.Equal(new byte[] { 23, 24 }, material.CopyBytes());
        Assert.Equal("SecretMaterial", material.ToString());
    }

    [Fact]
    public void Secret_ref_remains_exact_and_scope_identity_is_typed()
    {
        var secret = SecretId.New();
        var version = SecretVersionId.New();
        Assert.Equal(new SecretRef(secret, version),
            new SecretRef(SecretId.Parse(secret.ToString()), SecretVersionId.Parse(version.ToString())));
        Assert.NotEqual(new SecretRef(secret, version), new SecretRef(secret, SecretVersionId.New()));
        Assert.NotEqual(EnvironmentRecordId.New(), EnvironmentRecordId.New());
    }
}
