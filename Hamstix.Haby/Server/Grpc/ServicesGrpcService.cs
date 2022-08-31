using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Shared.Grpc.Services;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Grpc;

[Authorize]
public class ServicesGrpcService : ServicesService.ServicesServiceBase
{
    readonly HabbyContext _context;
    readonly IPluginsService _pluginsService;

    public ServicesGrpcService(
        HabbyContext context,
        IPluginsService pluginsService
        )
    {
        _pluginsService = pluginsService;
        _context = context;
    }

    public override async Task<GetAllResponse> GetAll(GetAllRequest request, ServerCallContext context)
    {
        var services = await _context.Services
                .AsNoTracking()
                .ToListAsync();

        var response = services.ApplyFieldMask<Service, GetAllResponse, ServiceModel>(
            request.FieldMask, (response, item) => response.Services.Add(item));

        return response;
    }

    public override async Task<ServiceModel> GetById(GetByIdRequest request, ServerCallContext context)
    {
        var service = await _context.Services.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (service is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"The service id {request.Id} is not found."));

        return service.ApplyFieldMask<Service, ServiceModel>(request.FieldMask);
    }

    public override async Task<ServiceModel> Create(CreateRequest request, ServerCallContext context)
    {
        if (await _context.Services.AnyAsync(x => x.Name == request.Name.Trim()))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The service name {request.Name} has already taken."));

        JsonObject jsonObject;
        try
        {
            jsonObject = JsonObject.Parse(request.JsonConfig)?.AsObject() ?? new JsonObject();
        }
        catch (Exception e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Can't parse json configuration. Details: {e.Message}"));
        }

        var service = new Service(request.Name.Trim())
        {
            JsonConfig = jsonObject,
            Template = request.Template
        };

        if (request.PluginName is not null)
        {
            if (!_pluginsService.Plugins.Any(x => x.Name == request.PluginName))
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Can't find plugin \"{request.PluginName}\" " +
                    $"in the registered plugins."));

            service.PluginName = request.PluginName;
        }

        _context.Services.Add(service);
        await _context.SaveChangesAsync();

        return service.Adapt<ServiceModel>();
    }

    public override async Task<ServiceModel> Update(UpdateRequest request, ServerCallContext context)
    {
        var service = await _context.Services.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (service is null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"The service id {request.Id} is not found."));

        if (await _context.Services.AnyAsync(x => x.Name == request.Service.Name.Trim() && x.Id != request.Id))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The service name {request.Service.Name} has already taken."));

        JsonObject jsonObject;
        try
        {
            jsonObject = JsonObject.Parse(request.Service.JsonConfig)?.AsObject() ?? new JsonObject();
        }
        catch (Exception e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Can't parse json configuration. Details: {e.Message}"));
        }

        if ((request.Service.PluginName is not null) && !_pluginsService.Plugins.Any(x => x.Name == request.Service.PluginName))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Can't find plugin \"{request.Service.PluginName}\" " +
                $"in the registered plugins."));

        if ((request.Service.PluginName is not null) && !await PluginAvailiable(request.Service.PluginName, service))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The plugin \"{request.Service.PluginName}\" " +
                $"has already taken by another service."));

        service.Name = request.Service.Name.Trim();
        service.JsonConfig = jsonObject;
        service.Template = request.Service.Template;
        service.PluginName = request.Service.PluginName;

        await _context.SaveChangesAsync();

        return service.Adapt<ServiceModel>();
    }

    public override async Task<Empty> Delete(DeleteRequest request, ServerCallContext context)
    {
        var service = await _context.Services.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (service is not null)
        {
            _context.Services.Remove(service);

            await _context.SaveChangesAsync();
        }
        return new Empty();
    }

    async Task<bool> PluginAvailiable(string pluginName, Service service) =>
            !await _context
                .Services
                .AnyAsync(x => x.PluginName == pluginName && x.Id != service.Id);
}