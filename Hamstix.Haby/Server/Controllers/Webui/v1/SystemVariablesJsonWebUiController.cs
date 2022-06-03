using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Shared.Api.WebUi.v1.Keys;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monq.Core.MvcExtensions.ViewModels;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Controllers.Webui.v1
{
    [Route("[area]/application/system-variables/json")]
    public class SystemVariablesJsonWebUiController : WebUiV1Controller
    {
        readonly HabbyContext _context;

        public SystemVariablesJsonWebUiController(
            HabbyContext context
            )
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<JsonSystemVariablesViewModel>> GetConfiguration()
        {
            var systemVariables = await _context.SystemVariables
                .AsNoTracking()
                .ToListAsync();

            var configuration = new JsonObject();
            foreach (var systemVariable in systemVariables)
            {
                configuration.Add(systemVariable.Name, systemVariable.Value.CloneJsonNode());
            }

            return new JsonSystemVariablesViewModel { Configuration = configuration };
        }

        [HttpPut]
        public async Task<ActionResult<JsonSystemVariablesViewModel>> UpdateConfiguration(
            [FromBody] JsonSystemVariablesPutViewModel value)
        {
            JsonObject? jsonObject;
            try
            {
                jsonObject = JsonNode.Parse(value.Configuration)?.AsObject();
            }
            catch (Exception e)
            {
                return BadRequest(new ErrorResponseViewModel($"Can't parse json configuration. " +
                    $"It must be valid json object. Details: {e.Message}"));
            }
            if (jsonObject is null)
                return BadRequest(new ErrorResponseViewModel($"Can't parse json configuration. " +
                    $"It must be valid json object."));

            var dbVariables = await _context.SystemVariables.ToListAsync();

            // Parse json object to variables.
            foreach (var jsonItem in jsonObject)
            {
                var dbVariable = dbVariables.FirstOrDefault(x => x.Name == jsonItem.Key);
                if (dbVariable is not null)
                    dbVariable.Value = jsonItem.Value.CloneJsonNode();
                else
                    _context.SystemVariables.Add(new Models.SystemVariable(jsonItem.Key, jsonItem.Value != null ?
                        jsonItem.Value.CloneJsonNode() : JsonValue.Create(string.Empty)!));
            }

            foreach (var dbVariable in dbVariables)
            {
                if (!jsonObject.ContainsKey(dbVariable.Name))
                    _context.SystemVariables.Remove(dbVariable);
            }

            await _context.SaveChangesAsync();

            return new JsonSystemVariablesViewModel { Configuration = jsonObject };
        }
    }
}
