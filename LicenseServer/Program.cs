using Npgsql;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var rawDatabaseUrl =
    builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException(
        "Falta la variable de entorno DATABASE_URL."
    );

var connectionString = BuildPostgresConnectionString(rawDatabaseUrl);

bool IsAdmin(HttpRequest req)
{
    return req.Headers.TryGetValue("X-Admin-Key", out var key)
        && !string.IsNullOrWhiteSpace(key)
        && key == builder.Configuration["AdminKey"];
}


// ============================================================
// INICIALIZAR BASE DE DATOS
// ============================================================

await EnsureDatabaseAsync();


// ============================================================
// VALIDAR LICENCIA
// ============================================================

app.MapPost("/api/license/validate", async (LicenseCheck check) =>
{
    if (string.IsNullOrWhiteSpace(check.Hwid) ||
        string.IsNullOrWhiteSpace(check.LicenseKey))
    {
        return Results.BadRequest(new
        {
            active = false,
            message = "Datos incompletos"
        });
    }

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    LicenseRecord? lic;

    await using (var cmd = new NpgsqlCommand("""
        SELECT
            id,
            license_key,
            customer,
            hwid,
            active,
            expires_at,
            created_at,
            last_seen_at
        FROM licenses
        WHERE LOWER(license_key) = LOWER(@key)
        LIMIT 1
        """, conn))
    {
        cmd.Parameters.AddWithValue("key", check.LicenseKey.Trim());

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return Results.NotFound(new
            {
                active = false,
                message = "Clave no encontrada"
            });
        }

        lic = ReadLicense(reader);
    }

    if (!lic.Active)
    {
        return Results.Ok(new
        {
            active = false,
            customer = lic.Customer,
            message = "Licencia desactivada"
        });
    }

    if (lic.ExpiresAt is not null &&
        lic.ExpiresAt.Value < DateTimeOffset.UtcNow)
    {
        return Results.Ok(new
        {
            active = false,
            customer = lic.Customer,
            message = "Licencia expirada",
            expiresAt = lic.ExpiresAt
        });
    }

    // Primera activación: vincular HWID
    if (string.IsNullOrWhiteSpace(lic.Hwid))
    {
        await using var update = new NpgsqlCommand("""
            UPDATE licenses
            SET hwid = @hwid,
                last_seen_at = @now
            WHERE id = @id
            """, conn);

        update.Parameters.AddWithValue("hwid", check.Hwid.Trim());
        update.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        update.Parameters.AddWithValue("id", lic.Id);

        await update.ExecuteNonQueryAsync();

        lic.Hwid = check.Hwid.Trim();
        lic.LastSeenAt = DateTimeOffset.UtcNow;
    }
    else if (!lic.Hwid.Equals(
        check.Hwid.Trim(),
        StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new
        {
            active = false,
            customer = lic.Customer,
            message = "Licencia vinculada a otro equipo"
        });
    }
    else
    {
        await using var updateSeen = new NpgsqlCommand("""
            UPDATE licenses
            SET last_seen_at = @now
            WHERE id = @id
            """, conn);

        updateSeen.Parameters.AddWithValue(
            "now",
            DateTimeOffset.UtcNow
        );

        updateSeen.Parameters.AddWithValue(
            "id",
            lic.Id
        );

        await updateSeen.ExecuteNonQueryAsync();

        lic.LastSeenAt = DateTimeOffset.UtcNow;
    }

    return Results.Ok(new
    {
        active = true,
        customer = lic.Customer,
        expiresAt = lic.ExpiresAt,
        message = "OK"
    });
});


// ============================================================
// LISTAR LICENCIAS
// ============================================================

app.MapGet("/api/admin/licenses", async (HttpRequest req) =>
{
    if (!IsAdmin(req))
        return Results.Unauthorized();

    var list = new List<LicenseRecord>();

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand("""
        SELECT
            id,
            license_key,
            customer,
            hwid,
            active,
            expires_at,
            created_at,
            last_seen_at
        FROM licenses
        ORDER BY created_at DESC
        """, conn);

    await using var reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        list.Add(ReadLicense(reader));
    }

    return Results.Ok(list);
});


// ============================================================
// CREAR LICENCIA
// ============================================================

