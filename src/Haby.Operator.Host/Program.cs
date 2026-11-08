using Haby.Operator.Host;

if (args.FirstOrDefault() == "operator-token-issue")
    return await LocalOperatorIssuance.RunAsync(args[1..], Console.Out, Console.Error);

await using var app = await OperatorHostApplication.BuildMigratedAsync(args);
app.Run();
return 0;
