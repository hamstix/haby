using Hamstix.Haby.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Hamstix.Haby.Server.Controllers.Webui.v1
{
    [Route("[area]/acls")]
    public class AclsController : WebUiV1Controller
    {
        readonly IHttpClientFactory _httpClientFactory;

        public AclsController(
            IHttpClientFactory httpClientFactory
            )
        {
            _httpClientFactory = httpClientFactory;
        }

        [HttpPost("check-token")]
        public async Task<AuthResultModel> CheckToken([FromBody] AclModel value)
        {
            return new AuthResultModel { IsAuthSuccessful = true };
        }
    }
}
