using Microsoft.EntityFrameworkCore;
namespace MagicMirror.Data;

/// <summary>
/// Application database context.
/// Extend with DbSet&lt;T&gt; properties for your domain models.
/// Provider is configured in Program.cs via builder.Configuration.
/// </summary>
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    // Add your DbSets here:
    // public DbSet<Product> Products => Set<Product>();
    // public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Configure entity relationships, indexes, and seed data here.
    }
}