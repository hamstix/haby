using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Shared;
using Hamstix.Haby.Shared.Api.WebUi.v1.RegistryStatuses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hamstix.Haby.Server.Controllers.Webui.v1
{
    [Route("[area]")]
    public class RegistryStatusWebUiController : WebUiV1Controller
    {
        readonly Services.ISchemaInitializer _schemaInitializer;
        readonly IWebHostEnvironment _env;
        readonly DbContextOptions<HabbyContext> _contextOptions;

        public RegistryStatusWebUiController(
            DbContextOptions<HabbyContext> contextOptions,
            Services.ISchemaInitializer schemaInitializer,
            IWebHostEnvironment env
            )
        {
            _contextOptions = contextOptions;
            _env = env;
            _schemaInitializer = schemaInitializer;
        }

        /// <summary>
        /// Get registry status.
        /// </summary>
        /// <returns></returns>
        [HttpGet("status")]
        public async Task<RegStatusViewModel> GetStatus()
        {
            // Try connect to Database.
            try
            {
                await CheckDatabaseConnection();
            }
            catch (Exception e)
            {
                return new RegStatusViewModel
                {
                    DbSchemaInitialized = false,
                    Message = e.Message,
                    Status = RegStatuses.Error
                };
            }

            var model = await BuildStatusModel();

            // Exit early if the database has not yet been configured to prevent an error.
            if (!model.DbSchemaInitialized)
                return model;

            model.Status = RegStatuses.Initialized;

            return model;
        }

        /// <summary>
        /// Initialize registry database.
        /// </summary>
        /// <returns></returns>
        [HttpPost("initialize")]
        public async Task<RegStatusViewModel> InitializeSchema()
        {
            var model = await BuildStatusModel();
            try
            {
                if (!await _schemaInitializer.IsSchemaInitialized())
                {
                    await _schemaInitializer.InitializeSchema();
                }

                model.DbSchemaInitialized = true;
            }
            catch (Exception e)
            {
                model.DbSchemaInitialized = false;
                model.Message = e.Message;
            }

            await CheckDatabaseConnection();
            model.Status = RegStatuses.Initialized;

            return model;
        }

        async Task CheckDatabaseConnection()
        {
            using var context = new HabbyContext(_contextOptions);
            await context.RegConfiguration.AnyAsync();
        }

        async Task<RegStatusViewModel> BuildStatusModel()
        {
            var model = new RegStatusViewModel
            {
                Version = Monq.Core.BasicDotNetMicroservice.Helpers.MicroserviceInfo.GetEntryPointAssembleVersion(),
                Status = RegStatuses.NotInitialized,
                DbSchemaInitialized = await _schemaInitializer.IsSchemaInitialized(),
                Environment = _env.EnvironmentName.ToLower(),
                ApiVersion = AppConstants.CurrentApiVersion.ToString()
            };

            return model;
        }
    }
}
