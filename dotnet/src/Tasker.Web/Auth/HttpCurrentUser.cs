using Microsoft.AspNetCore.Http;
using Tasker.Core.Users;

namespace Tasker.Web.Auth;

/// <summary>
/// Пользователь текущего запроса — claim <c>sub</c>:
/// сервер — из JWT человека (REST) или токена агента (MCP); локальный режим — агент в MCP,
/// локальный человек в REST демона (см. <see cref="LocalHumanRequests"/>).
/// </summary>
internal class HttpCurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    public Guid? Id =>
        Guid.TryParse(http.HttpContext?.User.FindFirst(TokenService.UserIdClaim)?.Value, out var id) ? id : null;
}
