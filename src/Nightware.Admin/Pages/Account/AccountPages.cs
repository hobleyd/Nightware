using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Nightware.Admin.Auth;

namespace Nightware.Admin.Pages.Account;

/// <summary>Starts the Keycloak sign-in; returns only to a local URL.</summary>
public sealed class SignInModel : PageModel
{
    public IActionResult OnGet(string? returnUrl) =>
        Challenge(new AuthenticationProperties { RedirectUri = LocalOrHome(Url, returnUrl) }, OpenIdConnectDefaults.AuthenticationScheme);

    internal static string LocalOrHome(IUrlHelper url, string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && url.IsLocalUrl(returnUrl) ? returnUrl : url.Content("~/");
}

/// <summary>
/// Sends the admin back to Keycloak to prove who they are again (<c>max_age=0</c>), for a destructive action.
/// </summary>
public sealed class ReauthenticateModel : PageModel
{
    public IActionResult OnGet(string? returnUrl)
    {
        var properties = new AuthenticationProperties { RedirectUri = SignInModel.LocalOrHome(Url, returnUrl) };
        properties.Items[AdminAuthentication.ForceReauthenticationItem] = "1";
        return Challenge(properties, OpenIdConnectDefaults.AuthenticationScheme);
    }
}

/// <summary>
/// The signed-in person changes their own password on Keycloak's own screen (application-initiated action
/// <c>UPDATE_PASSWORD</c>); the console never sees the password. POST starts it; GET shows how it went.
/// </summary>
public sealed class PasswordModel : PageModel
{
    public string? Status { get; private set; }

    public void OnGet(string? status) =>
        Status = status is "success" or "cancelled" or "error" ? status : null;

    public IActionResult OnPost()
    {
        var properties = new AuthenticationProperties { RedirectUri = Url.Page("/Account/Password") };
        properties.Items[AdminAuthentication.RequiredActionItem] = AdminAuthentication.UpdatePasswordAction;
        return Challenge(properties, OpenIdConnectDefaults.AuthenticationScheme);
    }
}

/// <summary>Ends the console session and the Keycloak session (RP-initiated logout). POST only.</summary>
public sealed class SignOutModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public IActionResult OnPost() =>
        SignOut(new AuthenticationProperties { RedirectUri = Url.Page("/Account/SignedOut") }, CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme);
}

public sealed class SignedOutModel : PageModel
{
    public void OnGet()
    {
    }
}

/// <summary>Signed in, but not an administrator of anything (or the sign-in was refused).</summary>
public sealed class DeniedModel : PageModel
{
    public string? Reason { get; private set; }

    public void OnGet(string? reason)
    {
        Reason = reason;
        Response.StatusCode = StatusCodes.Status403Forbidden;
    }
}
