using Haby.Configuration.Client;

try
{
    var endpoint = new Uri(Environment.GetEnvironmentVariable("HABY_RUNTIME_ENDPOINT") ?? throw new InvalidOperationException());
    var revision = Environment.GetEnvironmentVariable("HABY_RUNTIME_REVISION") ?? throw new InvalidOperationException();
    var path = Environment.GetEnvironmentVariable("HABY_RUNTIME_TOKEN_FILE") ?? throw new InvalidOperationException();
    if (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && (endpoint.IsLoopback ||
        Environment.GetEnvironmentVariable("HABY_RUNTIME_ALLOW_INSECURE_HTTP") == "1"))) throw new InvalidOperationException();
    var identity = await RuntimeConfigurationClient.ConnectAsync(endpoint, revision, path, CancellationToken.None);
    Console.WriteLine($"Configuration accepted; revision={revision}; user={identity.User}; database={identity.Database}; operation=ok");
    return 0;
}
catch
{
    Console.Error.WriteLine("Configuration delivery or application connection failed. Details withheld.");
    return 1;
}
