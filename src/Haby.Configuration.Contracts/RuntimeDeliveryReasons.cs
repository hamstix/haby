namespace Haby.Configuration.Contracts;

/// <summary>Closed, non-sensitive ResourceExhausted reasons for DeliverRuntime only.</summary>
public static class RuntimeDeliveryReasons
{
    public const string Header = "haby-runtime-reason";
    public const string AdmissionOverload = "admission-overload";
    public const string OutputLimit = "output-limit";
}
