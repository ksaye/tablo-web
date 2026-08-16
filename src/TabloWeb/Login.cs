using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace TabloWeb;

/// <summary>
/// Sign-in for the site.
///
/// There is no separate account system: you sign in with the Tablo account itself. A successful
/// login against the Tablo cloud is what proves you are allowed in, and it is also what connects
/// the server to the DVR — one credential, one thing to remember.
///
/// Everything except the login page needs a session, including the static page and the HLS
/// segments, because the segments *are* the content. The gate therefore runs ahead of the
/// static-file middleware, which does not consult authorization policies at all.
/// </summary>
public static class Login
{
    public const string PagePath = "/login";
    private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>
    /// Run without a sign-in gate. Only sensible on a network where everyone who can reach the
    /// port is already welcome to watch — the site has no other protection.
    /// </summary>
    public static bool NoLogin =>
        Environment.GetEnvironmentVariable("TABLOWEB_NO_LOGIN") is "1" or "true";

    /// <summary>Paths that have to work before there is a session.</summary>
    private static bool IsPublic(PathString path) =>
        path.StartsWithSegments(PagePath) ||
        path.StartsWithSegments("/api/login") ||
        path.StartsWithSegments("/healthz");

    public static void AddTabloLogin(this WebApplicationBuilder builder)
    {
        // Session cookies are signed with data-protection keys. They must outlive a restart or
        // everyone is silently signed out every time the container is recreated.
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(CredentialStore.KeyDirectory))
            .SetApplicationName("TabloWeb");

        builder.Services.AddAuthentication(Scheme).AddCookie(o =>
        {
            o.Cookie.Name = "tablo.session";
            o.Cookie.HttpOnly = true;
            // Default to SameAsRequest, not Always: plenty of installations are plain http on a
            // home network, and a cookie marked Secure can never be set over one. Behind TLS,
            // set TABLOWEB_COOKIE_SECURE=1.
            o.Cookie.SecurePolicy =
                Environment.GetEnvironmentVariable("TABLOWEB_COOKIE_SECURE") is "1" or "true"
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
            o.Cookie.SameSite = SameSiteMode.Lax;
            // Watching television should not mean signing in every day.
            o.ExpireTimeSpan = TimeSpan.FromDays(30);
            o.SlidingExpiration = true;
            o.LoginPath = PagePath;
        });

        builder.Services.AddAuthorization();
    }

    public static void UseTabloLogin(this WebApplication app)
    {
        if (NoLogin)
        {
            app.Logger.LogWarning(
                "TABLOWEB_NO_LOGIN is set: there is no sign-in. Anyone who can reach this port can " +
                "browse and watch everything on the DVR.");
            return;
        }

        app.UseAuthentication();
        app.UseAuthorization();

        app.Use(async (context, next) =>
        {
            if (IsPublic(context.Request.Path) || context.User.Identity?.IsAuthenticated == true)
            {
                await next();
                return;
            }

            // An expired session must not answer a fetch with a login page — the browser would
            // quietly try to parse HTML as JSON. Say 401 and let the page redirect.
            if (context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/stream"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"Your session has expired.","signIn":"/login"}""");
                return;
            }

            context.Response.Redirect(PagePath + "?next=" +
                Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
        });
    }

    public static void MapLoginEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

        app.MapGet(PagePath, (HttpContext http, TabloSession tablo) =>
            http.User.Identity?.IsAuthenticated == true && !tablo.NeedsCredentials
                ? Results.Redirect("/")
                : Results.Content(Pages.LoginPage(tablo.NeedsCredentials), "text/html"));

        app.MapGet("/api/me", (HttpContext http, TabloSession tablo) => Results.Ok(new
        {
            loginRequired = !NoLogin,
            signedIn = NoLogin || http.User.Identity?.IsAuthenticated == true,
            name = http.User.FindFirst(ClaimTypes.Name)?.Value ?? tablo.AccountEmail,
            device = tablo.Device?.Name,
            canSaveCredentials = CredentialStore.SavingAllowed,
            credentialsSaved = CredentialStore.HasSavedFile
        }));

        // Sign in with the Tablo account. Also connects the server to the DVR, so this is both
        // the front door and the setup step.
        app.MapPost("/api/login", async (HttpContext http, LoginRequest request, TabloSession tablo,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            var log = loggers.CreateLogger("TabloWeb.Login");
            var who = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (!Throttle.Allow(who))
                return Results.Json(new { error = "Too many attempts. Wait a minute and try again." },
                    statusCode: StatusCodes.Status429TooManyRequests);

            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { error = "Enter the email and password for your Tablo account." });

            try
            {
                var credentials = new Credentials(request.Email.Trim(), request.Password, request.ServerId);
                var result = await tablo.SignInAsync(credentials, ct);

                // More than one Tablo answered and none was chosen. Ask, then come back here with
                // the same credentials plus a serverId.
                if (result.Devices is { Count: > 1 } && string.IsNullOrWhiteSpace(request.ServerId))
                    return Results.Ok(new { chooseDevice = true, devices = result.Devices });

                Throttle.Clear(who);

                if (request.Remember == true && CredentialStore.SavingAllowed)
                    CredentialStore.Save(credentials with { ServerId = result.ServerId }, log);

                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, credentials.Email)], Scheme);
                await http.SignInAsync(Scheme, new ClaimsPrincipal(identity),
                    new AuthenticationProperties { IsPersistent = true });

                log.LogInformation("{Email} signed in from {Address}", credentials.Email, who);
                return Results.Ok(new { ok = true, device = result.DeviceName });
            }
            catch (Exception ex)
            {
                log.LogWarning("Sign-in failed from {Address}: {Message}", who, ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
            }
        });

        app.MapPost("/api/signout", async (HttpContext http, TabloSession tablo, bool? forget) =>
        {
            await http.SignOutAsync(Scheme);
            // "Forget this Tablo" is the way back out of a saved credential — otherwise the next
            // visitor would find the server already connected with someone else's account.
            if (forget == true)
            {
                CredentialStore.Clear();
                tablo.Disconnect();
            }
            return Results.Ok(new { ok = true });
        });
    }

    /// <summary>
    /// A crude cap on password guessing. In-memory and per-address, which is the right weight
    /// for a single-household service: it stops a script, and it costs nothing.
    /// </summary>
    private static class Throttle
    {
        private const int Limit = 10;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
        private static readonly ConcurrentDictionary<string, (int Count, DateTime Since)> Attempts = new();

        public static bool Allow(string address)
        {
            var now = DateTime.UtcNow;
            var entry = Attempts.AddOrUpdate(address, _ => (1, now),
                (_, old) => now - old.Since > Window ? (1, now) : (old.Count + 1, old.Since));
            return entry.Count <= Limit;
        }

        public static void Clear(string address) => Attempts.TryRemove(address, out _);
    }
}