app.MapPost("/api/admin/licenses",
async (HttpRequest req, CreateLicense input) =>
{
    if (!IsAdmin(req))
        return Results.Unauthorized();

    var key = string.IsNullOrWhiteSpace(input.LicenseKey)
        ? MakeKey()
        : input.LicenseKey.Trim();

    var item = new LicenseRecord
    {
        Id = Guid.NewGuid(),
        LicenseKey = key,
        Customer = string.IsNullOrWhiteSpace(input.Customer)
            ? "Cliente"
            : input.Customer.Trim(),
        Active = input.Active,
        Hwid = input.Hwid?.Trim() ?? "",
        ExpiresAt = input.ExpiresAt,
        CreatedAt = DateTimeOffset.UtcNow,
        LastSeenAt = null
    };

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    await using var existsCmd = new NpgsqlCommand("""
        SELECT EXISTS(
            SELECT 1
            FROM licenses
            WHERE LOWER(license_key) = LOWER(@key)
        )
        """, conn);

    existsCmd.Parameters.AddWithValue("key", key);

    var exists = (bool)(await existsCmd.ExecuteScalarAsync() ?? false);

    if (exists)
    {
        return Results.Conflict(new
        {
            message = "La clave ya existe"
        });
    }

    await using var cmd = new NpgsqlCommand("""
        INSERT INTO licenses
        (
            id,
            license_key,
            customer,
            hwid,
            active,
            expires_at,
            created_at,
            last_seen_at
        )
        VALUES
        (
            @id,
            @license_key,
            @customer,
            @hwid,
            @active,
            @expires_at,
            @created_at,
            @last_seen_at
        )
        """, conn);

    cmd.Parameters.AddWithValue("id", item.Id);
    cmd.Parameters.AddWithValue("license_key", item.LicenseKey);
    cmd.Parameters.AddWithValue("customer", item.Customer);
    cmd.Parameters.AddWithValue("hwid", item.Hwid);
    cmd.Parameters.AddWithValue("active", item.Active);

    cmd.Parameters.AddWithValue(
        "expires_at",
        item.ExpiresAt is null
            ? DBNull.Value
            : item.ExpiresAt.Value
    );

    cmd.Parameters.AddWithValue(
        "created_at",
        item.CreatedAt
    );

    cmd.Parameters.AddWithValue(
        "last_seen_at",
        DBNull.Value
    );

    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(item);
});


// ============================================================
// MODIFICAR LICENCIA
// ============================================================

app.MapPatch("/api/admin/licenses/{id:guid}",
async (
    HttpRequest req,
    Guid id,
    UpdateLicense input
) =>
{
    if (!IsAdmin(req))
        return Results.Unauthorized();

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    LicenseRecord? item;

    await using (var get = new NpgsqlCommand("""
        SELECT
            id,
            license_key,
            customer,
            hwid,
            active,
            expires_at,
            created_at,
            last_seen_at
        FROM licenses
        WHERE id = @id
        LIMIT 1
        """, conn))
    {
        get.Parameters.AddWithValue("id", id);

        await using var reader = await get.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            return Results.NotFound();

        item = ReadLicense(reader);
    }

    if (input.Active is not null)
        item.Active = input.Active.Value;

    if (input.Customer is not null)
        item.Customer = input.Customer.Trim();

    if (input.Hwid is not null)
        item.Hwid = input.Hwid.Trim();

    if (input.ClearHwid == true)
        item.Hwid = "";

    if (input.SetExpiry == true)
        item.ExpiresAt = input.ExpiresAt;

    await using var update = new NpgsqlCommand("""
        UPDATE licenses
        SET
            customer = @customer,
            hwid = @hwid,
            active = @active,
            expires_at = @expires_at
        WHERE id = @id
        """, conn);

    update.Parameters.AddWithValue(
        "customer",
        item.Customer
    );

    update.Parameters.AddWithValue(
        "hwid",
        item.Hwid
    );

    update.Parameters.AddWithValue(
        "active",
        item.Active
    );

    update.Parameters.AddWithValue(
        "expires_at",
        item.ExpiresAt is null
            ? DBNull.Value
            : item.ExpiresAt.Value
    );

    update.Parameters.AddWithValue(
        "id",
        item.Id
    );

    await update.ExecuteNonQueryAsync();

    return Results.Ok(item);
});


// ============================================================
// ELIMINAR LICENCIA
// ============================================================

app.MapDelete("/api/admin/licenses/{id:guid}",
async (HttpRequest req, Guid id) =>
{
    if (!IsAdmin(req))
        return Results.Unauthorized();

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand("""
        DELETE FROM licenses
        WHERE id = @id
        """, conn);

    cmd.Parameters.AddWithValue("id", id);

    var deleted = await cmd.ExecuteNonQueryAsync();

    return deleted > 0
        ? Results.NoContent()
        : Results.NotFound();
});


