namespace PrairieBookings.Models;

public sealed class Booking
{
    public int Id { get; set; }
    public int ProviderId { get; set; }
    public int ServiceId { get; set; }
    public DateTime StartsAt { get; set; }
    public string CustomerName { get; set; } = "";
    public string Email { get; set; } = "";
    public string ReferenceHash { get; set; } = "";
    public bool IsCancelled { get; set; }
    public bool IsBlocked { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public record BookingRequest(int ProviderId, int ServiceId, string StartsAt, string CustomerName, string Email);
public record CancellationRequest(string Reference);
public record Service(int Id, string Name, string Description, int Minutes);
public record Provider(int Id, string Name, string Role);
