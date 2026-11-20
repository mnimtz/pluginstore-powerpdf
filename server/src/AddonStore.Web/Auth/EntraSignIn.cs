using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AddonStore.Web.Auth;

/// <summary>
/// Wiring of "Sign in with Microsoft" (docs/entra-setup.md). Everything here is skipped while
/// <see cref="EntraSettings.Enabled"/> is false.
/// <para>Flow: GET /Account/Microsoft starts the OpenID Connect challenge (authorization code flow with PKCE,
/// response_mode=query); Entra returns to GET /signin-oidc; the handler redeems the code with the client secret,
/// validates the ID token (signature, issuer of an allowed tenant, audience, lifetime, nonce) and
/// <see cref="EntraIdentity.Read"/> applies the trust rules; the trimmed identity lands in the short-lived
/// external cookie; GET /Account/Microsoft/Callback finds, links or creates the account
/// (<see cref="EntraAccountService"/>) and signs it in with the normal store cookie.</para>
/// <para>Why query and not form_post: the answer from Entra is then a top-level GET navigation, so the
/// correlation and nonce cookies work with SameSite=Lax (form_post is a cross-site POST and needs SameSite=None),
/// and nothing in the Content-Security-Policy has to change. The challenge is a GET link for the same reason:
/// a form POST answered by a redirect to login.microsoftonline.com would be blocked by form-action 'self'.</para>
/// </summary>
public static class EntraSignIn
{
    /// <summary>Raised from the token validation; ends as /Account/Login?entra=&lt;code&gt;.</summary>
    private sealed class RejectedException(EntraRejection rejection) : Exception("Entra sign-in refused: " + rejection)
    {
        public EntraRejection Rejection { get; } = rejection;
    }

    public static string Code(EntraRejection r) => r switch
    {
        EntraRejection.Tenant => "tenant",
        EntraRejection.Email => "email",
        _ => "failed",
    };

    public static void AddEntraSignIn(this AuthenticationBuilder auth, EntraSettings s, bool development)
    {
        if (!s.Enabled) return;
        var cookiePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        auth.Services.AddScoped<EntraAccountService>();
        // the external cookie only carries the trimmed identity for a few minutes between /signin-oidc and the callback
        auth.Services.ConfigureExternalCookie(o =>
        {
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = cookiePolicy;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
        auth.AddOpenIdConnect(EntraSettings.Scheme, EntraAccountService.ProviderDisplayName, o =>
        {
            o.SignInScheme = IdentityConstants.ExternalScheme;
            o.Authority = s.Authority;
            o.ClientId = s.ClientId;
            o.ClientSecret = s.ClientSecret;
            o.CallbackPath = EntraSettings.CallbackPath;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.UsePkce = true;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.Scope.Add("email");
            o.Prompt = "select_account";
            o.MapInboundClaims = false;          // raw claim names (tid, oid, upn, ...)
            o.SaveTokens = false;                // no tokens in any cookie
            o.GetClaimsFromUserInfoEndpoint = false;
            o.RequireHttpsMetadata = true;
            o.DisableTelemetry = true;
            o.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
            o.TokenValidationParameters.ValidateIssuer = true;
            o.TokenValidationParameters.ValidIssuers = s.ValidIssuers.ToList();
            o.TokenValidationParameters.NameClaimType = "name";
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = cookiePolicy;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SecurePolicy = cookiePolicy;
            o.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = ctx =>
                {
                    // the registered redirect URI is https; behind a proxy that hides the scheme the request may look like http
                    if (!development && ctx.ProtocolMessage.RedirectUri?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true)
                        ctx.ProtocolMessage.RedirectUri = "https://" + ctx.ProtocolMessage.RedirectUri["http://".Length..];
                    return Task.CompletedTask;
                },
                OnTokenValidated = ctx =>
                {
                    var (identity, rejection) = EntraIdentity.Read(ctx.Principal?.Claims ?? Enumerable.Empty<System.Security.Claims.Claim>(), s.AllowedTenants);
                    if (identity is null) ctx.Fail(new RejectedException(rejection));
                    else ctx.Principal = identity.ToPrincipal();   // only key, address and name reach the cookie
                    return Task.CompletedTask;
                },
                OnRemoteFailure = ctx =>
                {
                    var code = ctx.Failure switch
                    {
                        RejectedException r => Code(r.Rejection),
                        SecurityTokenInvalidIssuerException => "tenant",
                        _ => "failed",
                    };
                    ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AddonStore.Entra")
                        .LogWarning("Sign in with Microsoft refused ({Code}): {Reason}", code, LogSafe.Of(ctx.Failure?.Message, 300));
                    ctx.Response.Redirect("/Account/Login?entra=" + code);
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                },
            };
        });
    }

    public static void MapEntraSignIn(this WebApplication app, EntraSettings s)
    {
        if (!s.Enabled) return;

        // A plain link starts the sign-in (see the class remarks); starting it cannot sign anybody in by itself,
        // the correlation cookie and state bind the answer to this browser.
        app.MapGet(EntraSettings.ChallengePath, () =>
            Results.Challenge(new AuthenticationProperties { RedirectUri = EntraSettings.CompletePath }, new[] { EntraSettings.Scheme }));

        app.MapGet(EntraSettings.CompletePath, async (HttpContext ctx, EntraAccountService accounts, UserManager<AppUser> users,
                                                      SignInManager<AppUser> signIn, AuditService audit, NotificationService notify) =>
        {
            var external = await ctx.AuthenticateAsync(IdentityConstants.ExternalScheme);
            await ctx.SignOutAsync(IdentityConstants.ExternalScheme);
            var id = external.Succeeded ? EntraIdentity.FromPrincipal(external.Principal) : null;
            if (id is null) return Results.LocalRedirect("/Account/Login?entra=failed");

            var outcome = await accounts.ResolveAsync(id);
            var user = outcome.User;
            if (user is null) return Results.LocalRedirect("/Account/Login?entra=failed");
            switch (outcome.Kind)
            {
                case EntraAccountService.Kind.Created:
                    // the same record and notice as a manual access request (Pages/Account/Register.cshtml.cs)
                    await audit.LogAsync(user.DisplayName, "user.access-requested", user.Email ?? "", "Sign in with Microsoft");
                    await notify.NotifyStaffAsync("AccessRequest",
                        "[Add-on Store] New access request",
                        $"<p><b>{System.Net.WebUtility.HtmlEncode(user.DisplayName)}</b> ({System.Net.WebUtility.HtmlEncode(user.Email)}) requests publisher access.</p>" +
                        "<p>Signed in with a Microsoft work account; no reason given.</p>" +
                        $"<p><a href=\"{await notify.BaseUrlAsync()}/Admin/Users\">Review it on the Users page</a>.</p>");
                    return Results.LocalRedirect("/Account/Login?registered=1");
                case EntraAccountService.Kind.Linked:
                    await audit.LogAsync(user.DisplayName, "user.entra-linked", user.Email ?? user.Id, $"tenant {id.TenantId}");
                    break;
            }

            if (EntraAccountService.Gate(user, await users.IsLockedOutAsync(user)) is { } refused)
                return Results.LocalRedirect("/Account/Login?entra=" + refused);
            await signIn.SignInAsync(user, isPersistent: true, authenticationMethod: EntraSettings.Scheme);
            // same landing page as the password sign-in (Pages/Account/Login.cshtml.cs)
            return Results.LocalRedirect(Shell.Sidebar(ctx) ? "/Start" : "/Dashboard");
        });
    }
}
