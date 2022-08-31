using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Configurator;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Grpc.ConfigurationUnits;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Grpc;

[Authorize]
public class ConfigurationUnitsGrpcService : ConfigurationUnitsService.ConfigurationUnitsServiceBase
{
    readonly HabbyContext _context;
    readonly ICuConfigurator _configurator;

    public ConfigurationUnitsGrpcService(
        HabbyContext context,
        ICuConfigurator configurator
        )
    {
        _configurator = configurator;
        _context = context;
    }

    public override async Task<GetAllResponse> GetAll(GetAllRequest request, ServerCallContext context)
    {
        var configurationUnits = await _context.ConfigurationUnits
                .AsNoTracking()
                .ToListAsync();

        var response = configurationUnits.ApplyFieldMask<ConfigurationUnit, GetAllResponse, ConfigurationUnitModel>(
            request.FieldMask, (response, item) => response.ConfigurationUnits.Add(item));

        return response;
    }

    public override async Task<ConfigurationUnitModel> GetById(GetByIdRequest request, ServerCallContext context)
    {
        var configurationUnit = await _context.ConfigurationUnits.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (configurationUnit is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"The configuration unit id {request.Id} is not found."));

        return configurationUnit.ApplyFieldMask<ConfigurationUnit, ConfigurationUnitModel>(request.FieldMask);
    }

    public override async Task<ConfigurationUnitModel> Create(CreateRequest request, ServerCallContext context)
    {
        if (await _context.ConfigurationUnits.AnyAsync(x => x.Name == request.Name.Trim()))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The configuration unit name {request.Name} has already taken."));

        JsonArray jsonObject;
        try
        {
            jsonObject = JsonArray.Parse(request.Template)?.AsArray() ?? new JsonArray();
        }
        catch (Exception e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Can't parse json configuration. It must be an array. Details: {e.Message}"));
        }

        var cu = new ConfigurationUnit(request.Name.Trim(), request.Version.Trim())
        {
            Template = jsonObject
        };

        _context.ConfigurationUnits.Add(cu);

        await _context.SaveChangesAsync();

        return cu.Adapt<ConfigurationUnitModel>();
    }

    public override async Task<ConfigurationUnitModel> Update(UpdateRequest request, ServerCallContext context)
    {
        var configurationUnit = await _context.ConfigurationUnits.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (configurationUnit is null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"The configuration unit id {request.Id} is not found."));

        if (await _context.ConfigurationUnits.AnyAsync(x => x.Name == request.ConfigurationUnit.Name.Trim() && x.Id != request.Id))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The configuration unit name {request.ConfigurationUnit.Name} has already taken."));

        JsonArray jsonObject;
        try
        {
            jsonObject = JsonArray.Parse(request.ConfigurationUnit.Template)?.AsArray() ?? new JsonArray();
        }
        catch (Exception e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Can't parse json configuration. It must be an array. Details: {e.Message}"));
        }
        configurationUnit.Template = jsonObject;
        configurationUnit.Name = request.ConfigurationUnit.Name;
        configurationUnit.Version = request.ConfigurationUnit.Version;

        await _context.SaveChangesAsync();

        return configurationUnit.Adapt<ConfigurationUnitModel>();
    }

    public override async Task<Empty> Delete(DeleteRequest request, ServerCallContext context)
    {
        var cu = await _context.ConfigurationUnits.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (cu is not null)
        {
            _context.ConfigurationUnits.Remove(cu);

            await _context.SaveChangesAsync();
        }
        return new Empty();
    }

    public override async Task<ReconfigurationResponse> Reconfigure(ReconfigurationRequest request, ServerCallContext context)
    {
        if (!await _context.ConfigurationUnits.AnyAsync(x => x.Id == request.Id))
            throw new RpcException(new Status(StatusCode.NotFound,
                $"The configuration unit id {request.Id} is not found."));

        var results = await _configurator.Configure(request.Id);

        var configurationUnit = await _context.ConfigurationUnits.FirstAsync(x => x.Id == request.Id);

        var result = new ReconfigurationResponse
        {
            ConfigurationUnit = configurationUnit.Adapt<ConfigurationUnitModel>(),
        };
        result.Results.AddRange(results.Adapt<List<ReconfigurationKeyResultModel>>());

        return result;
    }

    public override async Task<GetVariablesResponse> GetVariables(GetVariablesRequest request, ServerCallContext context)
    {
        if (!await _context.ConfigurationUnits.AnyAsync(x => x.Id == request.Id))
            throw new RpcException(new Status(StatusCode.NotFound,
                $"The configuration unit id {request.Id} is not found."));

        var variables = await _context
            .Variables
            .Include(x => x.ConfigurationUnitAtService)
                .ThenInclude(x => x.Service)
            .Where(x => x.ConfigurationUnitId == request.Id)
            .ProjectToType<ConfigurationUnitVariableModel>()
            .ToListAsync();
        var response = new GetVariablesResponse();
        response.Variables.AddRange(variables);
        return response;
    }

    public override async Task<Empty> DeleteVariable(DeleteVariableRequest request, ServerCallContext context)
    {
        if (!await _context.ConfigurationUnits.AnyAsync(x => x.Id == request.Id))
            throw new RpcException(new Status(StatusCode.NotFound,
                $"The configuration unit id {request.Id} is not found."));

        var variable = await _context
            .Variables
            .FirstOrDefaultAsync(x => x.Name == request.Name && x.ServiceId == request.ServiceId && x.Key == request.Key);
        if (variable is not null)
        {
            _context.Variables.Remove(variable);
            await _context.SaveChangesAsync();
        }

        return new Empty();
    }
}