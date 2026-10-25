namespace BaruchsTreks.Users;

/// <summary>Uživatel z tabulky uživatelů: PartitionKey <c>user</c>, RowKey = <c>UserName.ToLowerInvariant()</c>.</summary>
public sealed record AppUser(string UserName, string PasswordHash, bool IsAdmin, string Created);
