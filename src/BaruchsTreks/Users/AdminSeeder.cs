using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Users;

/// <summary>Jeden administrátor z konfigurace (<c>Admin:Users:N:UserName|Password</c>).</summary>
public sealed class AdminCredential
{
    public string? UserName { get; set; }
    public string? Password { get; set; }
}

/// <summary>Sekce <c>Admin</c> v konfiguraci: účty, které se při startu založí nebo srovnají s konfigurací.</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    public List<AdminCredential> Users { get; set; } = [];
}

/// <summary>
/// Při startu založí administrátory z konfigurace. Prázdné položky přeskočí, existující uživatele nezdvojí.
/// Pokud uložený hash už nesouhlasí s nakonfigurovaným heslem, přepíše se (zdrojem pravdy jsou GitHub secrets).
/// </summary>
public sealed class AdminSeeder(
    IUserRepository users,
    IPasswordHasher<AppUser> hasher,
    IOptions<AdminOptions> options,
    TimeProvider time)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var credential in options.Value.Users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(credential.UserName) || string.IsNullOrWhiteSpace(credential.Password))
                continue;

            var existing = await users.FindAsync(credential.UserName);
            if (existing is null)
            {
                var created = new AppUser(credential.UserName, "", IsAdmin: true, Now());
                await users.UpsertAsync(created with { PasswordHash = hasher.HashPassword(created, credential.Password) });
            }
            else if (hasher.VerifyHashedPassword(existing, existing.PasswordHash, credential.Password) == PasswordVerificationResult.Failed)
            {
                await users.UpsertAsync(existing with { PasswordHash = hasher.HashPassword(existing, credential.Password) });
            }
        }
    }

    private string Now() => time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture);
}
