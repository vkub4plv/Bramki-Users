using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using System.Security.Claims;

public sealed class DevGroupSidClaims : IClaimsTransformation
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _cfg;
    private const string GroupSid = "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid";

    public DevGroupSidClaims(IWebHostEnvironment env, IConfiguration cfg)
    { _env = env; _cfg = cfg; }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (!_env.IsDevelopment()) return Task.FromResult(principal);

        var groups = _cfg.GetSection("Auth:Groups");
        var sids = new[] { groups["All"], groups["Admin"], groups["HR"], groups["Karty"], groups["Ochrona"] }
                   .Where(s => !string.IsNullOrWhiteSpace(s))!;

        // Add a separate identity that carries only dev group SIDs
        var devId = new ClaimsIdentity(authenticationType: "DevGroups");
        foreach (var sid in sids) devId.AddClaim(new Claim(GroupSid, sid!));

        // Avoid duplicating if it's already there
        var hasAny = principal.Claims.Any(c => c.Type == GroupSid && sids.Contains(c.Value));
        if (!hasAny) principal.AddIdentity(devId);

        return Task.FromResult(principal);
    }
}