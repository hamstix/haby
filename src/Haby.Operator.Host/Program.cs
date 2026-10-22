using Haby.Operator.Host;

await using var app = await OperatorHostApplication.BuildMigratedAsync(args);
app.Run();
