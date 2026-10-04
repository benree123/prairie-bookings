using Microsoft.EntityFrameworkCore;
using PrairieBookings.Models;

namespace PrairieBookings.Data;

public sealed class BookingDb(DbContextOptions<BookingDb> options) : DbContext(options)
{
    public DbSet<Booking> Bookings => Set<Booking>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        var booking = model.Entity<Booking>();
        booking.Property(x => x.IsBlocked).HasDefaultValue(false);
        booking.Property(x => x.CustomerName).HasMaxLength(100).IsRequired();
        booking.Property(x => x.Email).HasMaxLength(254).IsRequired();
        booking.Property(x => x.ReferenceHash).HasMaxLength(64).IsRequired();
        // Concurrent requests cannot reserve the same provider and time.
        // A cancellation frees the slot without deleting booking history.
        booking.HasIndex(x => new { x.ProviderId, x.StartsAt }).IsUnique().HasFilter("[IsCancelled] = 0");
        booking.HasIndex(x => x.ReferenceHash).IsUnique();
    }
}
