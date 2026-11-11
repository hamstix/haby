using System.Security.Cryptography.X509Certificates;
using k8s;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: qualification <explicit-https-endpoint> <private-credential-directory>");
    return 2;
}
try
{
    var endpoint = new Uri(args[0]);
    if (endpoint.Scheme != "https") return 2;
    var directory = Path.GetFullPath(args[1]);
    using var client = ClientFactory.Create(endpoint, directory);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    var version = await client.Version.GetCodeAsync(deadline.Token);
    Console.WriteLine($"mTLS version: {version.GitVersion}; framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    using var mismatch = ClientFactory.Create(endpoint, directory, tlsName: "wrong.haby-qualification.invalid");
    try
    {
        await mismatch.Version.GetCodeAsync(deadline.Token);
        Console.Error.WriteLine("FAIL TLS mismatched server name was accepted");
        return 1;
    }
    catch (HttpRequestException)
    {
        Console.WriteLine("PASS TLS mismatched server name rejected");
    }
    await Cases.Run(client, endpoint, directory, deadline.Token);
    return 0;
}
catch (Exception exception)
{
    // Never emit exception messages, response bodies, headers or credential material.
    Console.Error.WriteLine($"Qualification failed: {exception.GetType().Name}");
    if (exception is k8s.Autorest.HttpOperationException http) Console.Error.WriteLine($"HTTP {(int)http.Response.StatusCode}");
    return 1;
}
