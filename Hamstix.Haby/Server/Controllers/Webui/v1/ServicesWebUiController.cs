using AutoMapper;
using AutoMapper.QueryableExtensions;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Shared.Api.WebUi.v1.Plugins;
using Hamstix.Haby.Shared.Api.WebUi.v1.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monq.Core.MvcExtensions.Validation;
using Monq.Core.MvcExtensions.ViewModels;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Controllers.Webui.v1
{
    [Route("[area]/services")]
    public class ServicesWebUiController : WebUiV1Controller
    {
        readonly HabbyContext _context;
        readonly IMapper _mapper;
        readonly IPluginsService _pluginsService;

        public ServicesWebUiController(
            HabbyContext context,
            IMapper mapper,
            IPluginsService pluginsService
            )
        {
            _pluginsService = pluginsService;
            _context = context;
            _mapper = mapper;
        }

        /// <summary>
        /// Get all configured services.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<ServicePreviewViewModel>>> GetAll()
        {
            var services = await _context.Services
                .AsNoTracking()
                .ProjectTo<ServicePreviewViewModel>(_mapper.ConfigurationProvider)
                .ToListAsync();

            return services;
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<ServiceViewModel>> Get(long id)
        {
            var service = await _context.Services.FirstOrDefaultAsync(x => x.Id == id);

            if (service is null)
                return NotFound(new ErrorResponseViewModel($"The service id {id} is not found."));

            return _mapper.Map<ServiceViewModel>(service);
        }

        /// <summary>
        /// Create new service.
        /// </summary>
        /// <param name="value">New service model.</param>
        /// <returns></returns>
        [HttpPost]
        [ValidateActionParameters]
        public async Task<ActionResult<ServiceViewModel>> PostNewService([FromBody] ServicePostViewModel value)
        {
            if (await _context.Services.AnyAsync(x => x.Name == value.Name.Trim()))
                return BadRequest(new ErrorResponseViewModel($"The service name {value.Name} has already taken."));

            JsonObject jsonObject;
            try
            {
                jsonObject = JsonObject.Parse(value.JsonConfig)?.AsObject() ?? new JsonObject();
            }
            catch (Exception e)
            {
                return BadRequest(new ErrorResponseViewModel($"Can't parse json configuration. Details: {e.Message}"));
            }

            var service = new Service(value.Name.Trim())
            {
                JsonConfig = jsonObject,
                Template = value.Template
            };

            if (value.PluginName is not null)
            {
                if (!_pluginsService.Plugins.Any(x => x.Name == value.PluginName))
                    return BadRequest(new ErrorResponseViewModel($"Can't find plugin \"{value.PluginName}\" " +
                        $"in the registered plugins."));

                service.PluginName = value.PluginName;
            }

            _context.Services.Add(service);
            await _context.SaveChangesAsync();

            return _mapper.Map<ServiceViewModel>(service);
        }

        /// <summary>
        /// Update service.
        /// </summary>
        /// <param name="value">Updated service model.</param>
        /// <returns></returns>
        [HttpPut("{id}")]
        [ValidateActionParameters]
        public async Task<ActionResult<ServiceViewModel>> UpdateService(long id, [FromBody] ServicePutViewModel value)
        {
            var service = await _context.Services.FirstOrDefaultAsync(x => x.Id == id);

            if (service is null)
                return NotFound(new ErrorResponseViewModel($"The service id {id} is not found."));

            if (await _context.Services.AnyAsync(x => x.Name == value.Name.Trim() && x.Id != id))
                return BadRequest(new ErrorResponseViewModel($"The service name {value.Name} has already taken."));

            JsonObject jsonObject;
            try
            {
                jsonObject = JsonObject.Parse(value.JsonConfig)?.AsObject() ?? new JsonObject();
            }
            catch (Exception e)
            {
                return BadRequest(new ErrorResponseViewModel($"Can't parse json configuration. Details: {e.Message}"));
            }

            if ((value.PluginName is not null) && !_pluginsService.Plugins.Any(x => x.Name == value.PluginName))
                return BadRequest(new ErrorResponseViewModel($"Can't find plugin \"{value.PluginName}\" " +
                    $"in the registered plugins."));

            if ((value.PluginName is not null) && !await PluginAvailiable(value.PluginName, service))
                return BadRequest(new ErrorResponseViewModel($"The plugin \"{value.PluginName}\" " +
                    $"has already taken by another service."));

            service.Name = value.Name.Trim();
            service.JsonConfig = jsonObject;
            service.Template = value.Template;
            service.PluginName = value.PluginName;

            await _context.SaveChangesAsync();

            return _mapper.Map<ServiceViewModel>(service);
        }

        [HttpDelete("{id}")]
        public async Task Delete(long id)
        {
            var service = await _context.Services.FirstOrDefaultAsync(x => x.Id == id);

            if (service is not null)
            {
                _context.Services.Remove(service);

                await _context.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Get all registered plugins.
        /// </summary>
        /// <returns></returns>
        [HttpGet("plugins")]
        public async Task<ActionResult<IEnumerable<PluginPreviewViewModel>>> GetAvailablePlugins()
        {
            var plugins = _pluginsService.Plugins.ToList();

            var usedPlugins = await _context
                .Services
                .Where(x => x.PluginName != null)
                .Select(x => x.PluginName)
                .ToListAsync();

            var resultPlugins = plugins.Where(x => !usedPlugins.Contains(x.Name)).ToList();

            return _mapper.Map<List<PluginPreviewViewModel>>(resultPlugins);
        }

        async Task<bool> PluginAvailiable(string pluginName, Service service) => 
            !await _context
                .Services
                .AnyAsync(x => x.PluginName == pluginName && x.Id != service.Id);
    }
}
