using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Source.Infrastructure;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c>. The real host refuses
/// to boot without a strong <c>Jwt:Key</c> (correctly!), which used to make
/// migration scaffolding depend on a fully-configured environment. Migrations only
/// need the MODEL, not a live database or secrets — so this factory hands EF a
/// context wired to a dummy Npgsql connection string that is never opened.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only;Username=design;Password=design")
            .Options;

        return new ApplicationDbContext(options, httpContextAccessor: null);
    }
}
