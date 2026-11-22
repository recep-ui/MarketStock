using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Infrastructure.Persistence;
using IdentityOptions = PazaryeriHub.Application.Auth.IdentityOptions;

namespace PazaryeriHub.Infrastructure.Auth;

/// <summary>Panel kullanıcıları (Dapper). Parolalar ASP.NET Core <see cref="PasswordHasher{TUser}"/> (PBKDF2) ile hash'lenir.</summary>
public sealed class UserService(
    SqlConnectionFactory connectionFactory,
    IOptions<IdentityOptions> options,
    TimeProvider clock,
    ILogger<UserService> logger) : IUserService
{
    private static readonly PasswordHasher<string> Hasher = new();

    public async Task<LoginResult> ValidateAsync(string userName, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            return new(null, false);
        }

        await using var connection = await OpenAsync(ct);
        var user = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            "SELECT * FROM dbo.AppUser WHERE KullaniciAdi = @userName", new { userName = userName.Trim() }, cancellationToken: ct));
        var now = clock.GetUtcNow().UtcDateTime;

        if (user is null || !user.Aktif)
        {
            // Zamanlama farkıyla kullanıcı adı tahmin edilmesin diye yine de hash doğrulaması yapılır.
            Hasher.VerifyHashedPassword("", DummyHash, password);
            return new(null, false);
        }
        if (user.KilitBitis is { } lockedUntil && lockedUntil > now)
        {
            return new(null, true);
        }

        var verification = Hasher.VerifyHashedPassword(user.KullaniciAdi, user.SifreHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            var failures = user.BasarisizGiris + 1;
            var lockout = failures >= options.Value.MaxFailedLogins ? now.AddMinutes(options.Value.LockoutMinutes) : (DateTime?)null;
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.AppUser SET BasarisizGiris = @failures, KilitBitis = @lockout WHERE Id = @Id",
                new { failures = lockout is null ? failures : 0, lockout, user.Id }, cancellationToken: ct));
            if (lockout is not null)
            {
                logger.LogWarning("Kullanıcı {KullaniciAdi} {Adet} hatalı giriş nedeniyle {Dakika} dk kilitlendi.", user.KullaniciAdi, failures, options.Value.LockoutMinutes);
            }
            return new(null, lockout is not null);
        }

        var newHash = verification == PasswordVerificationResult.SuccessRehashNeeded ? Hasher.HashPassword(user.KullaniciAdi, password) : user.SifreHash;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.AppUser SET BasarisizGiris = 0, KilitBitis = NULL, SonGiris = @now, SifreHash = @newHash WHERE Id = @Id",
            new { now, newHash, user.Id }, cancellationToken: ct));
        return new(ToDto(user with { SonGiris = now }), false);
    }

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<UserRow>(new CommandDefinition("SELECT * FROM dbo.AppUser ORDER BY KullaniciAdi", cancellationToken: ct));
        return rows.Select(ToDto).ToList();
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var v = new Validator();
        v.Required("userName", request.UserName, 100).Required("fullName", request.FullName, 200);
        ValidateRole(v, request.Role);
        ValidatePassword(v, request.Password);
        v.ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        try
        {
            var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                INSERT dbo.AppUser (KullaniciAdi, AdSoyad, Rol, SifreHash) OUTPUT inserted.Id VALUES (@userName, @fullName, @role, @hash)
                """, new
                {
                    userName = request.UserName.Trim(),
                    fullName = request.FullName.Trim(),
                    role = request.Role,
                    hash = Hasher.HashPassword(request.UserName.Trim(), request.Password)
                }, cancellationToken: ct));
            return await GetAsync(connection, id, ct);
        }
        catch (SqlException ex) when (ex.Number is DbErrors.UniqueConstraint or DbErrors.UniqueIndex)
        {
            throw new ConflictException("Bu kullanıcı adı zaten kayıtlı.", ex);
        }
    }

    public async Task<UserDto> UpdateAsync(int id, UpdateUserRequest request, int? actingUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var v = new Validator();
        v.Required("fullName", request.FullName, 200);
        ValidateRole(v, request.Role);
        v.ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var current = await LockAsync(connection, tx, id, ct);
        var losesAdmin = current.Rol == Roles.Admin && current.Aktif && (request.Role != Roles.Admin || !request.Active);
        if (losesAdmin && actingUserId == id)
        {
            throw new ConflictException("Kendi hesabınızı pasife alamaz veya yönetici yetkinizi kaldıramazsınız. Başka bir yönetici yapmalı.");
        }
        if (losesAdmin)
        {
            await EnsureAnotherActiveAdminAsync(connection, tx, id, ct);
        }
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.AppUser SET AdSoyad = @fullName, Rol = @role, Aktif = @active WHERE Id = @id",
            new { id, fullName = request.FullName.Trim(), role = request.Role, active = request.Active }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return await GetAsync(connection, id, ct);
    }

    public async Task DeleteAsync(int id, int? actingUserId, CancellationToken ct)
    {
        if (actingUserId == id)
        {
            throw new ConflictException("Kendi hesabınızı silemezsiniz. Başka bir yönetici silmeli.");
        }
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var current = await LockAsync(connection, tx, id, ct);
        if (current.Rol == Roles.Admin && current.Aktif)
        {
            await EnsureAnotherActiveAdminAsync(connection, tx, id, ct);
        }
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM dbo.AppUser WHERE Id = @id", new { id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        logger.LogInformation("Kullanıcı silindi: {KullaniciAdi} (Id {Id}).", current.KullaniciAdi, id);
    }

    public async Task<string?> GetActiveRoleAsync(int id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Rol FROM dbo.AppUser WHERE Id = @id AND Aktif = 1", new { id }, cancellationToken: ct));
    }

    private static async Task<UserRow> LockAsync(SqlConnection connection, SqlTransaction tx, int id, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            "SELECT * FROM dbo.AppUser WITH (UPDLOCK, ROWLOCK) WHERE Id = @id", new { id }, tx, cancellationToken: ct))
        ?? throw new NotFoundException($"Kullanıcı bulunamadı (Id={id}).");

    /// <summary>Sistemde en az bir aktif yönetici kalmalı (yoksa panele kimse yönetici olarak giremez).</summary>
    private static async Task EnsureAnotherActiveAdminAsync(SqlConnection connection, SqlTransaction tx, int exceptId, CancellationToken ct)
    {
        var others = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.AppUser WITH (UPDLOCK, HOLDLOCK) WHERE Rol = @admin AND Aktif = 1 AND Id <> @exceptId",
            new { admin = Roles.Admin, exceptId }, tx, cancellationToken: ct));
        if (others == 0)
        {
            throw new ConflictException("Son aktif yönetici silinemez, pasife alınamaz veya yetkisi düşürülemez. Önce başka bir yönetici ekleyin.");
        }
    }

    public async Task SetPasswordAsync(int id, string password, CancellationToken ct)
    {
        var v = new Validator();
        ValidatePassword(v, password);
        v.ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        var name = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT KullaniciAdi FROM dbo.AppUser WHERE Id = @id", new { id }, cancellationToken: ct))
            ?? throw new NotFoundException($"Kullanıcı bulunamadı (Id={id}).");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.AppUser SET SifreHash = @hash, BasarisizGiris = 0, KilitBitis = NULL WHERE Id = @id",
            new { id, hash = Hasher.HashPassword(name, password) }, cancellationToken: ct));
    }

    /// <summary>Tohum kullanıcıları uygular (bkz. <see cref="IdentityOptions"/>). Parola değerleri loglanmaz.</summary>
    public async Task SeedAsync(IHostEnvironment environment, CancellationToken ct)
    {
        var seeds = options.Value.SeedUsers.Where(s => !string.IsNullOrWhiteSpace(s.UserName) && !string.IsNullOrEmpty(s.Password)).ToList();
        if (seeds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(ct);
        var anyUser = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM dbo.AppUser", cancellationToken: ct)) > 0;
        var upsert = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (anyUser && !upsert)
        {
            return; // üretimde tohum yalnızca ilk kurulumda
        }

        foreach (var seed in seeds)
        {
            if (!Roles.All.Contains(seed.Role))
            {
                logger.LogWarning("Tohum kullanıcı {KullaniciAdi} atlandı: geçersiz rol.", seed.UserName);
                continue;
            }
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.AppUser SET AdSoyad = @fullName, Rol = @role, SifreHash = @hash, Aktif = 1, BasarisizGiris = 0, KilitBitis = NULL
                 WHERE KullaniciAdi = @userName;
                IF @@ROWCOUNT = 0
                    INSERT dbo.AppUser (KullaniciAdi, AdSoyad, Rol, SifreHash) VALUES (@userName, @fullName, @role, @hash);
                """, new
                {
                    userName = seed.UserName.Trim(),
                    fullName = string.IsNullOrWhiteSpace(seed.FullName) ? seed.UserName.Trim() : seed.FullName.Trim(),
                    role = seed.Role,
                    hash = Hasher.HashPassword(seed.UserName.Trim(), seed.Password)
                }, cancellationToken: ct));
        }
        logger.LogInformation("{Adet} tohum kullanıcı uygulandı.", seeds.Count);
    }

    private void ValidatePassword(Validator v, string? password) =>
        v.Must(password is not null && password.Length >= options.Value.MinPasswordLength, "password",
            $"Parola en az {options.Value.MinPasswordLength} karakter olmalı.");

    private static void ValidateRole(Validator v, string? role) =>
        v.Must(role is not null && Roles.All.Contains(role), "role", $"Rol şunlardan biri olmalı: {string.Join(", ", Roles.All)}.");

    private static async Task<UserDto> GetAsync(SqlConnection connection, int id, CancellationToken ct) =>
        ToDto(await connection.QuerySingleAsync<UserRow>(new CommandDefinition("SELECT * FROM dbo.AppUser WHERE Id = @id", new { id }, cancellationToken: ct)));

    private static UserDto ToDto(UserRow u) =>
        new(u.Id, u.KullaniciAdi, u.AdSoyad, u.Rol, u.Aktif, u.SonGiris is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null);

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>Var olmayan kullanıcıda da aynı maliyette doğrulama için sabit hash.</summary>
    private static readonly string DummyHash = Hasher.HashPassword("", Guid.NewGuid().ToString());

    private sealed record UserRow
    {
        public int Id { get; init; }
        public string KullaniciAdi { get; init; } = "";
        public string AdSoyad { get; init; } = "";
        public string Rol { get; init; } = "";
        public string SifreHash { get; init; } = "";
        public bool Aktif { get; init; }
        public int BasarisizGiris { get; init; }
        public DateTime? KilitBitis { get; init; }
        public DateTime? SonGiris { get; init; }
        public DateTime OlusturmaTarihi { get; init; }
    }
}
