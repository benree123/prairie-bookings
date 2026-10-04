param([string]$BaseUrl = 'http://localhost:5080')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$references = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Check($Condition, [string]$Description) {
    if (-not $Condition) { throw "FAIL: $Description" }
    $script:passed++
    Write-Host "PASS: $Description"
}
function MakePost([string]$Path, $Body) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$BaseUrl$Path")
    $request.Content = [System.Net.Http.StringContent]::new(($Body | ConvertTo-Json -Compress), [System.Text.Encoding]::UTF8, 'application/json')
    return $request
}
function ReadResponse($Response) {
    return @{ Status = [int]$Response.StatusCode; Data = ($Response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json) }
}
function Post([string]$Path, $Body) {
    $request = MakePost $Path $Body
    try { return ReadResponse ($client.SendAsync($request).GetAwaiter().GetResult()) }
    finally { $request.Dispose() }
}
try {
    $catalog = Invoke-RestMethod "$BaseUrl/api/catalog"
    Check ($catalog.services.Count -eq 3 -and $catalog.providers.Count -eq 3) 'Service and advisor catalog'
    Check ($catalog.timezone -eq 'America/Edmonton') 'Alberta time zone'
    $day = [datetime]::ParseExact($catalog.today, 'yyyy-MM-dd', [cultureinfo]::InvariantCulture).AddDays(1)
    while ($day.DayOfWeek -in @([DayOfWeek]::Saturday, [DayOfWeek]::Sunday)) { $day = $day.AddDays(1) }
    $date = $day.ToString('yyyy-MM-dd')
    $availability = Invoke-RestMethod "$BaseUrl/api/availability?providerId=3&date=$date"
    $slot = ($availability.slots | Where-Object available | Select-Object -First 1).startsAt
    Check ([bool]$slot) 'Future weekday availability'
    $weekend = $day
    while ($weekend.DayOfWeek -ne [DayOfWeek]::Saturday) { $weekend = $weekend.AddDays(1) }
    $weekendSlots = Invoke-RestMethod "$BaseUrl/api/availability?providerId=3&date=$($weekend.ToString('yyyy-MM-dd'))"
    Check ($weekendSlots.slots.Count -eq 0) 'No weekend appointments'
    $body = @{providerId=3;serviceId=1;startsAt=$slot;customerName='Portfolio Test';email='test@example.com'}
    $invalid = $body.Clone(); $invalid.email='invalid'
    Check ((Post '/api/bookings' $invalid).Status -eq 400) 'Reject invalid email'
    $invalid = $body.Clone(); $invalid.startsAt='2020-01-01T09:00:00'
    Check ((Post '/api/bookings' $invalid).Status -eq 400) 'Reject past appointments'
    $invalid = $body.Clone(); $invalid.startsAt=$date+'T09:15:00'
    Check ((Post '/api/bookings' $invalid).Status -eq 400) 'Reject off-grid times'
    $invalid = $body.Clone(); $invalid.providerId=999
    Check ((Post '/api/bookings' $invalid).Status -eq 400) 'Reject unknown providers'
    $invalid = $body.Clone(); $invalid.startsAt=$weekend.ToString('yyyy-MM-dd')+'T09:00:00'
    Check ((Post '/api/bookings' $invalid).Status -eq 400) 'Reject weekend booking requests'
    $invalid = $body.Clone(); $invalid.startsAt=$day.AddDays(40).ToString('yyyy-MM-dd')+'T09:00:00'
    Check ((Post '/api/bookings' $invalid).Status -eq 400) 'Reject dates beyond booking horizon'
    # Start both requests before waiting: verifies the database invariant under concurrency.
    $firstRequest = MakePost '/api/bookings' $body
    $secondRequest = MakePost '/api/bookings' $body
    $firstTask = $client.SendAsync($firstRequest)
    $secondTask = $client.SendAsync($secondRequest)
    $results = @((ReadResponse $firstTask.GetAwaiter().GetResult()), (ReadResponse $secondTask.GetAwaiter().GetResult()))
    $firstRequest.Dispose(); $secondRequest.Dispose()
    foreach ($result in $results) { if ($result.Status -eq 201) { $references.Add($result.Data.reference) } }
    Check (@($results | Where-Object Status -eq 201).Count -eq 1 -and @($results | Where-Object Status -eq 409).Count -eq 1) 'Concurrent booking: exactly one reservation, one conflict'
    $reference = $references[0]
    $availability = Invoke-RestMethod "$BaseUrl/api/availability?providerId=3&date=$date"
    Check (-not ($availability.slots | Where-Object startsAt -eq $slot).available) 'Reserved slot disappears from availability'
    Check ((Post '/api/bookings/cancel' @{reference=('0'*32)}).Status -eq 404) 'Unknown reference cannot cancel a booking'
    Check ((Post '/api/bookings/cancel' @{reference=$reference}).Status -eq 200) 'Cancel using private reference'
    Check ((Post '/api/bookings/cancel' @{reference=$reference}).Status -eq 200) 'Repeated cancellation is idempotent'
    $availability = Invoke-RestMethod "$BaseUrl/api/availability?providerId=3&date=$date"
    Check (($availability.slots | Where-Object startsAt -eq $slot).available) 'Cancellation releases slot'
    $rebooked = Post '/api/bookings' $body
    if ($rebooked.Status -eq 201) { $references.Add($rebooked.Data.reference) }
    Check ($rebooked.Status -eq 201) 'Released slot can be booked again'
    Write-Host "$passed checks passed."
} finally {
    foreach ($reference in $references) { try { $null = Post '/api/bookings/cancel' @{reference=$reference} } catch { Write-Warning 'Test booking cleanup failed. Cancel the reference manually.' } }
    $client.Dispose()
}
