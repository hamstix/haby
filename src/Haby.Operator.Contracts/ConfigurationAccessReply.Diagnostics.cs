namespace Haby.Operator.Contracts;

public sealed partial class ConfigurationAccessReply : Google.Protobuf.ICustomDiagnosticMessage
{
    public string ToDiagnosticString() => "{\"configurationAccess\":\"[redacted]\"}";
}

public sealed partial class DeliverConfigurationReply : Google.Protobuf.ICustomDiagnosticMessage
{
    public string ToDiagnosticString() => "{\"configurationDelivery\":\"[redacted]\"}";
}
