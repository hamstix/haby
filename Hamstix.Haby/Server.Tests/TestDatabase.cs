using Hamstix.Haby.Server.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Hamstix.Haby.Server.Tests;

sealed class TestDatabase
{
    readonly string _name = Guid.NewGuid().ToString();

    public HabbyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<HabbyContext>()
            .UseInMemoryDatabase(_name)
            .Options;

        return new HabbyContext(options);
    }
}
