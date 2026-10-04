# Prairie Bookings

A full-stack appointment booking portfolio project for a fictional Alberta service business. Customers choose an advisor and a weekday time, reserve a 30-minute session, and cancel using a private reference.

**Stack:** C# · ASP.NET Core 8 Minimal APIs · Cookie authentication · Entity Framework Core · SQL (SQLite by default, SQL Server optional) · JavaScript · HTML/CSS · PowerShell/Python integration tests.

## Run locally

Install the .NET 8 SDK or a newer SDK that can target .NET 8, and the ASP.NET Core 8 runtime. No separate database installation is needed for SQLite.

```powershell
dotnet restore
dotnet run --urls http://localhost:5080
```

Open http://localhost:5080. The app creates `App_Data/bookings.db` on first launch. Stop with Ctrl+C. Use fictional names and emails: this is a portfolio demo, and it does not send email.

## What works

- Three services and three fictional advisors, with 30-minute appointments.
- Weekday availability from 9 AM to 5 PM, within the next 30 days.
- All appointment wall-clock times are in `America/Edmonton`, including daylight saving changes in the current-time calculation.
- Server-side checks for date range, business hours, slot alignment, service, advisor, name, and email.
- A filtered unique SQL index prevents simultaneous active reservations for one advisor and time.
- A booking returns a cryptographically random, 128-bit private reference. Only its SHA-256 hash is stored in the database.
- Cancellation preserves history and makes the slot available again. Repeated cancellation is safe.
- Responsive layout, labelled controls, keyboard focus styles, loading/error states, and a confirmation screen.
- Six navigation destinations: Home, Services, Our team, About, Manage booking, and Book a session. Service and advisor cards preselect their choice in the booking form.
- Pexels stock photography saved locally so the interface does not depend on external image hosts. See `ASSETS.md` for photographer credits, source links, and licensing.
- Write requests are limited to 20 per minute per client IP. API responses are not cached.
- Password-protected staff workspace at `/staff.html`, with date/advisor/status filters, appointment counts, cancellation confirmation, and advisor time blocking.
- Staff availability blocks and customer bookings share one unique active reservation index, preventing conflicts even when requests arrive concurrently.

## Staff login

Open http://localhost:5080/staff.html or use the **Staff workspace** link in the footer. The default username is `staff`. On first local startup, the app generates a unique password in `App_Data/staff-password.txt`. Open that file locally and copy the password into the staff login form. The password and encryption keys are excluded from Git and the source ZIP.

To use your own credentials, set `Staff__Username` and `Staff__Password` environment variables before starting the app. Passwords must have at least 12 characters. Do not put them in source control. Changing either credential invalidates existing sessions. Use the environment variables and protected secret storage when hosting; the generated file is a local development convenience.

Staff sessions last one hour, use an HttpOnly/SameSite Strict cookie, and do not persist after the browser session. Staff mutations require the custom `X-Staff-Request: 1` header to prevent cross-site form submissions; the application does not enable cross-origin API access. Login is limited to five attempts per client IP per minute. All appointment and availability management endpoints require the Staff role.

The staff workspace displays contact details only after authentication. It never returns cancellation references or their hashes. The single staff account is appropriate to this portfolio MVP; multi-user identity, audit logging, and individual staff permissions are future enhancements.

## Architecture and tradeoffs

```text
HTML/CSS + JavaScript
        │ fetch /api/*
ASP.NET Core Minimal APIs
        │ EF Core parameterized queries
SQLite / SQL Server
        └─ unique active (ProviderId, StartsAt) index
```

`Models/Booking.cs` defines the booking record and request types. `Data/BookingDb.cs` defines column sizes and indexes. `Program.cs` contains the API and scheduling rules. `wwwroot/app.js` coordinates the UI and handles API errors.

The database, rather than an availability check alone, is the final authority on slot ownership. If two visitors book together, one gets HTTP 201 and the other gets HTTP 409. This avoids the check-then-insert race.

Services and advisors are fixed catalog entries in this focused MVP. Staff can block individual future slots and release them again. Booking rows with `IsBlocked=true` represent those reservations, so one filtered unique index covers both staff blocks and customer bookings; appointment queries exclude the blocks. Bookings use local wall-clock times deliberately because there is one Alberta business location and appointments are only during daytime. A multi-location version should store instants in UTC and model each location's zone, including ambiguous/nonexistent local times.

