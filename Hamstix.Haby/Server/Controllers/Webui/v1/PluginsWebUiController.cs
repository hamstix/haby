using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Shared.Api.WebUi.v1.Plugins;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hamstix.Haby.Server.Controllers.Webui.v1
{
    [Authorize]
    [Route("[area]/plugins")]
    public class PluginsWebUiController : WebUiV1Controller
    {
        readonly IPluginsService _pluginsService;

        public PluginsWebUiController(
            IPluginsService pluginsService
            )
        {
            _pluginsService = pluginsService;
        }

        /// <summary>
        /// Get all registered plugins.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ActionResult<IEnumerable<PluginPreviewViewModel>> GetAll()
        {
            var plugins = _pluginsService.Plugins.ToList();

            return plugins.Adapt<List<PluginPreviewViewModel>>();
        }
    }
}
