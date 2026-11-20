using System.Text.Encodings.Web;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AddonStore.Web.Auth;

/// <summary>
/// Wiring of "Sign in with Microsoft" (docs/entra-setup.md). The OpenID Connect scheme is always registered;
/// while <see cref="EntraRuntime.Current"/> is off, its handler ignores every request (/signin-oidc ends in 404
/// like any unknown address) and the routes below answer 404, exactly as without any configuration.
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

    public static void AddEntraSignIn(this AuthenticationBuilder auth, bool development)
    {
        var services = auth.Services;
        var cookiePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        services.AddSingleton<EntraRuntime>();
        services.AddScoped<EntraAccountService>();
        // the external cookie only carries the trimmed identity for a few minutes between /signin-oidc and the callback
        services.ConfigureExternalCookie(o =>
        {
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = cookiePolicy;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
        // what AddOpenIdConnect registers, with the handler that knows when the feature is off
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>());
        services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>>(sp => new EntraOptionsSetup(sp.GetRequiredService<EntraRuntime>(), cookiePolicy, development));
        auth.AddRemoteScheme<OpenIdConnectOptions, EntraOidcHandler>(EntraSettings.Scheme, EntraAccountService.ProviderDisplayName, configureOptions: null);
    }

    /// <summary>
    /// Builds the options of the "Entra" scheme from the configuration current at that moment. The options monitor
    /// caches the result; <see cref="EntraRuntime.Apply"/> removes the cached entry when the configuration changes.
    /// </summary>
    internal sealed class EntraOptionsSetup(EntraRuntime runtime, CookieSecurePolicy cookiePolicy, bool development) : IConfigureNamedOptions<OpenIdConnectOptions>
    {
        public void Configure(OpenIdConnectOptions o) => Configure(Options.DefaultName, o);

        public void Configure(string? name, OpenIdConnectOptions o)
        {
            if (name != EntraSettings.Scheme) return;
            var s = runtime.Current;
            o.SignInScheme = IdentityConstants.ExternalScheme;
            o.CallbackPath = EntraSettings.CallbackPath;
            if (!s.Enabled)
            {
                // valid but inert options: the handler ignores every request while off, and an empty static
                // configuration means no metadata is ever fetched
                o.ClientId = "off";
                o.Configuration = new OpenIdConnectConfiguration();
                return;
            }
            o.Authority = s.Authority;
            o.ClientId = s.ClientId;
            o.ClientSecret = s.ClientSecret;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.UsePkce = true;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.Scope.Add("email");
            o.Prompt = "select_account";
            o.MapInboundClaims = false;          // raw claim names (tid, oid, preferred_username, ...)
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
            var allowed = s.AllowedTenants;
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
                    var (identity, rejection) = EntraIdentity.Read(ctx.Principal?.Claims ?? Enumerable.Empty<System.Security.Claims.Claim>(), allowed);
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
        }
    }

    /// <summary>The OpenID Connect handler, inactive while "Sign in with Microsoft" is off.</summary>
    public sealed class EntraOidcHandler(IOptionsMonitor<OpenIdConnectOptions> options, ILoggerFactory logger, HtmlEncoder htmlEncoder,
                                         UrlEncoder encoder, EntraRuntime runtime)
        : OpenIdConnectHandler(options, logger, htmlEncoder, encoder)
    {
        // off: neither /signin-oidc nor the sign-out paths of the handler are answered
        public override Task<bool> HandleRequestAsync() =>
            runtime.Current.Enabled && Options.ClientId == runtime.Current.ClientId ? base.HandleRequestAsync() : Task.FromResult(false);

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            if (runtime.Current.Enabled) return base.HandleChallengeAsync(properties);
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
    }

    public static void MapEntraSignIn(this WebApplication app)
    {
        // A plain link starts the sign-in (see the class remarks); starting it cannot sign anybody in by itself,
        // the correlation cookie and state bind the answer to this browser.
        app.MapGet(EntraSettings.ChallengePath, async (HttpContext ctx, EntraRuntime runtime, ILoggerFactory logs) =>
        {
            if (!(await runtime.GetAsync()).Enabled) return Results.NotFound();
            // S2.1.0: a wrong tenant or an unreachable Microsoft endpoint must not end in a server error page
            try
            {
                await ctx.ChallengeAsync(EntraSettings.Scheme, new AuthenticationProperties { RedirectUri = EntraSettings.CompletePath });
                return Results.Empty;
            }
            catch (InvalidOperationException ex)
            {
                logs.CreateLogger("AddonStore.Entra").LogWarning(ex, "Sign in with Microsoft: the Entra configuration could not be loaded (check the directory on the Settings page or Entra:TenantId / Entra:Authority)");
                return Results.LocalRedirect("/Account/Login?entra=failed");
            }
        });

        app.MapGet(EntraSettings.CompletePath, async (HttpContext ctx, EntraRuntime runtime, EntraAccountService accounts, UserManager<AppUser> users,
                                                      SignInManager<AppUser> signIn, AuditService audit, NotificationService notify) =>
        {
            if (!(await runtime.GetAsync()).Enabled) return Results.NotFound();
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
                    // S2.1.0: the store never confirmed e-mail addresses, so an account found by address may have been
                    // registered by someone else. The Microsoft sign-in proves the address: every other session of the
                    // account ends (new security stamp) and the address is told that a Microsoft account was linked.
                    await users.UpdateSecurityStampAsync(user);
                    if (!string.IsNullOrEmpty(user.Email))
                        await notify.SendDirectAsync(user.Email, "[Add-on Store] Microsoft account linked",
                            $"<p>A Microsoft work account was linked to your Add-on Store account <b>{System.Net.WebUtility.HtmlEncode(user.DisplayName)}</b> " +
                            "with its first sign-in. Other open sessions of this account were signed out.</p>" +
                            $"<p>If this was not you, contact an administrator: <a href=\"{await notify.BaseUrlAsync()}\">{System.Net.WebUtility.HtmlEncode(await notify.BaseUrlAsync())}</a>.</p>",
                            "EntraLinked");
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
