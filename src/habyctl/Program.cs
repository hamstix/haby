using System.Text;
using Haby.Cli;

// JSON declarations can contain literal Unicode. Redirected output must preserve
// it independently of the Windows console code page, without a UTF-8 BOM.
Console.OutputEncoding = new UTF8Encoding(false);

return await new CliApplication(
    new EnvironmentEndpointSource(),
    new GrpcRegistryClientFactory(),
    Console.Out,
    Console.Error).RunAsync(args);
