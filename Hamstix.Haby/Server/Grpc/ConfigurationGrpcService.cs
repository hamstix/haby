using Grpc.Core;
using Hamstix.Haby.Client.Extensions;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Configurator;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Grpc.Configuration;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Grpc;

[Authorize]
public class ConfigurationGrpcService : ConfigurationService.ConfigurationServiceBase
{
    readonly HabbyContext _context;
    readonly ICuConfigurator _configurator;

    public ConfigurationGrpcService(
        HabbyContext context,
        ICuConfigurator configurator
        )
    {
        _configurator = configurator;
        _context = context;
    }

    public override async Task<GetCUKeysResponse> GetKeys(GetCUKeysRequest request, ServerCallContext context)
    {
        var keys = await _context
                .ConfigurationUnits
                .AsNoTracking()
                .Where(x => x.Name == request.CuName)
                .SelectMany(x => x.Keys)
                .ToListAsync();

        var response = keys.ApplyFieldMask<ConfigurationKey, GetCUKeysResponse, KeyModel>(
            request.FieldMask, (response, item) => response.Keys.Add(item));

        return response;
    }

    public override async Task<KeyConfigurationCodeModel> GetKeyConfiguration(GetKeyConfigurationRequest request, ServerCallContext context)
    {
        var configuration = await _context
            .ConfigurationKeys
            .AsNoTracking()
            .Where(x => x.ConfigurationUnit.Name == request.CuName && x.Name == request.KeyName)
            .ProjectToType<KeyConfigurationCodeModel>()
            .FirstOrDefaultAsync();

        if (configuration is null)
            throw new RpcException(new Status(StatusCode.NotFound, "The configuration key is not found."));

        return configuration;
    }

    public override async Task<KeyConfigurationCodeModel> UpdateKeyConfiguration(UpdateKeyConfigurationRequest request, ServerCallContext context)
    {
        var configuration = await _context.ConfigurationKeys
                .FirstOrDefaultAsync(x => x.ConfigurationUnit.Name == request.CuName && x.Name == request.KeyName);

        if (configuration is null)
            throw new RpcException(new Status(StatusCode.NotFound, "The configuration key is not found."));

        JsonObject jsonNode;
        try
        {
            jsonNode = JsonNode.Parse(request.KeyConfiguration.Configuration)?.AsObject() ?? new JsonObject();
        }
        catch (Exception e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Can't parse json configuration. Details: {e.Message}"));
        }

        configuration.Configuration = jsonNode;
        await _context.SaveChangesAsync();

        return new KeyConfigurationCodeModel { Configuration = jsonNode.ToProtoStruct() };
    }

    public override async Task<KeyConfigurationCodeModel> RegenerateKeyConfig(RegenerateKeyConfigRequest request, ServerCallContext context)
    {
        var configuration = await _context.ConfigurationKeys
                .FirstOrDefaultAsync(x => x.ConfigurationUnit.Name == request.CuName && x.Name == request.KeyName);

        if (configuration is null)
            throw new RpcException(new Status(StatusCode.NotFound, "The configuration key is not found."));

        configuration.Configuration = new JsonObject();
        await _context.SaveChangesAsync();

        return new KeyConfigurationCodeModel { Configuration = configuration.Configuration.AsObject().ToProtoStruct() };
    }
}