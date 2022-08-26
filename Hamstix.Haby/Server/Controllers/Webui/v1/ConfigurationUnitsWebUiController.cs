using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Configurator;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits;
using Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits.Variables;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monq.Core.MvcExtensions.Validation;
using Monq.Core.MvcExtensions.ViewModels;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Controllers.Webui.v1
{
    [Authorize]
    [Route("[area]/configuration-units")]
    public class ConfigurationUnitsWebUiController : WebUiV1Controller
    {
        readonly HabbyContext _context;
        readonly ICuConfigurator _configurator;

        public ConfigurationUnitsWebUiController(
            HabbyContext context,
            ICuConfigurator configurator
            )
        {
            _configurator = configurator;
            _context = context;
        }

        /// <summary>
        /// Get all installed configuration units.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<ConfigurationUnitPreviewViewModel>>> GetAll()
        {
            var configurationUnits = await _context.ConfigurationUnits
                .AsNoTracking()
                .ProjectToType<ConfigurationUnitPreviewViewModel>()
                .ToListAsync();

            return configurationUnits;
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<ConfigurationUnitViewModel>> Get(long id)
        {
            var configurationUnit = await _context.ConfigurationUnits.FirstOrDefaultAsync(x => x.Id == id);

            if (configurationUnit is null)
                return NotFound(new ErrorResponseViewModel($"The configuration unit id {id} is not found."));

            return configurationUnit.Adapt<ConfigurationUnitViewModel>();
        }

        [HttpPost]
        [ValidateActionParameters]
        public async Task<ActionResult<ConfigurationUnitViewModel>> Add(
            [FromBody] ConfigurationUnitPutViewModel value)
        {
            if (await _context.ConfigurationUnits.AnyAsync(x => x.Name == value.Name.Trim()))
                return BadRequest(new ErrorResponseViewModel($"The configuration unit name {value.Name} has already taken."));

            JsonArray jsonObject;
            try
            {
                jsonObject = JsonArray.Parse(value.Template)?.AsArray() ?? new JsonArray();
            }
            catch (Exception e)
            {
                return BadRequest(new ErrorResponseViewModel($"Can't parse json configuration. It must be an array. Details: {e.Message}"));
            }

            var cu = new ConfigurationUnit(value.Name.Trim(), value.Version.Trim())
            {
                Template = jsonObject
            };

            _context.ConfigurationUnits.Add(cu);

            await _context.SaveChangesAsync();

            return cu.Adapt<ConfigurationUnitViewModel>();
        }

        [HttpPut("{id}")]
        [ValidateActionParameters]
        public async Task<ActionResult<ConfigurationUnitViewModel>> Update(long id,
            [FromBody] ConfigurationUnitPutViewModel value)
        {
            var configurationUnit = await _context.ConfigurationUnits.FirstOrDefaultAsync(x => x.Id == id);

            if (configurationUnit is null)
                return NotFound(new ErrorResponseViewModel($"The configuration unit id {id} is not found."));

            if (await _context.Services.AnyAsync(x => x.Name.ToLower() == value.Name.ToLower() && x.Id != id))
                return BadRequest(new ErrorResponseViewModel($"The service name {value.Name} already taken."));

            JsonArray jsonObject;
            try
            {
                jsonObject = JsonArray.Parse(value.Template)?.AsArray() ?? new JsonArray();
            }
            catch (Exception e)
            {
                return BadRequest(new ErrorResponseViewModel($"Can't parse json configuration. It must be an array. Details: {e.Message}"));
            }
            configurationUnit.Template = jsonObject;
            configurationUnit.Name = value.Name;
            configurationUnit.Version = value.Version;

            await _context.SaveChangesAsync();

            return configurationUnit.Adapt<ConfigurationUnitViewModel>();
        }

        [HttpDelete("{id}")]
        public async Task Delete(long id)
        {
            var configurationUnit = await _context.ConfigurationUnits.FirstOrDefaultAsync(x => x.Id == id);

            if (configurationUnit is not null)
            {
                _context.ConfigurationUnits.Remove(configurationUnit);

                await _context.SaveChangesAsync();
            }
        }

        [HttpPost("{id}/reconfigure")]
        public async Task<ActionResult<ConfigurationUnitResultsViewModel>> Reconfigure(long id)
        {
            if (!await _context.ConfigurationUnits.AnyAsync(x => x.Id == id))
                return NotFound(new ErrorResponseViewModel($"The configuration unit id {id} is not found."));

            var results = await _configurator.Configure(id);

            var configurationUnit = await _context.ConfigurationUnits.FirstAsync(x => x.Id == id);

            var result = new ConfigurationUnitResultsViewModel
            {
                ConfigurationUnit = configurationUnit.Adapt<ConfigurationUnitViewModel>(),
                Results = results.Adapt<IEnumerable<ConfigurationUnitKeyResultViewModel>>()
            };

            return result;
        }

        [HttpGet("{id}/variables")]
        public async Task<ActionResult<IEnumerable<ConfigurationUnitVariableViewModel>>> GetVariables(long id)
        {
            if (!await _context.ConfigurationUnits.AnyAsync(x => x.Id == id))
                return NotFound(new ErrorResponseViewModel($"The configuration unit id {id} is not found."));

            return await _context
                .Variables
                .Include(x => x.ConfigurationUnitAtService)
                    .ThenInclude(x => x.Service)
                .Where(x => x.ConfigurationUnitId == id)
                .ProjectToType<ConfigurationUnitVariableViewModel>()
                .ToListAsync();
        }

        [HttpDelete("{id}/variables")]
        public async Task<ActionResult<IEnumerable<ConfigurationUnitVariableViewModel>>> DeleteVariable(long id,
            string name, string key, long serviceId)
        {
            if (!await _context.ConfigurationUnits.AnyAsync(x => x.Id == id))
                return NotFound(new ErrorResponseViewModel($"The configuration unit id {id} is not found."));

            var variable = await _context
                .Variables
                .FirstOrDefaultAsync(x => x.Name == name && x.ServiceId == serviceId && x.Key == key);
            if (variable is not null)
            {
                _context.Variables.Remove(variable);
                await _context.SaveChangesAsync();
            }

            return NoContent();
        }
    }
}
