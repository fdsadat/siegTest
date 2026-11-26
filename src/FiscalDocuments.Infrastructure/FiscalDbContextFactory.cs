using FiscalDocuments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FiscalDocuments.Infrastructure;

public sealed class FiscalDbContextFactory : IDesignTimeDbContextFactory<FiscalDbContext>
{
    public FiscalDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=FiscalDocuments;Integrated Security=true;TrustServerCertificate=true";
        return new FiscalDbContext(new DbContextOptionsBuilder<FiscalDbContext>().UseSqlServer(connection).Options);
    }
}
