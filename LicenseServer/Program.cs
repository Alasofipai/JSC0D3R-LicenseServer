using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var dataDir = Path.Combine(app.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
var dataFile = Path.Combine(dataDir, "licenses.json");
var gate = new object();
if (!File.Exists(dataFile)) File.WriteAllText(dataFile, "[]");

List<LicenseRecord> Load()
{
    lock (gate)
    {
        var json = File.ReadAllText(dataFile);
        return JsonSerializer.Deserialize<List<LicenseRecord>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }
}
void Save(List<LicenseRecord> list)
{
    lock (gate)
    {
        File.WriteAllText(dataFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }
}
bool IsAdmin(HttpRequest req) => req.Headers.TryGetValue("X-Admin-Key", out var key) && key == builder.Configuration["AdminKey"];

app.MapPost("/api/license/validate", (LicenseCheck check) =>
{
    if (string.IsNullOrWhiteSpace(check.Hwid) || string.IsNullOrWhiteSpace(check.LicenseKey))
        return Results.BadRequest(new { active = false, message = "Datos incompletos" });

    var lic = Load().FirstOrDefault(x => x.LicenseKey.Equals(check.LicenseKey, StringComparison.OrdinalIgnoreCase));
    if (lic is null) return Results.NotFound(new { active = false, message = "Clave no encontrada" });
    if (!lic.Active) return Results.Ok(new { active = false, customer = lic.Customer, message = "Licencia desactivada" });
    if (lic.ExpiresAt is not null && lic.ExpiresAt < DateTimeOffset.UtcNow)
        return Results.Ok(new { active = false, customer = lic.Customer, message = "Licencia expirada", expiresAt = lic.ExpiresAt });

    if (string.IsNullOrWhiteSpace(lic.Hwid))
    {
        lic.Hwid = check.Hwid;
        lic.LastSeenAt = DateTimeOffset.UtcNow;
        var all = Load();
        var idx = all.FindIndex(x => x.Id == lic.Id);
        all[idx] = lic;
        Save(all);
    }
    else if (!lic.Hwid.Equals(check.Hwid, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new { active = false, customer = lic.Customer, message = "Licencia vinculada a otro equipo" });
    }
    else
    {
        var all = Load();
        var idx = all.FindIndex(x => x.Id == lic.Id);
        if (idx >= 0) { all[idx].LastSeenAt = DateTimeOffset.UtcNow; Save(all); }
    }

    return Results.Ok(new { active = true, customer = lic.Customer, expiresAt = lic.ExpiresAt, message = "OK" });
});

app.MapGet("/api/admin/licenses", (HttpRequest req) => IsAdmin(req) ? Results.Ok(Load()) : Results.Unauthorized());

app.MapPost("/api/admin/licenses", (HttpRequest req, CreateLicense input) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    var list = Load();
    var key = string.IsNullOrWhiteSpace(input.LicenseKey) ? MakeKey() : input.LicenseKey.Trim();
    if (list.Any(x => x.LicenseKey.Equals(key, StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new { message = "La clave ya existe" });
    var item = new LicenseRecord
    {
        Id = Guid.NewGuid(), LicenseKey = key, Customer = input.Customer?.Trim() ?? "Cliente",
        Active = input.Active, Hwid = input.Hwid?.Trim() ?? "", ExpiresAt = input.ExpiresAt,
        CreatedAt = DateTimeOffset.UtcNow
    };
    list.Add(item); Save(list); return Results.Ok(item);
});

app.MapPatch("/api/admin/licenses/{id:guid}", (HttpRequest req, Guid id, UpdateLicense input) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    var list = Load(); var item = list.FirstOrDefault(x => x.Id == id); if (item is null) return Results.NotFound();
    if (input.Active is not null) item.Active = input.Active.Value;
    if (input.Customer is not null) item.Customer = input.Customer.Trim();
    if (input.Hwid is not null) item.Hwid = input.Hwid.Trim();
    if (input.ClearHwid == true) item.Hwid = "";
    if (input.SetExpiry == true) item.ExpiresAt = input.ExpiresAt;
    Save(list); return Results.Ok(item);
});

app.MapDelete("/api/admin/licenses/{id:guid}", (HttpRequest req, Guid id) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    var list = Load(); var removed = list.RemoveAll(x => x.Id == id); Save(list);
    return removed > 0 ? Results.NoContent() : Results.NotFound();
});

app.MapFallbackToFile("index.html");
app.Run();

static string MakeKey()
{
    const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
    var charsOut = bytes.Select(b => chars[b % chars.Length]).ToArray();
    return string.Join('-', Enumerable.Range(0, 4).Select(i => new string(charsOut, i * 5, 5)));
}

record LicenseCheck(string Hwid, string LicenseKey);
record CreateLicense(string? Customer, string? LicenseKey, string? Hwid, bool Active = true, DateTimeOffset? ExpiresAt = null);
record UpdateLicense(bool? Active, string? Customer, string? Hwid, bool? ClearHwid, bool? SetExpiry, DateTimeOffset? ExpiresAt);
class LicenseRecord
{
    public Guid Id { get; set; }
    public string LicenseKey { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Hwid { get; set; } = "";
    public bool Active { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}
