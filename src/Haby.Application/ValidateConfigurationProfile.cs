using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

/// <summary>Portable declaration validation only; does not resolve references or access storage.</summary>
public static class ValidateConfigurationProfile
{
    public static ProfileWireResult<ConfigurationProfileRevision> Execute(ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = ProfilePayloadParser.ParseProfile(payload);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
