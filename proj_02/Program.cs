using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;




var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp", policy =>
    {
        policy.WithOrigins("http://localhost:5173") // Vite server default
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});


// Fix for enum serialization issues: Previously, POST requests required using number values (0,1,2)
// instead of enum names (NOT_STARTED, STARTED, etc). This converter allows using string names in JSON.
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

string connStr = "Data Source=hoa.db";

// helper
IDbConnection OpenConnection() => new SqliteConnection(connStr);

// make sure tables exist
using (var conn = OpenConnection())
{
    conn.Execute(@"
        CREATE TABLE IF NOT EXISTS Resident (
            ResidentId INTEGER PRIMARY KEY,
            Name TEXT NOT NULL,
            Unit TEXT,
            Email TEXT
        );
        CREATE TABLE IF NOT EXISTS Complaint (
            ComplaintId INTEGER PRIMARY KEY AUTOINCREMENT,
            ResidentId INTEGER NOT NULL,
            Subject TEXT NOT NULL,
            Description TEXT NOT NULL,
            Status TEXT NOT NULL,
            Priority TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT,
            LocationNote TEXT,
            FOREIGN KEY (ResidentId) REFERENCES Resident(ResidentId)
        );
    ");

    // seed residents if empty
    var count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Resident;");
    if (count == 0)
    {
        conn.Execute("INSERT INTO Resident (ResidentId, Name, Unit, Email) VALUES (1,'Alice Johnson','Unit A','alice@example.com');");
        conn.Execute("INSERT INTO Resident (ResidentId, Name, Unit, Email) VALUES (2,'Bob Smith','Unit B','bob@example.com');");
    }
}


// 1) Enable static file serving from ./wwwroot (create this folder at project root)
//    Example: wwwroot/uploads/complaints/42/pipe_leak.jpg -> /uploads/complaints/42/pipe_leak.jpg
app.UseStaticFiles();
app.UseCors("AllowReactApp");



// simple request/resposne logging middleware, ctx = httpcontext instacne
app.Use(async (ctx, next) =>
{
    // Skip logging for static files
    if (ctx.Request.Path.StartsWithSegments("/uploads"))
    {
        await next();
        return;
    }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ReqLog");

    var path = $"{ctx.Request.Path}{ctx.Request.QueryString}";
    log.LogInformation("REQUEST {m} {p}", ctx.Request.Method, path);

    await next(); // like saying, "call the next middleware (or the endpoint), then wait until it finishes so I can run my after-logic.”

    sw.Stop();
    log.LogInformation("RESPONSE {code} in {ms} ms", ctx.Response.StatusCode, sw.ElapsedMilliseconds);
});
// exception handling middleware
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ExceptionMiddleware");

        log.LogError(ex, "Unhandled exception for {Path}", ctx.Request.Path);

        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = "Something went wrong. Please try again later."
        });
    }
});


// HEADERS/repsonse middleware: set headers before the response begins
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Api-Version"] = "1.0";
    ctx.Response.Headers["X-Correlation-Id"] = Guid.NewGuid().ToString();

    await next(); // let the rest of the pipeline run
});






// var residents = new List<Resident>
// {
//     new Resident(1, "Alice Johnson", "Unit A", "alice@example.com"),
//     new Resident(2, "Bob Smith", "Unit B", "bob@example.com")
// };

// var complaints = new List<Complaint>();
// var nextComplaintId = 1; //initialize compliant id's


//routing/endpoints 


// Sanity endpoint; also useful to see custom headers later
app.MapGet("/greet", () => Results.Ok(new { message = "HOA API up" }))
   .WithName("Greet");

