using Google.Protobuf.WellKnownTypes;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Grpc;
using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Shared.PluginsCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;
using ConfigurationGrpc = Hamstix.Haby.Shared.Grpc.Configuration;
using ConfigurationUnitsGrpc = Hamstix.Haby.Shared.Grpc.ConfigurationUnits;
using GeneratorsGrpc = Hamstix.Haby.Shared.Grpc.Generators;
using OrganizationUnitsGrpc = Hamstix.Haby.Shared.Grpc.OrganizationUnits;
using PluginsGrpc = Hamstix.Haby.Shared.Grpc.Plugins;
using ServicesGrpc = Hamstix.Haby.Shared.Grpc.Services;
using SystemGrpc = Hamstix.Haby.Shared.Grpc.System;

namespace Hamstix.Haby.Server.Tests;

public class GrpcServicesSmokeTests
{
    [Fact]
    public async Task ConfigurationService_HandlesGetKeys()
    {
        await using var context = CreateContext();
        var sut = new ConfigurationGrpcService(context, null!);

        var response = await sut.GetKeys(new ConfigurationGrpc.GetCUKeysRequest(), null!);

        Assert.Empty(response.Keys);
    }

    [Fact]
    public async Task ConfigurationUnitsService_HandlesGetAll()
    {
        await using var context = CreateContext();
        var sut = new ConfigurationUnitsGrpcService(context, null!);

        var response = await sut.GetAll(new ConfigurationUnitsGrpc.GetAllRequest(), null!);

        Assert.Empty(response.ConfigurationUnits);
    }

    [Fact]
    public async Task GeneratorsService_HandlesGetAll()
    {
        await using var context = CreateContext();
        var sut = new GeneratorsGrpcService(context);

        var response = await sut.GetAll(new GeneratorsGrpc.GetAllRequest(), null!);

        Assert.Empty(response.Generators);
    }

    [Fact]
    public async Task OrganizationUnitsService_HandlesGetAll()
    {
        await using var context = CreateContext();
        var sut = new OrganizationUnitsGrpcService(context);

        var response = await sut.GetAll(new OrganizationUnitsGrpc.GetAllRequest(), null!);

        Assert.Empty(response.OrganizationUnits);
    }

    [Fact]
    public async Task PluginsService_HandlesGetAll()
    {
        await using var context = CreateContext();
        var pluginsService = new PluginsService(Array.Empty<Plugin>());
        var sut = new PluginsGrpcService(context, pluginsService);

        var response = await sut.GetAll(new PluginsGrpc.PluginsRequest(), null!);

        Assert.Empty(response.Plugins);
    }

    [Fact]
    public async Task ServicesService_HandlesGetAll()
    {
        await using var context = CreateContext();
        var pluginsService = new PluginsService(Array.Empty<Plugin>());
        var sut = new ServicesGrpcService(context, pluginsService);

        var response = await sut.GetAll(new ServicesGrpc.GetAllRequest(), null!);

        Assert.Empty(response.Services);
    }

    [Fact]
    public async Task SystemService_HandlesAuthenticationCheck()
    {
        await using var context = CreateContext();
        var sut = new SystemGrpcService(context);

        var response = await sut.CheckAuthToken(new SystemGrpc.AclModel(), null!);

        Assert.True(response.IsAuthSuccessful);
    }

    [Fact]
    public async Task SystemStatusService_HandlesApplicationStatus()
    {
        var options = CreateContextOptions();
        var sut = new SystemStatusGrpcService(
            options,
            new UninitializedSchemaInitializer(),
            new TestWebHostEnvironment());

        var response = await sut.GetApplicationStatus(new Empty(), null!);

        Assert.False(response.DbSchemaInitialized);
        Assert.Equal(SystemGrpc.RegStatuses.NotInitialized, response.Status);
    }

    static HabbyContext CreateContext() => new(CreateContextOptions());

    static DbContextOptions<HabbyContext> CreateContextOptions() =>
        new DbContextOptionsBuilder<HabbyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    sealed class UninitializedSchemaInitializer : ISchemaInitializer
    {
        public Task<bool> IsSchemaInitialized() => Task.FromResult(false);

        public Task InitializeSchema() => Task.CompletedTask;
    }

    sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = nameof(GrpcServicesSmokeTests);
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = Environments.Development;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
    }
}
