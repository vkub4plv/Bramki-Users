using System.Security.Claims;

namespace BramkiUsers.Infrastructure
{
    public interface ICurrentUserService
    {
        ClaimsPrincipal? Principal { get; }
        string? Name { get; }
        void SetUser(ClaimsPrincipal principal);
    }

    public sealed class CurrentUserService : ICurrentUserService
    {
        private ClaimsPrincipal? _principal;

        public ClaimsPrincipal? Principal => _principal;
        public string? Name => _principal?.Identity?.Name;

        public void SetUser(ClaimsPrincipal principal)
        {
            _principal = principal;
        }
    }
}