// Create a complaint
app.MapPost("/complaints", async (CreateComplaintDto dto) =>
{
    using var conn = OpenConnection();

    // verify resident exists
    var resident = await conn.QueryFirstOrDefaultAsync<Resident>(
        "SELECT * FROM Resident WHERE ResidentId = @id;", new { id = dto.ResidentId });
    if (resident is null)
        return Results.NotFound(new { error = $"Resident {dto.ResidentId} not found." });

    var sql = @"INSERT INTO Complaint
        (ResidentId, Subject, Description, Status, Priority, CreatedAtUtc, LocationNote)
        VALUES (@ResidentId, @Subject, @Description, @Status, @Priority, @CreatedAtUtc, @LocationNote);
        SELECT last_insert_rowid();";

    var newId = await conn.ExecuteScalarAsync<int>(sql, new {
        dto.ResidentId,
        dto.Subject,
        dto.Description,
        Status = ResolvedStatus.NOT_STARTED.ToString(),
        Priority = (dto.Priority ?? PriorityLevel.NORMAL).ToString(),
        CreatedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
        dto.LocationNote
    });

    var dbComplaint = await conn.QueryFirstAsync<DbComplaint>(
        "SELECT * FROM Complaint WHERE ComplaintId = @id;", new { id = newId });

    return Results.Created($"/complaints/{newId}", dbComplaint.ToDomainModel());
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
app.MapGet("/complaints", async (string? status, string? priority, int? residentId) =>
{
    using var conn = OpenConnection();

    var sql = "SELECT * FROM Complaint WHERE 1=1 ";
    var dyn = new DynamicParameters();

    if (!string.IsNullOrWhiteSpace(status))
    {
        sql += "AND Status = @status ";
        dyn.Add("status", status, DbType.String);
    }

    if (!string.IsNullOrWhiteSpace(priority))
    {
        sql += "AND Priority = @priority ";
        dyn.Add("priority", priority, DbType.String);
    }

    if (residentId is not null)
    {
        sql += "AND ResidentId = @rid ";
        dyn.Add("rid", residentId, DbType.Int32);
    }

    var dbComplaints = await conn.QueryAsync<DbComplaint>(sql, dyn);
    var complaints = dbComplaints.Select(c => c.ToDomainModel());
    return Results.Ok(complaints);
})

.WithName("ListComplaints");

// Get a single complaint
app.MapGet("/complaints/{id:int}", async (int id) =>
{
    using var conn = OpenConnection();
    var dbComplaint = await conn.QueryFirstOrDefaultAsync<DbComplaint>(
        "SELECT * FROM Complaint WHERE ComplaintId = @id;", new { id });

    if (dbComplaint is null)
        return Results.NotFound(new { error = $"Complaint {id} not found." });

    return Results.Ok(dbComplaint.ToDomainModel());
})
.WithName("GetComplaintById");


// Update status only (PATCH /complaints/{id}/status)
app.MapPatch("/complaints/{id:int}/status", async (int id, UpdateStatusDto dto) =>
{
    using var conn = OpenConnection();

    // ensure complaint exists
    var dbComplaint = await conn.QueryFirstOrDefaultAsync<DbComplaint>(
        "SELECT * FROM Complaint WHERE ComplaintId = @id;", new { id });
    if (dbComplaint is null)
        return Results.NotFound(new { error = $"Complaint {id} not found." });

    // validate status
    if (!Enum.IsDefined(typeof(ResolvedStatus), dto.Status))
        return Results.BadRequest(new { error = $"Invalid status '{dto.Status}'." });

    // update DB
    var sql = @"UPDATE Complaint 
                SET Status = @status, UpdatedAtUtc = @updated 
                WHERE ComplaintId = @id;";
    await conn.ExecuteAsync(sql, new {
        id,
        status = dto.Status.ToString(),
        updated = DateTimeOffset.UtcNow.ToString("o")
    });

    // re-query updated row
    var updated = await conn.QueryFirstAsync<DbComplaint>(
        "SELECT * FROM Complaint WHERE ComplaintId = @id;", new { id });

    return Results.Ok(updated.ToDomainModel());
})
.WithName("UpdateComplaintStatus");


// Residents list and single
app.MapGet("/residents", async () =>
{
    using var conn = OpenConnection();
    var dbResidents = await conn.QueryAsync<Resident>(
        "SELECT * FROM Resident;");
    return Results.Ok(dbResidents);
})
.WithName("ListResidents");

app.MapGet("/residents/{id:int}", async (int id) =>
{
    using var conn = OpenConnection();
    var resident = await conn.QueryFirstOrDefaultAsync<Resident>(
        "SELECT * FROM Resident WHERE ResidentId = @id;", new { id });
    return resident is null 
        ? Results.NotFound(new { error = $"Resident {id} not found." }) 
        : Results.Ok(resident);
})
.WithName("GetResidentById");


app.Run();

// --- Models / DTOs ---

// DB model that matches SQLite table exactly
public class DbComplaint
{
    public int ComplaintId { get; set; }
    public int ResidentId { get; set; }
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public string Priority { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string? UpdatedAtUtc { get; set; }
    public string? LocationNote { get; set; }

    // Helper to convert DB -> domain model
    public Complaint ToDomainModel() => new(
        ComplaintId,
        ResidentId,
        Subject,
        Description,
        Enum.Parse<ResolvedStatus>(Status),
        Enum.Parse<PriorityLevel>(Priority),
        DateTimeOffset.Parse(CreatedAtUtc),
        UpdatedAtUtc is null ? null : DateTimeOffset.Parse(UpdatedAtUtc),
        new List<string>(), // DB doesn't store attachments
        LocationNote
    );
}

// Simple enums for status/priority
public enum ResolvedStatus { NOT_STARTED, STARTED, COMPLETE }
public enum PriorityLevel { LOW, NORMAL, HIGH }  // Low=0, Normal=1, High=2):

public record Resident
{
    public Resident() { }
    public Resident(int ResidentId, string Name, string Unit, string Email)
    {
        this.ResidentId = ResidentId;
        this.Name = Name;
        this.Unit = Unit;
        this.Email = Email;
    }

    public int ResidentId { get; init; }
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Email { get; init; } = "";
}

public record Complaint(
    int ComplaintId,
    int ResidentId,
    string Subject,
    string Description,
    ResolvedStatus Status,
    PriorityLevel Priority,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    List<string>? AttachmentPaths = null,
    string? LocationNote = null
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



