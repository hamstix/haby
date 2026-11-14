using Grpc.Net.Client;
using Haby.Configuration.Client;
using Haby.Configuration.Contracts;

var stage = "args";
try
{
    stage = "args";
    if (args.Length is not (5 or 6)) throw new InvalidOperationException();
    stage = "endpoint-policy";
    var endpoint = new Uri(args[0]);
    var loopbackTest = args.Length == 6 && args[5] == "--allow-loopback-http" && endpoint.IsLoopback && endpoint.Scheme == "http";
    if (endpoint.Scheme != "https" && !loopbackTest) throw new InvalidOperationException();
    // Caller owns endpoint/system TLS trust and channel. No ambient credentials or retry/hedging policy.
    stage = "channel";
    using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { MaxReceiveMessageSize = 8 * 1024 * 1024 });
    stage = "client";
    var startup = new RuntimeStartupClient(new ConfigurationDelivery.ConfigurationDeliveryClient(channel));
    stage = "fetch";
    using var complete = await startup.FetchAsync(args[1], [args[3], args[4]], args[2]);
    // Application-specific initialization/binding starts here, only after the entire mandatory set is present.
    stage = "primary";
    if (complete.Documents.Count != 2) throw new InvalidOperationException();
    Console.WriteLine("primary-started");
    return 0;
}
catch (RuntimeStartupException failure)
{
    Console.Error.WriteLine(failure.Failure switch
    {
        RuntimeStartupFailure.InvalidBootstrap => "runtime-startup-refused:invalid-bootstrap",
        RuntimeStartupFailure.CredentialUnavailable => "runtime-startup-refused:credential-unavailable",
        RuntimeStartupFailure.InvalidCredential => "runtime-startup-refused:invalid-credential",
        RuntimeStartupFailure.InvalidResponse => "runtime-startup-refused:invalid-response",
        RuntimeStartupFailure.OutputLimit => "runtime-startup-refused:output-limit",
        RuntimeStartupFailure.IdentityRefused => "runtime-startup-refused:identity-refused",
        RuntimeStartupFailure.AccessDenied => "runtime-startup-refused:access-denied",
        RuntimeStartupFailure.ConfigurationUnavailable => "runtime-startup-refused:configuration-unavailable",
        RuntimeStartupFailure.TransportFailure => "runtime-startup-refused:transport-failure",
        RuntimeStartupFailure.CallerCancelled => "runtime-startup-refused:caller-cancelled",
        RuntimeStartupFailure.StartupDeadlineExceeded => "runtime-startup-refused:startup-deadline-exceeded",
        _ => "runtime-startup-refused:unknown"
    });
    return 1;
}
catch
{
    Console.Error.WriteLine(stage switch
    {
        "args" => "runtime-startup-refused:unknown;stage=args",
        "endpoint-policy" => "runtime-startup-refused:unknown;stage=endpoint-policy",
        "channel" => "runtime-startup-refused:unknown;stage=channel",
        "client" => "runtime-startup-refused:unknown;stage=client",
        "fetch" => "runtime-startup-refused:unknown;stage=fetch",
        "primary" => "runtime-startup-refused:unknown;stage=primary",
        _ => "runtime-startup-refused:unknown"
    });
    return 1;
}
