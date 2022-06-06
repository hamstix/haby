using Fluid.Values;
using Hamstix.Haby.Server.Helpers;

namespace Hamstix.Haby.Server.Configurator
{
    public static class CommonLiquidFunctions
    {
        static FunctionValue _regex = new FunctionValue((args, context) =>
        {
            var input = args.At(0).ToStringValue();
            var pattern = args.At(1).ToStringValue();
            var replacement = args.At(2).ToStringValue();

            var result = System.Text.RegularExpressions.Regex.Replace(input, pattern, replacement);

            return new ValueTask<FluidValue>(new StringValue(result));
        });

        public static FunctionValue Regex => _regex;

        static FunctionValue _password = new FunctionValue((args, context) =>
        {
            var requiredLenght = (int)args.At(0).ToNumberValue();
            var requiredUniqueChars = (int)args.At(1).ToNumberValue();
            var requireDigits = args.At(2).ToBooleanValue();
            var requireNonAlphanumeric = args.At(3).ToBooleanValue();
            var requireLowercase = args.At(4).ToBooleanValue();
            var requireUppercase = args.At(5).ToBooleanValue();

            var result = PasswordGenerator.Generate(requiredLenght,
                           requiredUniqueChars,
                           requireDigits,
                           requireNonAlphanumeric,
                           requireLowercase,
                           requireUppercase);

            return new ValueTask<FluidValue>(new StringValue(result));
        });

        public static FunctionValue Password => _password;
    }
}
