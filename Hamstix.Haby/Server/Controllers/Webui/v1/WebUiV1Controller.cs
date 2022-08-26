using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hamstix.Haby.Server.Controllers.Webui.v1
{
    [Authorize]
    [Area("api/webui/haby/v1")]
    [ApiController]
    public abstract class WebUiV1Controller : ControllerBase
    {

    }
}
