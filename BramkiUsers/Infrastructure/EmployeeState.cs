using BramkiUsers.Data;
using Microsoft.EntityFrameworkCore;

namespace BramkiUsers.Infrastructure;

public sealed class EmployeeState
{
    private readonly IDbContextFactory<RaportowanieContext> _dbFactory;

    public EmployeeState(IDbContextFactory<RaportowanieContext> dbFactory)
        => _dbFactory = dbFactory;

    public int? CurrentId { get; private set; }
    public DUser? User { get; private set; }
    public DGate? Gate { get; private set; }

    public event Action? Changed;

    public async Task LoadAsync(int id, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var u = await db.DUsers
            .AsNoTracking()
            .Include(x => x.Gates)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        CurrentId = u?.Id;
        User = u;
        Gate = u?.Gates?.FirstOrDefault();

        Changed?.Invoke();
    }

    public Task ReloadAsync(CancellationToken ct = default)
    => CurrentId is int id ? LoadAsync(id, ct) : Task.CompletedTask;

    public void Invalidate()
    {
        CurrentId = null; User = null; Gate = null;
        Changed?.Invoke();
    }
}
