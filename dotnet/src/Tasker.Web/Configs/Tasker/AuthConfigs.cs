using Tasker.Configs;

namespace Tasker.Web.Configs.Tasker;

/// <summary>Вход на сервере.</summary>
public class AuthConfigs : AConfigs
{
    /// <summary>
    /// Секрет подписи JWT (HMAC-SHA256), не короче 32 байт. <c>TASKER_AUTH_CONFIGS_SIGNING_KEY</c> или <c>--jwtkey=...</c>.
    /// Не задан — генерируется при старте: токены перестают действовать
    /// после перезапуска сервера. В продакшене задавать обязательно.
    /// </summary>
    [ConfigAlias("jwtkey")]
    public string? SigningKey { get; set; }

    /// <summary>Срок жизни access-токена (JWT). <c>TASKER_AUTH_CONFIGS_ACCESS_TOKEN_LIFETIME</c>, формат <c>00:15:00</c>.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Срок жизни refresh-токена. Каждый обмен выдаёт новый refresh-токен с новым сроком, поэтому
    /// активный пользователь не разлогинивается. <c>TASKER_AUTH_CONFIGS_REFRESH_TOKEN_LIFETIME</c>, формат <c>30.00:00:00</c>.
    /// </summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
}
