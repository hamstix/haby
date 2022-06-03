using AutoMapper;
using AutoMapper.QueryableExtensions;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits;
using Hamstix.Haby.Shared.Api.WebUi.v1.Keys;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monq.Core.MvcExtensions.ViewModels;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Controllers.Webui.v1
{
    [Route("[area]/configuration")]
    public class ConfigurationWebUiController : WebUiV1Controller
    {
        readonly HabbyContext _context;
        readonly IMapper _mapper;

        public ConfigurationWebUiController(
            HabbyContext context,
            IMapper mapper
            )
        {
            _context = context;
            _mapper = mapper;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<ConfigurationUnitPreviewViewModel>>> GetConfigurationUnits()
        {
            return await _context
                .ConfigurationUnits
                .AsNoTracking()
                .ProjectTo<ConfigurationUnitPreviewViewModel>(_mapper.ConfigurationProvider)
                .ToListAsync();
        }

        [HttpGet("{configurationUnitName}/keys")]
        public async Task<ActionResult<IEnumerable<ConfigurationKeyPreviewViewModel>>> GetKeys(string configurationUnitName)
        {
            return await _context
                .ConfigurationUnits
                .AsNoTracking()
                .Where(x => x.Name == configurationUnitName)
                .SelectMany(x => x.Keys)
                .ProjectTo<ConfigurationKeyPreviewViewModel>(_mapper.ConfigurationProvider)
                .ToListAsync();
        }

        [HttpGet("{configurationUnitName}/keys/{keyName}/config")]
        public async Task<ActionResult<ConfigurationCodeViewModel>> GetKeyConfiguration(string configurationUnitName,
            string keyName)
        {
            var configuration = await _context.ConfigurationKeys
                .FirstOrDefaultAsync(x => x.ConfigurationUnit.Name == configurationUnitName && x.Name == keyName);

            if (configuration is null)
                return BadRequest(new ErrorResponseViewModel("The configuration key is not found."));

            return new ConfigurationCodeViewModel { Configuration = configuration.Configuration };
        }

        [HttpPut("{configurationUnitName}/keys/{keyName}/config")]
        public async Task<ActionResult<ConfigurationCodeViewModel>> UpdateConfiguration(string configurationUnitName,
            string keyName,
            [FromBody] ConfigurationCodePutViewModel value)
        {
            var configuration = await _context.ConfigurationKeys
                .FirstOrDefaultAsync(x => x.ConfigurationUnit.Name == configurationUnitName && x.Name == keyName);

            if (configuration is null)
                return BadRequest(new ErrorResponseViewModel("The configuration key is not found."));

            JsonNode jsonNode;
            try
            {
                jsonNode = JsonNode.Parse(value.Configuration) ?? new JsonObject();
            }
            catch (Exception e)
            {
                return BadRequest(new ErrorResponseViewModel($"Can't parse json configuration. Details: {e.Message}"));
            }

            configuration.Configuration = jsonNode;
            await _context.SaveChangesAsync();

            return new ConfigurationCodeViewModel { Configuration = jsonNode };
        }

        /// <summary>
        /// Regenerate the default configuration unit template.
        /// </summary>
        /// <param name="configurationUnitName">The configuration unit name.</param>
        /// <param name="keyName">The configuration unit configuration key name.</param>
        /// <returns></returns>
        [HttpDelete("{configurationUnitName}/keys/{keyName}/config")]
        public async Task<ActionResult<ConfigurationCodeViewModel>> RegenerateConfig(string configurationUnitName,
            string keyName)
        {
            var configuration = await _context.ConfigurationKeys
                .FirstOrDefaultAsync(x => x.ConfigurationUnit.Name == configurationUnitName && x.Name == keyName);

            if (configuration is null)
                return BadRequest(new ErrorResponseViewModel("The configuration key is not found."));


            configuration.Configuration = new JsonObject();
            await _context.SaveChangesAsync();

            return new ConfigurationCodeViewModel { Configuration = configuration.Configuration };
        }
    }
}