The cancellation reference grants access to cancellation only. There is no public endpoint listing customer data. Keep the reference private. Lost-reference recovery, customer accounts, rescheduling, holidays, configurable durations, and email delivery are future features.

## API

| Method | Endpoint | Result |
|---|---|---|
| GET | `/api/catalog` | Services, advisors, time zone, booking date bounds |
| GET | `/api/availability?providerId=1&date=YYYY-MM-DD` | Slot timestamps and availability |
| POST | `/api/bookings` | HTTP 201 with private reference; 409 if reserved |
| POST | `/api/bookings/cancel` | Cancel with `{ "reference": "..." }` |

Booking request:

```json
{
  "providerId": 1,
  "serviceId": 1,
  "startsAt": "2026-10-05T09:00:00",
  "customerName": "Demo Visitor",
  "email": "demo@example.com"
}
```

Replace the example timestamp with an available future slot. Timestamps are Alberta local times without a UTC suffix.

## Verify

With the app running, use a second PowerShell 7 terminal:

```powershell
pwsh -NoProfile -File tests/smoke.ps1
```

The 17 integration checks cover the catalog, time zone, availability, weekends, invalid inputs, past/off-grid/out-of-range slots, concurrent booking, slot visibility, unknown references, cancellation, idempotency, and rebooking. Test-created bookings use fictional data and are cancelled in cleanup. Run against a disposable demo database. Repeated runs within a minute may hit the write limit; wait a minute or restart the demo app before rerunning.

Verified locally: build with zero warnings/errors, all 17 checks against SQLite, browser booking/cancellation, and desktop/390-pixel mobile layout. SQL Server support is configured but not verified here because the execution environment cannot authenticate to the installed SQL Server. Dependency vulnerability auditing could not run because NuGet's Windows TLS connection failed in this environment; dependencies were restored from NuGet packages downloaded with Python TLS. Auditing stays enabled in normal restore and GitHub CI.

The staff addition passed **25 additional integration checks**, including unauthorized access, invalid login, session cookies, CSRF headers, query validation, appointment filtering/cancellation, time blocking/releasing, and concurrent staff-block/customer-booking requests. Staff login, dashboard rendering, blocking, unblocking, and logout were also verified in the browser using a separate test database.

For a new disposable staff test database, start the app in one PowerShell terminal:

```powershell
$env:ConnectionStrings__Bookings = 'Data Source=App_Data/staff-test-fresh.db'
$env:Staff__Password = 'Staff-test-password-2026'
dotnet run --urls http://localhost:5081
```

Then in a second terminal with Python 3 installed:

```powershell
$env:PRAIRIE_TEST_PASSWORD = 'Staff-test-password-2026'
python tests/staff_smoke.py
```

Use a new database filename on each full run: cancelled test reservations remain as history. The displayed example password is only for a disposable test instance, not a deployed or customer-facing app. The CI workflow generates its own random test password. No additional Python packages are required.

## Optional SQL Server configuration

In PowerShell, configure a **new, dedicated database**:

```powershell
$env:DatabaseProvider = 'SqlServer'
$env:ConnectionStrings__Bookings = 'Server=(localdb)\MSSQLLocalDB;Database=PrairieBookingsPortfolio;Trusted_Connection=True;TrustServerCertificate=True'
dotnet run --urls http://localhost:5080
```

The account must be able to create the dedicated database. `EnsureCreated` is used for this MVP; an idempotent additive upgrade adds `IsBlocked` to databases from the initial version without removing booking history. A production application should use reviewed EF migrations. Do not point the app at an existing shared database. The example trusts the local development certificate; use proper certificate validation and encryption on a hosted SQL connection. Clear those environment variables to return to the SQLite default.

## Publish and extend

The repository includes GitHub Actions for build and API smoke tests. See `PUBLISHING.md` for GitHub setup and `LINKEDIN.md` for the project description, launch post, and resume bullet.

The app currently runs locally. A backend host is required for a live demo; GitHub Pages cannot run ASP.NET Core. Use a durable database or persistent volume if hosting SQLite. Configure HTTPS, `AllowedHosts`, appropriate retention for contact information, and proxy-aware rate limiting before accepting real users. Protect the data-protection key directory and configure key encryption for a hosted environment; the local version persists keys to the ignored `App_Data/keys` directory. This demo is not intended for real medical or other sensitive appointments.

## License

MIT. See `LICENSE`.
