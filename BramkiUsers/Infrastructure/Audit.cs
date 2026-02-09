using System;
using Microsoft.Extensions.Logging;

namespace BramkiUsers.Infrastructure
{
    /// <summary>
    /// Creates per-action audit scopes with lightweight context (User, Action, ErpId).
    /// </summary>
    public interface IAudit<TCategoryName>
    {
        AuditScope Begin(string action, string? erpId = null);
    }

    /// <summary>
    /// Represents one logical operation (e.g. Hire/Fire).
    /// Prepends [User]/[Action]/[ErpId] to every log message.
    /// </summary>
    public sealed class AuditScope : IDisposable
    {
        private readonly ILogger _logger;

        public string Action { get; }
        public string User { get; }
        public string? ErpId { get; }

        internal AuditScope(ILogger logger, string action, string user, string? erpId)
        {
            _logger = logger;
            Action = action;
            User = user;
            ErpId = erpId;
        }

        private string Prefix =>
            ErpId is { Length: > 0 }
                ? $"User={User} Action={Action} ErpId={ErpId} | "
                : $"User={User} Action={Action} | ";

        /// <summary>
        /// Log initial payload ONCE at the beginning of the operation.
        /// </summary>
        public void LogStart(object? payload = null)
        {
            if (payload is null)
            {
                _logger.LogInformation(Prefix + "Audit start.");
            }
            else
            {
                _logger.LogInformation(Prefix + "Audit start with input {@Payload}.", payload);
            }
        }

        public void Info(string messageTemplate, params object?[] args)
            => _logger.LogInformation(Prefix + messageTemplate, args);

        public void Warn(string messageTemplate, params object?[] args)
            => _logger.LogWarning(Prefix + messageTemplate, args);

        public void Error(Exception ex, string messageTemplate, params object?[] args)
            => _logger.LogError(ex, Prefix + messageTemplate, args);

        public void Success(string messageTemplate = "Operation completed successfully.", params object?[] args)
            => _logger.LogInformation(Prefix + messageTemplate, args);

        public void Dispose()
        {
            // no-op;
        }
    }

    public sealed class Audit<TCategoryName> : IAudit<TCategoryName>
    {
        private readonly ILogger<TCategoryName> _logger;
        private readonly ICurrentUserService _currentUser;

        public Audit(ILogger<TCategoryName> logger, ICurrentUserService currentUser)
        {
            _logger = logger;
            _currentUser = currentUser;
        }

        public AuditScope Begin(string action, string? erpId = null)
        {
            var user = _currentUser.Name ?? "<unknown>";
            return new AuditScope(_logger, action, user, erpId);
        }
    }
}