// ============================================================
// PANEL WEB
// ============================================================

app.MapFallbackToFile("index.html");

app.Run();


// ============================================================
// CREAR TABLA AUTOMÁTICAMENTE
// ============================================================

async Task EnsureDatabaseAsync()
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand("""
        CREATE TABLE IF NOT EXISTS licenses
        (
            id UUID PRIMARY KEY,
            license_key TEXT NOT NULL UNIQUE,
            customer TEXT NOT NULL,
            hwid TEXT NOT NULL DEFAULT '',
            active BOOLEAN NOT NULL DEFAULT TRUE,
            expires_at TIMESTAMPTZ NULL,
            created_at TIMESTAMPTZ NOT NULL,
            last_seen_at TIMESTAMPTZ NULL
        );

        CREATE INDEX IF NOT EXISTS
            idx_licenses_license_key_lower
        ON licenses (LOWER(license_key));

        CREATE INDEX IF NOT EXISTS
            idx_licenses_hwid
        ON licenses (hwid);
        """, conn);

    await cmd.ExecuteNonQueryAsync();
}


// ============================================================
// SOPORTAR DATABASE_URL DE RENDER
// ============================================================

static string BuildPostgresConnectionString(string value)
{
    if (!value.StartsWith(
            "postgres://",
            StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith(
            "postgresql://",
            StringComparison.OrdinalIgnoreCase))
    {
        return value;
    }

    var uri = new Uri(value);

    var userInfo = uri.UserInfo.Split(
        ':',
        2,
        StringSplitOptions.None
    );

    var username = Uri.UnescapeDataString(userInfo[0]);

    var password = userInfo.Length > 1
        ? Uri.UnescapeDataString(userInfo[1])
        : "";

    var database = uri.AbsolutePath.Trim('/');

    var cs = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = username,
        Password = password,
        Database = database,
        SSLMode = SslMode.Require,
        TrustServerCertificate = true,
        Pooling = true
    };

    return cs.ConnectionString;
}


// ============================================================
// LEER LICENCIA
// ============================================================

static LicenseRecord ReadLicense(NpgsqlDataReader reader)
{
    return new LicenseRecord
    {
        Id = reader.GetGuid(
            reader.GetOrdinal("id")
        ),

        LicenseKey = reader.GetString(
            reader.GetOrdinal("license_key")
        ),

        Customer = reader.GetString(
            reader.GetOrdinal("customer")
        ),

        Hwid = reader.GetString(
            reader.GetOrdinal("hwid")
        ),

        Active = reader.GetBoolean(
            reader.GetOrdinal("active")
        ),

        ExpiresAt = reader.IsDBNull(
            reader.GetOrdinal("expires_at")
        )
            ? null
            : reader.GetFieldValue<DateTimeOffset>(
                reader.GetOrdinal("expires_at")
            ),

        CreatedAt = reader.GetFieldValue<DateTimeOffset>(
            reader.GetOrdinal("created_at")
        ),

        LastSeenAt = reader.IsDBNull(
            reader.GetOrdinal("last_seen_at")
        )
            ? null
            : reader.GetFieldValue<DateTimeOffset>(
                reader.GetOrdinal("last_seen_at")
            )
    };
}


// ============================================================
// GENERAR CLAVE
// ============================================================

static string MakeKey()
{
    const string chars =
        "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    var bytes =
        System.Security.Cryptography
            .RandomNumberGenerator
            .GetBytes(20);

    var charsOut = bytes
        .Select(b => chars[b % chars.Length])
        .ToArray();

    return string.Join(
        '-',
        Enumerable
            .Range(0, 4)
            .Select(i =>
                new string(
                    charsOut,
                    i * 5,
                    5
                )
            )
    );
}


// ============================================================
// MODELOS
// ============================================================

record LicenseCheck(
    string Hwid,
    string LicenseKey
);

record CreateLicense(
    string? Customer,
    string? LicenseKey,
    string? Hwid,
    bool Active = true,
    DateTimeOffset? ExpiresAt = null
);

record UpdateLicense(
    bool? Active,
    string? Customer,
    string? Hwid,
    bool? ClearHwid,
    bool? SetExpiry,
    DateTimeOffset? ExpiresAt
);

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
