using Haby.Cli;

return await new CliApplication(
    new EnvironmentEndpointSource(),
    new GrpcRegistryClientFactory(),
    Console.Out,
    Console.Error).RunAsync(args);
