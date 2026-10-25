using Azure.Data.Tables;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Tests.Integration;

public class AdminSeederTests(AzuriteFixture azurite)
{
    private static readonly PasswordHasher<AppUser> Hasher = new();

    private static async Task<(TestStorage Storage, UserRepository Users, AdminSeeder Seeder)> CreateAsync(
        AzuriteFixture azurite, params (string? UserName, string? Password)[] admins)
    {
        var storage = new TestStorage(azurite);
        await storage.UsersTableClient.CreateIfNotExistsAsync(TestContext.Current.CancellationToken);
        var users = new UserRepository(storage.Tables, Options.Create(storage.Options));
        var options = new AdminOptions
        {
            Users = admins.Select(a => new AdminCredential { UserName = a.UserName, Password = a.Password }).ToList(),
        };
        return (storage, users, new AdminSeeder(users, Hasher, Options.Create(options), TimeProvider.System));
    }

    private static bool PasswordMatches(AppUser user, string password) =>
        Hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;

    private static async Task<List<TableEntity>> AllRowsAsync(TestStorage storage) =>
        await storage.UsersTableClient
            .QueryAsync<TableEntity>(cancellationToken: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Seeder_CreatesBothAdmins()
    {
        var ct = TestContext.Current.CancellationToken;
        var (storage, users, seeder) = await CreateAsync(azurite, ("Austy", "pw-1"), ("baruch", "pw-2"));

        await seeder.SeedAsync(ct);

        var first = await users.FindAsync("austy");
        var second = await users.FindAsync("BARUCH");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("Austy", first.UserName);
        Assert.True(first.IsAdmin);
        Assert.True(second.IsAdmin);
        Assert.True(PasswordMatches(first, "pw-1"));
        Assert.True(PasswordMatches(second, "pw-2"));
        Assert.False(PasswordMatches(first, "pw-2"));
        Assert.NotEqual("pw-1", first.PasswordHash);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}$", first.Created);

        var rows = await AllRowsAsync(storage);
        Assert.Equal(["austy", "baruch"], rows.Select(r => r.RowKey).Order().ToArray());
        Assert.All(rows, r => Assert.Equal("user", r.PartitionKey));
    }

    [Theory]
    [InlineData("", "pw-2")]
    [InlineData("baruch", "")]
    [InlineData(null, null)]
    public async Task Seeder_SkipsEmptyEntries(string? userName, string? password)
    {
        var ct = TestContext.Current.CancellationToken;
        var (storage, users, seeder) = await CreateAsync(azurite, ("austy", "pw-1"), (userName, password));

        await seeder.SeedAsync(ct);

        Assert.NotNull(await users.FindAsync("austy"));
        Assert.Single(await AllRowsAsync(storage));
    }

    [Fact]
    public async Task Seeder_UpdatesHashWhenPasswordChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (storage, users, seeder) = await CreateAsync(azurite, ("austy", "pw-old"));
        await seeder.SeedAsync(ct);
        var before = await users.FindAsync("austy");

        var changed = new AdminSeeder(
            users, Hasher,
            Options.Create(new AdminOptions { Users = [new AdminCredential { UserName = "austy", Password = "pw-new" }] }),
            TimeProvider.System);
        await changed.SeedAsync(ct);

        var after = await users.FindAsync("austy");
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.True(PasswordMatches(after, "pw-new"));
        Assert.False(PasswordMatches(after, "pw-old"));
        Assert.True(after.IsAdmin);
        Assert.Equal(before.Created, after.Created);
        Assert.Single(await AllRowsAsync(storage));
    }

    [Fact]
    public async Task Seeder_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (storage, users, seeder) = await CreateAsync(azurite, ("austy", "pw-1"), ("baruch", "pw-2"));
        await seeder.SeedAsync(ct);
        var before = await users.FindAsync("austy");

        await seeder.SeedAsync(ct);

        var after = await users.FindAsync("austy");
        Assert.Equal(before, after);
        Assert.Equal(2, (await AllRowsAsync(storage)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a#b")]
    public async Task Find_InvalidKeyCharacters_ReturnsNull(string userName)
    {
        var (_, users, _) = await CreateAsync(azurite);

        Assert.Null(await users.FindAsync(userName));
    }
}
