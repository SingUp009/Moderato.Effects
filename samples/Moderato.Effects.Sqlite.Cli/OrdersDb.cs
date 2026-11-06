using Microsoft.EntityFrameworkCore;

public sealed class Order
{
    public int Id { get; set; }
    public required string Name { get; set; }
}

public sealed class OrdersDb(DbContextOptions<OrdersDb> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Order>().Property(order => order.Id).ValueGeneratedNever();
}

internal sealed class OrdersDbFactory(DbContextOptions<OrdersDb> options) : IDbContextFactory<OrdersDb>
{
    public OrdersDb CreateDbContext() => new(options);
}
