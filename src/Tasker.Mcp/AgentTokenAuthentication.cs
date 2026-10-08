using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Tasker.Core.Agents;

namespace Tasker.Mcp;

/// <summary>
/// Вход агента на сервере: <c>Authorization: Bearer tsk_…</c>. Схема подключена только к <c>/mcp</c>:
/// в REST-API токен агента не принимается, а JWT людей не принимается в MCP.
/// </summary>
public class AgentTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentToken";

    /// <summary>Тот же claim, что у JWT людей, — поэтому текущий пользователь определяется одинаково.</summary>
    public const string UserIdClaim = "sub";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers[HeaderNames.Authorization];
        if (header == null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var agents = Context.RequestServices.GetRequiredService<AgentService>();
        var agent = await agents.Authenticate(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (agent == null)
            return AuthenticateResult.Fail("Agent token is invalid, expired or revoked");

        var identity = new ClaimsIdentity([new Claim(UserIdClaim, agent.Id.ToString()), new Claim("name", agent.Username)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
