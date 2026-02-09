using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BramkiUsers.SessionManagement;

namespace BramkiUsers.Wcf
{
    public interface IVisoSessionProvider
    {
        ValueTask<Guid> GetTokenAsync(CancellationToken ct = default);
        ValueTask<Guid> RefreshTokenAsync(CancellationToken ct = default); // force reconnect
    }

    public sealed class VisoSessionManager : BackgroundService, IVisoSessionProvider
    {
        private readonly VisoClientFactory _clients;
        private readonly VisoServiceAccountOptions _acct;
        private readonly VisoOptions _opt;
        private readonly ILogger<VisoSessionManager> _log;

        private readonly SemaphoreSlim _lock = new(1, 1);
        private Guid _token;

        public VisoSessionManager(
            VisoClientFactory clients,
            IOptions<VisoServiceAccountOptions> acct,
            IOptions<VisoOptions> opt,
            ILogger<VisoSessionManager> log)
        {
            _clients = clients; _acct = acct.Value; _opt = opt.Value; _log = log;
        }

        public async ValueTask<Guid> GetTokenAsync(CancellationToken ct = default)
        {
            if (_token != Guid.Empty) return _token;
            await EnsureConnectedAsync(ct);
            return _token;
        }

        public async ValueTask<Guid> RefreshTokenAsync(CancellationToken ct = default)
        {
            await ReconnectAsync(ct);
            return _token;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 1) Initial connect with exponential backoff (bounded) + jitter
            var backoff = TimeSpan.FromSeconds(5);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EnsureConnectedAsync(stoppingToken);
                    break; // connected
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "Initial VISO connect failed; retrying in {Delay}.", backoff);
                    await Task.Delay(ApplyJitter(backoff), stoppingToken);
                    var nextSeconds = Math.Min(backoff.TotalSeconds * 2, 60); // cap at 60s
                    backoff = TimeSpan.FromSeconds(nextSeconds);
                }
            }

            // 2) Steady-state keep-alive
            var keepAlive = TimeSpan.FromMinutes(Math.Clamp(_opt.KeepAliveMinutes, 1, 30));
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(keepAlive, stoppingToken);

                    if (_token == Guid.Empty)
                    {
                        await EnsureConnectedAsync(stoppingToken);
                        continue;
                    }

                    using var sm = _clients.CreateSession();
                    var op = await sm.GetOperatorBySessionAsync(_token);
                    if (op == null || string.IsNullOrEmpty(op.Login))
                        await ReconnectAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "Keep-alive failed; reconnecting.");
                    await ReconnectAsync(stoppingToken);
                }
            }
        }

        private static TimeSpan ApplyJitter(TimeSpan delay)
        {
            // 85%–115% jitter to avoid thundering herd if many instances restart together
            var factor = 0.85 + (Random.Shared.NextDouble() * 0.30);
            return TimeSpan.FromMilliseconds(delay.TotalMilliseconds * factor);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await DisconnectSafeAsync();
            await base.StopAsync(cancellationToken);
        }

        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (_token != Guid.Empty) return;

                using var sm = _clients.CreateSession();
                _token = await sm.ConnectAsync(_acct.Login, _acct.Password);
                _log.LogInformation("VISO connected; token {Token}", _token);
            }
            finally { _lock.Release(); }
        }

        private async Task ReconnectAsync(CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                await DisconnectSafeAsync();
                using var sm = _clients.CreateSession();
                _token = await sm.ConnectAsync(_acct.Login, _acct.Password);
                _log.LogInformation("VISO reconnected; token {Token}", _token);
            }
            finally { _lock.Release(); }
        }

        private async Task DisconnectSafeAsync()
        {
            try
            {
                if (_token == Guid.Empty) return;
                using var sm = _clients.CreateSession();
                await sm.DisconnectAsync(_token);
                _log.LogInformation("VISO disconnected");
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Disconnect ignored.");
            }
            finally { _token = Guid.Empty; }
        }
    }
}