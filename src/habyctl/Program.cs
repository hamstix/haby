using System.Text;
using Haby.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
return await HabyCliBuilder.CreateDefault().Build().RunAsync(args);
