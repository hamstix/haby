using System.Security.Cryptography.X509Certificates;
using k8s;

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] is not ("--probe-stock-tls" or "--deployment-kernel")))
{
    Console.Error.WriteLine("Usage: qualification <explicit-https-endpoint> <private-credential-directory>");
    return 2;
}
try
{
    var endpoint = new Uri(args[0]);
    if (endpoint.Scheme != "https") return 2;
    var directory = Path.GetFullPath(args[1]);
    if (args.Length == 3 && args[2] == "--deployment-kernel")
    {
        using var bootstrap = ClientFactory.Create(endpoint, directory);
        using var kernelDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        await DeploymentKernelCases.Run(bootstrap, endpoint, directory, kernelDeadline.Token);
        return 0;
    }
    if (args.Length == 3)
    {
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(Path.Combine(directory, "ca.crt"));
        using var stock = new Kubernetes(new KubernetesClientConfiguration
        {
            Host = endpoint.AbsoluteUri,
            SslCaCerts = roots,
            TlsServerName = "wrong.haby-qualification.invalid",
            ClientCertificateFilePath = Path.Combine(directory, "admin.crt"),
            ClientKeyFilePath = Path.Combine(directory, "admin.key"),
            HttpClientTimeout = TimeSpan.FromSeconds(10),
        });
        using var stockDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await stock.Version.GetCodeAsync(stockDeadline.Token);
        Console.Error.WriteLine("FAIL stock SslCaCerts accepted mismatched server name; stock path is unqualified.");
        return 1;
    }
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
    using var untrusted = ClientFactory.Create(endpoint, directory, untrusted: true);
    try
    {
        await untrusted.Version.GetCodeAsync(deadline.Token);
        Console.Error.WriteLine("FAIL untrusted CA accepted");
        return 1;
    }
    catch (HttpRequestException) { Console.WriteLine("PASS untrusted CA rejected"); }
    try
    {
        using var wrongKey = ClientFactory.Create(endpoint, directory, wrongKey: true);
        await wrongKey.Version.GetCodeAsync(deadline.Token);
        Console.Error.WriteLine("FAIL mismatched client key accepted");
        return 1;
    }
    catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or ArgumentException or HttpRequestException)
    {
        Console.WriteLine("PASS mismatched client key rejected");
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
