using Hamstix.Haby.Client.Extensions;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddHabyClientServices(client => new Uri(builder.HostEnvironment.BaseAddress));

await builder.Build().RunAsync();
