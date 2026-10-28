using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Tasker.Core.Auth;
using Tasker.Core.Users;
using Tasker.Web.Configs.Tasker;

namespace Tasker.Web.Auth;

/// <summary>
/// Выдача и проверка access-токенов (JWT). В токене — id пользователя и его SecurityStamp: после смены
/// пароля или удаления пользователя старые токены перестают приниматься, не дожидаясь истечения срока.
/// Refresh-токены — в <see cref="AuthService"/>.
/// </summary>
public class TokenService(AuthConfigs configs, TimeProvider time) : IAccessTokenIssuer
{
    public const string UserIdClaim = JwtRegisteredClaimNames.Sub;
    private const string StampClaim = "stamp";
    private const string Issuer = "tasker";
    private const int MinKeyBytes = 32;

    private readonly SymmetricSecurityKey _key = new(SigningKey(configs));

    public (string Token, DateTimeOffset ExpiresAt) Issue(User user, UserCredentials credentials)
    {
        var now = time.GetUtcNow();
        var expiresAt = now + configs.AccessTokenLifetime;

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                // Уникальный id: два входа в одну секунду не дают одинаковых токенов.
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                [UserIdClaim] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Name] = user.Username,
                [StampClaim] = credentials.SecurityStamp.ToString()
            },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256)
        });

        return (token, expiresAt);
    }

    public void Configure(JwtBearerOptions bearer)
    {
        // Имена claim-ов как в токене (sub, name), без переименования в длинные URI.
        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Issuer,
            IssuerSigningKey = _key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Name
        };
        bearer.Events = new JwtBearerEvents { OnTokenValidated = EnsureNotRevoked };
    }

    // Подпись и срок проверены; остаётся убедиться, что пользователь существует и не менял пароль.
    private static async Task EnsureNotRevoked(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (!Guid.TryParse(principal?.FindFirst(UserIdClaim)?.Value, out var userId))
        {
            context.Fail("Token has no user id");
            return;
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<IUserStorage>();
        var credentials = await users.GetCredentials(userId, context.HttpContext.RequestAborted);
        if (credentials == null || credentials.SecurityStamp.ToString() != principal!.FindFirst(StampClaim)?.Value)
            context.Fail("Token is no longer valid");
    }

    // Ключ подписи: не задан — генерируется на время работы процесса.
    // Удобно для разработки; в продакшене ключ задаётся, иначе все токены умирают при перезапуске.
    private static byte[] SigningKey(AuthConfigs configs)
    {
        if (string.IsNullOrWhiteSpace(configs.SigningKey))
        {
            Log.Warning(
                "JWT signing key is not set (TASKER_AUTH_CONFIGS_SIGNING_KEY / --jwtkey): using a temporary key, " +
                "all tokens become invalid after restart");
            return RandomNumberGenerator.GetBytes(64);
        }

        var key = Encoding.UTF8.GetBytes(configs.SigningKey);
        if (key.Length < MinKeyBytes)
            throw new InvalidOperationException($"JWT signing key must be at least {MinKeyBytes} bytes");
        return key;
    }
}
