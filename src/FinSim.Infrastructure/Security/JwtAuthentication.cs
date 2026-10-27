using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FinSim.Infrastructure.Security;

/// <summary>
/// Configures a deliberately narrow RS256 JWT profile. The same code accepts
/// either an HTTPS OIDC authority or a locally mounted public JWKS file; no
/// cloud-provider SDK or signing key is required by the service.
/// </summary>
public static class JwtAuthentication
{
    public const string ApiPolicy = "finsim-api";

    public static IServiceCollection AddFinSimJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        string requiredScope)
    {
        var issuer = Required(configuration, "Issuer");
        var audience = Required(configuration, "Audience");
        var authority = configuration["Authority"];
        var jwksFile = configuration["JwksFile"];

        if (string.IsNullOrWhiteSpace(authority) == string.IsNullOrWhiteSpace(jwksFile))
        {
            throw new InvalidOperationException(
                "Configure exactly one JWT Authority or JwksFile.");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.RequireHttpsMetadata = true;
                options.Audience = audience;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
                };

                if (!string.IsNullOrWhiteSpace(authority))
                {
                    if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) ||
                        uri.Scheme != Uri.UriSchemeHttps)
                    {
                        throw new InvalidOperationException(
                            "JWT Authority must be an absolute HTTPS URL.");
                    }

                    options.Authority = authority;
                }
                else
                {
                    var path = Path.GetFullPath(jwksFile!);
                    options.TokenValidationParameters.IssuerSigningKeyResolver =
                        (_, _, keyId, _) => LoadSigningKeys(path, keyId);
                }
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(ApiPolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => HasScope(context.User, requiredScope));
            });
        });
        return services;
    }

    private static IEnumerable<SecurityKey> LoadSigningKeys(string path, string? keyId)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length is <= 0 or > 1_048_576)
        {
            throw new SecurityTokenInvalidSigningKeyException(
                "JWT JWKS file is missing or invalid.");
        }

        if (OperatingSystem.IsLinux() &&
            (File.GetUnixFileMode(path) &
             (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new SecurityTokenInvalidSigningKeyException(
                "JWT JWKS file must not be group/world writable.");
        }

        var set = new JsonWebKeySet(File.ReadAllText(path));
        return set.GetSigningKeys().Where(key =>
            string.IsNullOrEmpty(keyId) || string.Equals(key.KeyId, keyId, StringComparison.Ordinal));
    }

    private static bool HasScope(System.Security.Claims.ClaimsPrincipal principal, string scope)
    {
        return principal.FindAll("scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(value => string.Equals(value, scope, StringComparison.Ordinal));
    }

    private static string Required(IConfiguration configuration, string name)
    {
        var value = configuration[name];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"JWT {name} is required.")
            : value;
    }
}
