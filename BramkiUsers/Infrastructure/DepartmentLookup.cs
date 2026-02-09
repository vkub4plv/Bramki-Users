using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;

namespace BramkiUsers.Data;

public sealed record DeptInfo(string Name, string Cc, string Code);

public interface IDepartmentLookup
{
    Task EnsureLoadedAsync(CancellationToken ct = default);
    Task ReloadAsync(CancellationToken ct = default);

    bool TryGetName(int? departmentId, out string name);
    IReadOnlyDictionary<int, string> All { get; }

    bool TryGetInfo(int? departmentId, out DeptInfo info);
    IReadOnlyDictionary<int, DeptInfo> AllInfos { get; }
}

public sealed class DepartmentLookup : IDepartmentLookup
{
    private readonly IDbContextFactory<RaportowanieContext> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile IReadOnlyDictionary<int, DeptInfo> _infos =
        ImmutableDictionary<int, DeptInfo>.Empty;

    private volatile IReadOnlyDictionary<int, string> _names =
        ImmutableDictionary<int, string>.Empty;

    public DepartmentLookup(IDbContextFactory<RaportowanieContext> factory)
        => _factory = factory;

    public IReadOnlyDictionary<int, DeptInfo> AllInfos => _infos;
    public IReadOnlyDictionary<int, string> All => _names;

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_infos.Count > 0) return;
        await ReloadAsync(ct);
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);

            var rows = await db.DDepartment.AsNoTracking()
                .Where(d =>
                    d.BramkiName != null && d.BramkiName != "" &&
                    d.BramkiCc != null && d.BramkiCc != "" &&
                    d.BramkiCode != null && d.BramkiCode != "")
                .Select(d => new
                {
                    d.DepartmentId,
                    d.BramkiName,
                    d.BramkiCc,
                    d.BramkiCode
                })
                .ToListAsync(ct);

            var infos = rows
                .Select(d =>
                    new KeyValuePair<int, DeptInfo>(
                        d.DepartmentId,
                        new DeptInfo(
                            (d.BramkiName ?? "").Trim(),
                            (d.BramkiCc ?? "").Trim(),
                            (d.BramkiCode ?? "").Trim())))
                .ToImmutableDictionary(k => k.Key, v => v.Value);

            var names = infos
                .Select(kv => new KeyValuePair<int, string>(kv.Key, kv.Value.Name))
                .ToImmutableDictionary(k => k.Key, v => v.Value);

            _infos = infos;
            _names = names;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryGetName(int? departmentId, out string name)
    {
        name = string.Empty;
        return departmentId is int id && _names.TryGetValue(id, out name);
    }

    public bool TryGetInfo(int? departmentId, out DeptInfo info)
    {
        info = default!;
        return departmentId is int id && _infos.TryGetValue(id, out info);
    }
}