using Haby.Operator.Host;

await using var app = await OperatorHostApplication.BuildMigratedAsync(args);
await app.RunAsync();
