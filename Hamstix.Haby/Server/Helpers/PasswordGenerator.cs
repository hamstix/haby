namespace Hamstix.Haby.Server.Helpers
{
    public static class PasswordGenerator
    {
        /// <summary>
        /// Generates a Random Password
        /// respecting the given strength requirements.
        /// </summary>
        /// <param name="requiredLength">Length of the required.</param>
        /// <param name="requiredUniqueChars">The required unique chars.</param>
        /// <param name="requireDigit">if set to <c>true</c> [require digit].</param>
        /// <param name="requireNonAlphanumeric">if set to <c>true</c> [the password will include non alphanumeric symbols].</param>
        /// <param name="requireLowercase">if set to <c>true</c> [the password will contain lowercased characters].</param>
        /// <param name="requireUppercase">if set to <c>true</c> [the password will contain uppercased characters].</param>
        /// <returns>
        /// A random password
        /// </returns>
        public static string Generate(
            int requiredLength = 8,
            int requiredUniqueChars = 0,
            bool requireDigit = true,
            bool requireNonAlphanumeric = true,
            bool requireLowercase = true,
            bool requireUppercase = true)
        {
            var randomChars = new[] {
            "ABCDEFGHJKLMNOPQRSTUVWXYZ",    // uppercase 
            "abcdefghijkmnopqrstuvwxyz",    // lowercase
            "0123456789",                   // digits
            "!@$?_-"                        // non-alphanumeric
            };
            var rand = new Random(Environment.TickCount);
            var chars = new List<char>();

            if (requireUppercase)
                chars.Insert(rand.Next(0, chars.Count),
                    randomChars[0][rand.Next(0, randomChars[0].Length)]);

            if (requireLowercase)
                chars.Insert(rand.Next(0, chars.Count),
                    randomChars[1][rand.Next(0, randomChars[1].Length)]);

            if (requireDigit)
                chars.Insert(rand.Next(0, chars.Count),
                    randomChars[2][rand.Next(0, randomChars[2].Length)]);

            if (requireNonAlphanumeric)
                chars.Insert(rand.Next(0, chars.Count),
                    randomChars[3][rand.Next(0, randomChars[3].Length)]);

            for (var i = chars.Count; i < requiredLength
                || chars.Distinct().Count() < requiredUniqueChars; i++)
            {
                var rcs = randomChars[rand.Next(0, randomChars.Length)];
                chars.Insert(rand.Next(0, chars.Count),
                    rcs[rand.Next(0, rcs.Length)]);
            }

            return new string(chars.ToArray());
        }
    }
}
