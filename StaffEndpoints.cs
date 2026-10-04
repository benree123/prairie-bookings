using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PrairieBookings.Data;
using PrairieBookings.Models;

namespace PrairieBookings;

public static class StaffEndpoints
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var directory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        var password = builder.Configuration["Staff:Password"];
        if (string.IsNullOrEmpty(password))
        {
            var file = Path.Combine(directory, "staff-password.txt");
            if (!File.Exists(file)) File.WriteAllText(file, Convert.ToHexString(RandomNumberGenerator.GetBytes(20)));
            password = File.ReadAllText(file).Trim();
        }
        if (password.Length < 12) throw new InvalidOperationException("Staff password must have at least 12 characters.");
        builder.Services.AddSingleton(new StaffCredentials(builder.Configuration["Staff:Username"] ?? "staff", password));
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys"))).SetApplicationName("PrairieBookings");
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "PrairieBookings.Staff";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(1);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = async context =>
            {
                var credentials = context.HttpContext.RequestServices.GetRequiredService<StaffCredentials>();
                if (context.Principal?.Identity?.Name != credentials.Username
                    || context.Principal.FindFirst("credential-version")?.Value != credentials.SecurityStamp)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("Staff", policy => policy.RequireAuthenticatedUser().RequireRole("Staff")));
    }

    // Additive, idempotent upgrade for databases created by the original portfolio MVP.
    // No customer rows are recreated or deleted. New deployments receive this column through EnsureCreated.
    public static async Task UpgradeSchema(BookingDb db)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.OpenConnectionAsync();
            try
            {
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA table_info(Bookings)";
                bool exists = false;
                using (var reader = await command.ExecuteReaderAsync())
                    while (await reader.ReadAsync()) if (reader.GetString(1) == "IsBlocked") exists = true;
                if (!exists) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Bookings ADD COLUMN IsBlocked INTEGER NOT NULL DEFAULT 0");
            }
            finally { await db.Database.CloseConnectionAsync(); }
        }
        else
            await db.Database.ExecuteSqlRawAsync("IF COL_LENGTH('Bookings', 'IsBlocked') IS NULL ALTER TABLE Bookings ADD IsBlocked bit NOT NULL CONSTRAINT DF_Bookings_IsBlocked DEFAULT 0");
    }

    public static void Map(WebApplication app)
    {
        // A custom header plus same-origin CORS defaults prevents cross-site form submissions.
        // Applied to login as well as authenticated mutations to prevent login/logout CSRF.
        var group = app.MapGroup("/api/staff").AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Request.Method != "GET" && context.HttpContext.Request.Headers["X-Staff-Request"] != "1")
                return Results.BadRequest(new { error = "Missing staff request header." });
            return await next(context);
        });

        group.MapPost("/login", async (StaffLogin request, StaffCredentials credentials, HttpContext context) =>
        {
            if (!credentials.Matches(request.Username, request.Password)) return Results.Json(new { error = "Incorrect username or password." }, statusCode: 401);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, credentials.Username), new Claim(ClaimTypes.Role, "Staff"), new Claim("credential-version", credentials.SecurityStamp)], CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
            return Results.Ok(new { username = credentials.Username });
        }).RequireRateLimiting("staff-login");

        group.MapGet("/session", (HttpContext context) => Results.Ok(new { username = context.User.Identity!.Name })).RequireAuthorization("Staff");
        group.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { message = "Signed out." });
        }).RequireAuthorization("Staff");

        group.MapGet("/appointments", async (string from, string to, int? providerId, string? status, BookingDb db) =>
        {
            if (!Schedule.TryDate(from, out var start) || !Schedule.TryDate(to, out var end) || end < start || (end - start).TotalDays > 90)
                return Results.BadRequest(new { error = "Choose a date range of up to 90 days." });
            if (providerId is not null && !Schedule.Providers.Any(x => x.Id == providerId))
                return Results.BadRequest(new { error = "Choose a valid advisor." });
            if (status is not null && status is not ("all" or "active" or "cancelled" or "completed"))
                return Results.BadRequest(new { error = "Choose a valid status." });
            var nextDay = end.AddDays(1);
            var query = db.Bookings.AsNoTracking().Where(x => !x.IsBlocked && x.StartsAt >= start && x.StartsAt < nextDay);
            if (providerId is not null) query = query.Where(x => x.ProviderId == providerId);
            var now = Schedule.Now;
            var counts = new { total = await query.CountAsync(), upcoming = await query.CountAsync(x => !x.IsCancelled && x.StartsAt > now),
                cancelled = await query.CountAsync(x => x.IsCancelled), completed = await query.CountAsync(x => !x.IsCancelled && x.StartsAt <= now) };
            query = status switch
            {
                "active" => query.Where(x => !x.IsCancelled && x.StartsAt > now),
                "cancelled" => query.Where(x => x.IsCancelled),
                "completed" => query.Where(x => !x.IsCancelled && x.StartsAt <= now),
                _ => query
            };
            var totalMatches = await query.CountAsync();
            var records = await query.OrderBy(x => x.StartsAt).ThenBy(x => x.Id).Take(200)
                .Select(x => new { x.Id, x.CustomerName, x.Email, x.ProviderId, x.ServiceId, x.StartsAt, x.IsCancelled }).ToListAsync();
            var appointments = records.Select(x => new { x.Id, x.CustomerName, x.Email,
                provider = Schedule.Providers.Single(p => p.Id == x.ProviderId).Name,
                service = Schedule.Services.Single(s => s.Id == x.ServiceId).Name,
                startsAt = x.StartsAt.ToString("yyyy-MM-dd'T'HH:mm:ss"),
                status = x.IsCancelled ? "cancelled" : x.StartsAt <= now ? "completed" : "active" });
            return Results.Ok(new { counts, appointments, totalMatches });
        }).RequireAuthorization("Staff");

        group.MapPost("/appointments/{id:int}/cancel", async (int id, BookingDb db) =>
        {
            var booking = await db.Bookings.SingleOrDefaultAsync(x => x.Id == id && !x.IsBlocked);
            if (booking is null) return Results.NotFound(new { error = "Appointment not found." });
            if (booking.IsCancelled) return Results.Ok(new { message = "Appointment is already cancelled." });
            if (booking.StartsAt <= Schedule.Now) return Results.BadRequest(new { error = "Past appointments cannot be cancelled." });
            booking.IsCancelled = true; await db.SaveChangesAsync();
            return Results.Ok(new { message = "Appointment cancelled. The slot is available again." });
        }).RequireAuthorization("Staff").RequireRateLimiting("writes");

        group.MapGet("/blocks", async (int providerId, string date, BookingDb db) =>
        {
            if (!Schedule.Providers.Any(x => x.Id == providerId) || !Schedule.TryDate(date, out var day) || !Schedule.ValidDay(day))
                return Results.BadRequest(new { error = "Choose a listed advisor and a date within the next 30 days." });
            var end = day.AddDays(1);
            var records = await db.Bookings.AsNoTracking().Where(x => x.IsBlocked && !x.IsCancelled && x.ProviderId == providerId && x.StartsAt >= day && x.StartsAt < end)
                .OrderBy(x => x.StartsAt).Select(x => new { x.Id, x.StartsAt, reason = x.CustomerName }).ToListAsync();
            return Results.Ok(new { blocks = records.Select(x => new { x.Id, startsAt = x.StartsAt.ToString("yyyy-MM-dd'T'HH:mm:ss"), x.reason }) });
        }).RequireAuthorization("Staff");

        group.MapPost("/blocks", async (StaffBlock request, BookingDb db) =>
        {
            if (!Schedule.Providers.Any(x => x.Id == request.ProviderId)
                || !DateTime.TryParseExact(request.StartsAt, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !Schedule.ValidDay(start.Date) || !Schedule.Slots(start.Date).Contains(start))
                return Results.BadRequest(new { error = "Choose a valid future appointment slot." });
            var reason = request.Reason?.Trim() ?? "";
            if (reason.Length is < 2 or > 100) return Results.BadRequest(new { error = "Enter a reason of 2–100 characters." });
            // Blocks share the booking table's unique active reservation index.
            // Blocking an occupied slot fails, including a concurrent customer booking.
            var block = new Booking { ProviderId = request.ProviderId, ServiceId = 0, StartsAt = start, IsBlocked = true,
                CustomerName = reason, Email = "", ReferenceHash = Schedule.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))) };
            db.Bookings.Add(block);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 } or SqliteException { SqliteErrorCode: 19 })
            { return Results.Conflict(new { error = "This time is already booked or blocked. Choose another slot." }); }
            return Results.Json(new { block.Id, message = "Time blocked. Customers cannot reserve this slot." }, statusCode: 201);
        }).RequireAuthorization("Staff").RequireRateLimiting("writes");

        group.MapPost("/blocks/{id:int}/release", async (int id, BookingDb db) =>
        {
            var block = await db.Bookings.SingleOrDefaultAsync(x => x.Id == id && x.IsBlocked);
            if (block is null) return Results.NotFound(new { error = "Blocked slot not found." });
            block.IsCancelled = true; await db.SaveChangesAsync();
            return Results.Ok(new { message = "Block removed. Future slots are available to book." });
        }).RequireAuthorization("Staff").RequireRateLimiting("writes");
    }
}

public record StaffLogin(string Username, string Password);
public record StaffBlock(int ProviderId, string StartsAt, string Reason);
public sealed class StaffCredentials(string username, string password)
{
    public string Username { get; } = username;
    private readonly byte[] passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    public string SecurityStamp => Convert.ToHexString(passwordHash);
    public bool Matches(string? suppliedUsername, string? suppliedPassword) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(suppliedPassword ?? "")), passwordHash)
        && string.Equals(suppliedUsername, Username, StringComparison.Ordinal);
}
