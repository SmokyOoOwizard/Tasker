using Microsoft.AspNetCore.Http;
using Tasker.Core.Users;

namespace Tasker.Web.Auth;

/// <summary>
/// Пользователь текущего запроса — claim <c>sub</c>:
/// сервер — из JWT человека (REST) или токена агента (MCP); десктоп — локальный агент в MCP,
/// а REST-запросы десктопа ни от чьего имени (входа нет).
/// </summary>
internal class HttpCurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    public Guid? Id =>
        Guid.TryParse(http.HttpContext?.User.FindFirst(TokenService.UserIdClaim)?.Value, out var id) ? id : null;
}
