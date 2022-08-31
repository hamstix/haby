using Grpc.Core;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Shared.Grpc.Plugins;
using Hamstix.Haby.Shared.PluginsCore;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Hamstix.Haby.Server.Grpc;

[Authorize]
public class PluginsGrpcService : PluginsService.PluginsServiceBase
{
    readonly Services.IPluginsService _pluginsService;
    readonly HabbyContext _context;

    public PluginsGrpcService(
        HabbyContext context,
        Services.IPluginsService pluginsService
        )
    {
        _context = context;
        _pluginsService = pluginsService;
    }

    public override async Task<PluginsResponse> GetAll(PluginsRequest request, ServerCallContext context)
    {
        var plugins = _pluginsService.Plugins.ToList();
        PluginsResponse response;
        if (request.ShowOnlyUnused)
        {
            var usedPlugins = await _context
                .Services
                .Where(x => x.PluginName != null)
                .Select(x => x.PluginName)
                .ToListAsync();

            var unusedPlugins = plugins.Where(x => !usedPlugins.Contains(x.Name)).ToList();
            response = unusedPlugins.ApplyFieldMask<Plugin, PluginsResponse, PluginModel>(
                request.FieldMask, (response, item) => response.Plugins.Add(item));
        }
        else
            response = plugins.ApplyFieldMask<Plugin, PluginsResponse, PluginModel>(
                request.FieldMask, (response, item) => response.Plugins.Add(item));

        return response;
    }
}