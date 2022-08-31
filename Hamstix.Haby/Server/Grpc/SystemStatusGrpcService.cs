using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Shared.Grpc.System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Hamstix.Haby.Server.Grpc;

[Authorize]
public class SystemStatusGrpcService : SystemStatusService.SystemStatusServiceBase
{
    readonly Services.ISchemaInitializer _schemaInitializer;
    readonly IWebHostEnvironment _env;
    readonly DbContextOptions<HabbyContext> _contextOptions;

    public SystemStatusGrpcService(DbContextOptions<HabbyContext> contextOptions,
            Services.ISchemaInitializer schemaInitializer,
            IWebHostEnvironment env)
    {
        _contextOptions = contextOptions;
        _env = env;
        _schemaInitializer = schemaInitializer;
    }

    public override async Task<ApplicationStatusModel> GetApplicationStatus(Empty request, ServerCallContext context)
    {
        // Try connect to Database.
        try
        {
            await CheckDatabaseConnection();
        }
        catch (Exception e)
        {
            return new ApplicationStatusModel
            {
                DbSchemaInitialized = false,
                Message = e.Message,
                Status = RegStatuses.Error
            };
        }

        var model = await BuildStatusModel();

        // Exit early if the database has not yet been configured to prevent an error.
        if (!model.DbSchemaInitialized)
            return model;

        model.Status = RegStatuses.Initialized;

        return model;
    }

    public override async Task<ApplicationStatusModel> InitializeSchema(Empty request, ServerCallContext context)
    {
        var model = await BuildStatusModel();
        try
        {
            if (!await _schemaInitializer.IsSchemaInitialized())
            {
                await _schemaInitializer.InitializeSchema();
            }

            model.DbSchemaInitialized = true;
        }
        catch (Exception e)
        {
            model.DbSchemaInitialized = false;
            model.Message = e.Message;
        }

        await CheckDatabaseConnection();
        model.Status = RegStatuses.Initialized;

        return model;
    }

    async Task CheckDatabaseConnection()
    {
        using var context = new HabbyContext(_contextOptions);
        await context.RegConfiguration.AnyAsync();
    }

    async Task<ApplicationStatusModel> BuildStatusModel()
    {
        var model = new ApplicationStatusModel
        {
            Version = Monq.Core.BasicDotNetMicroservice.Helpers.MicroserviceInfo.GetEntryPointAssembleVersion(),
            Status = RegStatuses.NotInitialized,
            DbSchemaInitialized = await _schemaInitializer.IsSchemaInitialized(),
            Environment = _env.EnvironmentName.ToLower(),
            ApiVersion = AppConstants.CurrentApiVersion.ToString()
        };

        return model;
    }
}