public sealed record LoginRequest(string Email, string Password, string? ServerId, bool? Remember);

/// <summary>Tablo account credentials, and which of the account's DVRs to use.</summary>
public sealed record Credentials(string Email, string Password, string? ServerId);

/// <summary>
/// Remembers the Tablo credentials on the server so a restart reconnects on its own — otherwise
/// nobody would see a guide until a human opened a browser and signed in.
///
/// The file is encrypted with the application's data-protection keys, which live beside it. That
/// protects it from a stray backup or a shared volume, not from someone who already has the
/// config directory: treat both as secrets, and keep the directory to mode 700.
/// </summary>
public static class CredentialStore
{
    /// <summary>Where credentials, data-protection keys and anything else durable live.</summary>
    public static readonly string Directory =
        Environment.GetEnvironmentVariable("TABLOWEB_CONFIG_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "config");

    public static string KeyDirectory => Ensure(Path.Combine(Directory, "keys"));
    private static string File => Path.Combine(Ensure(Directory), "credentials.dat");

    public static bool HasSavedFile => System.IO.File.Exists(File);

    /// <summary>Set TABLOWEB_SAVE_CREDENTIALS=0 to keep credentials in memory only.</summary>
    public static bool SavingAllowed =>
        Environment.GetEnvironmentVariable("TABLOWEB_SAVE_CREDENTIALS") is not ("0" or "false");

    private static IDataProtector? _protector;

    /// <summary>Called once at startup; the protector needs the built service provider.</summary>
    public static void Use(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("TabloWeb.Credentials.v1");

    public static void Save(Credentials credentials, ILogger log)
    {
        try
        {
            var json = JsonSerializer.Serialize(credentials);
            System.IO.File.WriteAllText(File, _protector!.Protect(json));
            Restrict(File);
            log.LogInformation("Tablo credentials saved to {File}", File);
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not save credentials to {File}: {Message}", File, ex.Message);
        }
    }

    public static Credentials? Load(ILogger log)
    {
        if (!HasSavedFile) return null;
        try
        {
            return JsonSerializer.Deserialize<Credentials>(
                _protector!.Unprotect(System.IO.File.ReadAllText(File)));
        }
        catch (Exception ex)
        {
            // Almost always the data-protection keys went missing — a container rebuilt without
            // its config volume. The file is useless now, so say so plainly and move on.
            log.LogWarning("Saved credentials could not be read ({Message}); sign in again", ex.Message);
            return null;
        }
    }

    public static void Clear()
    {
        try { if (HasSavedFile) System.IO.File.Delete(File); } catch { /* nothing to do */ }
    }

    private static string Ensure(string path)
    {
        var info = System.IO.Directory.CreateDirectory(path);
        Restrict(info.FullName);
        return info.FullName;
    }

    /// <summary>Owner-only, where the platform has such a notion.</summary>
    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            System.IO.File.SetUnixFileMode(path,
                System.IO.Directory.Exists(path)
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best effort; a mounted volume may not allow it */ }
    }
}
