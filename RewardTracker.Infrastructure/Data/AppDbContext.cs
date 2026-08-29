using Microsoft.EntityFrameworkCore;
using RewardTracker.Core.Entities;

namespace RewardTracker.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<RewardSite> RewardSites { get; set; }
    public DbSet<Account> Accounts { get; set; }
    public DbSet<PointLog> PointLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Osiguravamo da imena tabela budu jasna (opciono, ali dobra praksa)
        modelBuilder.Entity<RewardSite>().ToTable("RewardSites");
        modelBuilder.Entity<Account>().ToTable("Accounts");
        modelBuilder.Entity<PointLog>().ToTable("PointLogs");
    }
}
