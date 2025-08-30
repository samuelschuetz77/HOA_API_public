using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;



var builder = WebApplication.CreateBuilder(args);

// Fix for enum serialization issues: Previously, POST requests required using number values (0,1,2)
// instead of enum names (NOT_STARTED, STARTED, etc). This converter allows using string names in JSON.
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

// 1) Enable static file serving from ./wwwroot (create this folder at project root)
//    Example: wwwroot/uploads/complaints/42/pipe_leak.jpg -> /uploads/complaints/42/pipe_leak.jpg
app.UseStaticFiles();

// simple logging middleware, ctx = httpcontext instacne
app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ReqLog");

    var path = $"{ctx.Request.Path}{ctx.Request.QueryString}";
    log.LogInformation("REQUEST {m} {p}", ctx.Request.Method, path);

    await next();

    sw.Stop();
    log.LogInformation("RESPONSE {code} in {ms} ms", ctx.Response.StatusCode, sw.ElapsedMilliseconds);
});



// 2) --- In-memory data (simple for learning; swap to DB later) ---
var residents = new List<Resident>
{
    new Resident(1, "Alice Johnson", "Unit A", "alice@example.com"),
    new Resident(2, "Bob Smith", "Unit B", "bob@example.com")
};
var complaints = new List<Complaint>();
var nextComplaintId = 1; //initialize compliant id's

// 3) --- Routing / endpoints (minimal APIs) ---



// Sanity endpoint; also useful to see custom headers later
app.MapGet("/greet", () => Results.Ok(new { message = "HOA API up" }))
   .WithName("Greet");

// Create a complaint
app.MapPost("/complaints", (CreateComplaintDto dto) =>
{
    // Basic validation
    if (string.IsNullOrWhiteSpace(dto.Subject) || string.IsNullOrWhiteSpace(dto.Description))
        return Results.BadRequest(new { error = "Subject and Description are required." });

    if (residents.All(r => r.ResidentId != dto.ResidentId))
        return Results.NotFound(new { error = $"Resident {dto.ResidentId} not found." });

    var complaint = new Complaint(
        ComplaintId: nextComplaintId++,
        ResidentId: dto.ResidentId,
        Subject: dto.Subject.Trim(),
        Description: dto.Description.Trim(),
        Status: ResolvedStatus.NOT_STARTED,
        Priority: dto.Priority ?? PriorityLevel.NORMAL,
        CreatedAtUtc: DateTimeOffset.UtcNow,
        UpdatedAtUtc: null,
        AttachmentPaths: dto.AttachmentPaths?
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => p.Trim())
                            .ToList() ?? new List<string>(),
        LocationNote: dto.LocationNote
    );

    complaints.Add(complaint);

    // Return 201 with a Location that points to the new resource
    return Results.Created($"/complaints/{complaint.ComplaintId}", complaint);
})
.AddEndpointFilter(async (invocationContext, next) =>
{
    var dto = invocationContext.Arguments.OfType<CreateComplaintDto>().FirstOrDefault();
    var logger = invocationContext.HttpContext.RequestServices
        .GetRequiredService<ILoggerFactory>().CreateLogger("ReqLog");

    logger.LogInformation("Complaint created: residentId={rid}, subject=\"{sub}\"",
        dto?.ResidentId, dto?.Subject);

    return await next(invocationContext);
})
.WithName("CreateComplaint");


// List complaints with optional filters (?status=STARTED&priority=High&residentId=2)
app.MapGet("/complaints", (string? status, string? priority, int? residentId) =>
{
    IEnumerable<Complaint> query = complaints;

    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<ResolvedStatus>(status, ignoreCase: true, out var s))
            return Results.BadRequest(new { error = $"Unknown status '{status}'." });
        query = query.Where(c => c.Status == s);
    }

    if (!string.IsNullOrWhiteSpace(priority))
    {
        if (!Enum.TryParse<PriorityLevel>(priority, ignoreCase: true, out var p))  //ignoreCase: true = treat "started", "Started", "STARTED" the same.
            return Results.BadRequest(new { error = $"Unknown priority '{priority}'." });
        query = query.Where(c => c.Priority == p);
    }

    if (residentId is not null)
        query = query.Where(c => c.ResidentId == residentId);

    return Results.Ok(query);
})
.WithName("ListComplaints");

// Get a single complaint
app.MapGet("/complaints/{id:int}", (int id) =>
{
    var complaint = complaints.FirstOrDefault(c => c.ComplaintId == id);
    return complaint is null ? Results.NotFound(new { error = $"Complaint {id} not found." }) : Results.Ok(complaint);
})
.WithName("GetComplaintById");

// Update status only (PATCH /complaints/{id}/status)
app.MapPatch("/complaints/{id:int}/status", (int id, UpdateStatusDto dto) =>
{
    var complaint = complaints.FirstOrDefault(c => c.ComplaintId == id);
    if (complaint is null)
        return Results.NotFound(new { error = $"Complaint {id} not found." });

    // Validate incoming status
    if (!Enum.IsDefined(typeof(ResolvedStatus), dto.Status))
        return Results.BadRequest(new { error = $"Invalid status '{dto.Status}'." });

    // Replace with a new record (immutable pattern)
    var updated = complaint with { Status = dto.Status, UpdatedAtUtc = DateTimeOffset.UtcNow };
    complaints[complaints.FindIndex(c => c.ComplaintId == id)] = updated;

    return Results.Ok(updated);
})
.WithName("UpdateComplaintStatus");

// Residents list and single
app.MapGet("/residents", () => Results.Ok(residents)).WithName("ListResidents");
app.MapGet("/residents/{id:int}", (int id) =>
{
    var resident = residents.FirstOrDefault(r => r.ResidentId == id);
    return resident is null ? Results.NotFound(new { error = $"Resident {id} not found." }) : Results.Ok(resident);
}).WithName("GetResidentById");

app.Run();

// --- Models / DTOs ---

// Simple enums for status/priority
public enum ResolvedStatus { NOT_STARTED, STARTED, COMPLETE }
public enum PriorityLevel { LOW, NORMAL, HIGH }  // Low=0, Normal=1, High=2):

public record Resident(int ResidentId, string Name, string Unit, string Email);

public record Complaint(
    int ComplaintId,
    int ResidentId,
    string Subject,
    string Description,
    ResolvedStatus Status,
    PriorityLevel Priority,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    List<string> AttachmentPaths,
    string? LocationNote
);

// Incoming body for POST /complaints
public record CreateComplaintDto(
    int ResidentId,
    string Subject,
    string Description,
    PriorityLevel? Priority,
    List<string>? AttachmentPaths,
    string? LocationNote
);

// Incoming body for PATCH /complaints/{id}/status
public record UpdateStatusDto(
    ResolvedStatus Status
);



