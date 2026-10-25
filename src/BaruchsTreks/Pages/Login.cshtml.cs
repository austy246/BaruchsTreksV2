using System.Security.Claims;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

public class LoginModel(IUserRepository users, IPasswordHasher<AppUser> hasher) : PageModel
{
    [BindProperty(Name = "next", SupportsGet = true)]
    public string? Next { get; set; }

    [BindProperty(Name = "username")]
    public string? UserName { get; set; }

    [BindProperty(Name = "password")]
    public string? Password { get; set; }

    public bool LoginFailed { get; private set; }

    /// <summary>Hlášení z <c>auth_views.py</c> (v Djangu <c>messages.error</c>), zobrazené nad formulářem.</summary>
    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = string.IsNullOrEmpty(UserName) ? null : await users.FindAsync(UserName);

        if (user is not null
            && !string.IsNullOrEmpty(Password)
            && hasher.VerifyHashedPassword(user, user.PasswordHash, Password) != PasswordVerificationResult.Failed)
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, user.UserName),
                    new Claim(AppClaims.IsAdmin, user.IsAdmin ? "true" : "false"),
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });

            // Jen lokální adresy; cokoli jiného (https://evil.example, //evil.example) vede na výchozí stránku.
            return LocalRedirect(Url.IsLocalUrl(Next) ? Next! : AuthSetup.DefaultRedirect);
        }

        LoginFailed = true;
        ErrorMessage = user is not null
            ? "Password is incorrect. Please try again."
            : "Username does not exist. Please check your username.";
        return Page();
    }
}
