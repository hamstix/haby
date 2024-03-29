﻿using Hamstix.Haby.Server.Models;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Configurator
{
    public interface IServiceConfigurator
    {
        /// <summary>
        /// Configure the configuration unit at service using service handlers.
        /// </summary>
        /// <param name="service">Service.</param>
        /// <param name="renderedTemplate">Rendered template.</param>
        /// <param name="variables">The variables merges service, and cu list.</param>
        /// <returns></returns>
        Task<Shared.PluginsCore.ConfigurationResult> Configure(Service service, JsonNode renderedTemplate, JsonObject variables);
    }
}
