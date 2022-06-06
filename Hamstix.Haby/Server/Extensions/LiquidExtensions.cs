using Fluid;
using Hamstix.Haby.Server.Configurator;

namespace Hamstix.Haby.Server.Extensions
{
    public static class LiquidExtensions
    {
        public static void SetCommonFunctions(this TemplateContext context)
        {
            var regex = CommonLiquidFunctions.Regex;

            var password = CommonLiquidFunctions.Password;

            context.SetValue("regexReplace", regex);
            context.SetValue("password", password);
        }
    }
}
