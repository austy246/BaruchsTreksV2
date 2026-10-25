using BaruchsTreks.Storage;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BaruchsTreks.Users;

public static class AuthSetup
{
    public const string AdminPolicy = "AdminOnly";
    public const string CookieName = "baruchstreks_auth";
    public const string LoginPath = "/accounts/login/";
    public const string DefaultRedirect = "/trips/";

    public static IServiceCollection AddAppAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdminOptions>().BindConfiguration(AdminOptions.SectionName);
        services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<AdminSeeder>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.LoginPath = LoginPath;
                options.ReturnUrlParameter = "next";
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
                // Anonym i přihlášený ne-admin dostanou stejné relativní přesměrování na přihlášení, nikdy 401/403.
                options.Events.OnRedirectToLogin = RedirectToLogin;
                options.Events.OnRedirectToAccessDenied = RedirectToLogin;
            });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IWebHostEnvironment>((options, env) =>
                options.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);

        // Antiforgery cookie: HttpOnly zůstává výchozí, mimo Development navíc vždy Secure (provoz jde přes HTTPS za Caddy).
        services.AddOptions<AntiforgeryOptions>()
            .Configure<IWebHostEnvironment>((options, env) =>
            {
                if (!env.IsDevelopment())
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(AppClaims.IsAdmin, "true"));

        // Klíče pro cookie a antiforgery jsou v blobu, aby přežily restart i nasazení nového kontejneru.
        // Akceptované riziko: klíče jsou v privátním kontejneru baruchstreks-keys nešifrované (XmlKeyManager
        // proto loguje varování). Kdo má čtecí přístup ke storage, mohl by podvrhnout cookie, ale to už
        // znamená plný přístup k datům.
        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        var connectionString = configuration.GetConnectionString("Storage")
            ?? throw new InvalidOperationException("Missing configuration value ConnectionStrings:Storage.");
        services.AddDataProtection()
            .SetApplicationName("BaruchsTreks")
            .PersistKeysToAzureBlobStorage(connectionString, storage.KeysContainer, "keys.xml");

        return services;
    }

    private static Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        var next = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        context.Response.Redirect(QueryHelpers.AddQueryString(LoginPath, "next", next));
        return Task.CompletedTask;
    }
}
