using AutoMapper;
using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Shared.Api.WebUi.v1.Plugins;
using Microsoft.AspNetCore.Mvc;

namespace Hamstix.Haby.Server.Controllers.Webui.v1
{
    [Route("[area]/plugins")]
    public class PluginsWebUiController : WebUiV1Controller
    {
        readonly IPluginsService _pluginsService;
        readonly IMapper _mapper;

        public PluginsWebUiController(
            IPluginsService pluginsService,
            IMapper mapper
            )
        {
            _pluginsService = pluginsService;
            _mapper = mapper;
        }

        /// <summary>
        /// Get all registered plugins.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ActionResult<IEnumerable<PluginPreviewViewModel>> GetAll()
        {
            var plugins = _pluginsService.Plugins.ToList();

            return _mapper.Map<List<PluginPreviewViewModel>>(plugins);
        }
    }
}
