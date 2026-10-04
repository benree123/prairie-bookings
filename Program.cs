using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PrairieBookings.Data;
using PrairieBookings.Models;
using PrairieBookings;

var builder = WebApplication.CreateBuilder(args);
StaffEndpoints.Configure(builder);
// Console logging works locally, in containers and on hosted services.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
builder.Services.AddDbContext<BookingDb>(options =>
{
    if (builder.Configuration["DatabaseProvider"] == "SqlServer") options.UseSqlServer(builder.Configuration.GetConnectionString("Bookings"));
    else options.UseSqlite(builder.Configuration.GetConnectionString("Bookings"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("staff-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("writes", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BookingDb>();
    db.Database.EnsureCreated();
    await StaffEndpoints.UpgradeSchema(db);
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
StaffEndpoints.Map(app);

app.MapGet("/api/catalog", () => Results.Ok(new
{
    services = Schedule.Services, providers = Schedule.Providers,
    timezone = "America/Edmonton", today = Schedule.Now.ToString("yyyy-MM-dd"),
    lastDate = Schedule.Now.Date.AddDays(30).ToString("yyyy-MM-dd")
}));

app.MapGet("/api/availability", async (int providerId, string date, BookingDb db) =>
{
    if (!Schedule.Providers.Any(x => x.Id == providerId) || !Schedule.TryDate(date, out var day) || !Schedule.ValidDay(day))
        return Results.BadRequest(new { error = "Choose a valid provider and a date within the next 30 days." });
    var nextDay = day.AddDays(1);
    var reserved = await db.Bookings.Where(x => x.ProviderId == providerId && !x.IsCancelled && x.StartsAt >= day && x.StartsAt < nextDay)
        .Select(x => x.StartsAt).ToListAsync();
    var slots = Schedule.Slots(day).Select(time => new { startsAt = time.ToString("yyyy-MM-dd'T'HH:mm:ss"), available = !reserved.Contains(time) });
    return Results.Ok(new { slots });
});

app.MapPost("/api/bookings", async (BookingRequest request, BookingDb db) =>
{
    if (!Schedule.Providers.Any(x => x.Id == request.ProviderId) || !Schedule.Services.Any(x => x.Id == request.ServiceId))
        return Results.BadRequest(new { error = "Choose a listed service and provider." });
    if (!DateTime.TryParseExact(request.StartsAt, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
        || !Schedule.ValidDay(start.Date) || !Schedule.Slots(start.Date).Contains(start))
        return Results.BadRequest(new { error = "Choose a future weekday slot between 9 AM and 5 PM, within the next 30 days." });
    var name = request.CustomerName?.Trim() ?? "";
    var email = request.Email?.Trim() ?? "";
    if (name.Length is < 2 or > 100 || email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email)
        return Results.BadRequest(new { error = "Enter a name of 2–100 characters and a valid email address." });
    var reference = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    var booking = new Booking { ProviderId = request.ProviderId, ServiceId = request.ServiceId, StartsAt = start,
        CustomerName = name, Email = email, ReferenceHash = Schedule.Hash(reference) };
    db.Bookings.Add(booking);
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 } or SqliteException { SqliteErrorCode: 19 })
    { return Results.Conflict(new { error = "That slot was just booked. Choose another time." }); }
    return Results.Json(new { reference, startsAt = start.ToString("yyyy-MM-dd'T'HH:mm:ss"), request.ProviderId, request.ServiceId }, statusCode: 201);
}).RequireRateLimiting("writes");

app.MapPost("/api/bookings/cancel", async (CancellationRequest request, BookingDb db) =>
{
    var reference = request.Reference?.Trim().ToUpperInvariant() ?? "";
    if (reference.Length != 32 || !reference.All(Uri.IsHexDigit))
        return Results.BadRequest(new { error = "Enter the 32-character reference from your booking confirmation." });
    var hash = Schedule.Hash(reference);
    var booking = await db.Bookings.SingleOrDefaultAsync(x => x.ReferenceHash == hash);
    if (booking is null) return Results.NotFound(new { error = "Booking reference not found." });
    if (booking.IsBlocked) return Results.NotFound(new { error = "Booking reference not found." });
    if (booking.IsCancelled) return Results.Ok(new { message = "This appointment is already cancelled." });
    if (booking.StartsAt <= Schedule.Now) return Results.BadRequest(new { error = "Past appointments cannot be cancelled." });
    booking.IsCancelled = true;
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Appointment cancelled. The slot is available again." });
}).RequireRateLimiting("writes");

app.Run();

internal static class Schedule
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Edmonton");
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public static readonly Service[] Services = [
        new(1, "Discovery consultation", "Discuss your goals and find the right next step.", 30),
        new(2, "Project review", "Get focused feedback on your project or plan.", 30),
        new(3, "Follow-up session", "Check progress and work through your questions.", 30)];
    public static readonly Provider[] Providers = [new(1, "Alex Morgan", "Client advisor"), new(2, "Jordan Lee", "Project specialist"), new(3, "Sam Patel", "Client advisor")];
    public static bool TryDate(string value, out DateTime date) => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    public static bool ValidDay(DateTime date) => date >= Now.Date && date <= Now.Date.AddDays(30);
    public static IEnumerable<DateTime> Slots(DateTime day)
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) yield break;
        for (var time = day.AddHours(9); time < day.AddHours(17); time = time.AddMinutes(30))
            if (time > Now) yield return time;
    }
    public static string Hash(string reference) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference)));
}
