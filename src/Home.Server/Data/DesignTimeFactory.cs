using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Home.Server.Data;

/// <summary>Used only by `dotnet ef migrations add`.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<HomeDb>
{
    public HomeDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HomeDb>().UseSqlite("Data Source=design.db").Options);
}
