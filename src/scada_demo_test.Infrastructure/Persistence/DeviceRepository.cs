using Microsoft.EntityFrameworkCore;
using scada_demo_test.Domain.Entities;
using scada_demo_test.Domain.Interfaces;

namespace scada_demo_test.Infrastructure.Persistence;

public class DeviceRepository : IDeviceRepository
{
    private readonly MyDbContextDxy _db;
    public DeviceRepository(MyDbContextDxy db) => _db = db;

    public Task<Device?> GetByExternalIdAsync(string externalId, CancellationToken ct = default) =>
        _db.Devices.FirstOrDefaultAsync(d => d.ExternalId == externalId, ct);

    public Task<Device?> GetByIpAddressAsync(string ipAddress, CancellationToken ct = default) =>
        _db.Devices.FirstOrDefaultAsync(d => d.IpAddress != null && d.IpAddress.Trim() == ipAddress, ct);

    public Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Devices.AsNoTracking().ToListAsync(ct);

    public async Task AddAsync(Device device, CancellationToken ct = default)
    {
        _db.Devices.Add(device);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Device device, CancellationToken ct = default)
    {
        _db.Devices.Update(device);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Device device, CancellationToken ct = default)
    {
        _db.Devices.Remove(device);
        await _db.SaveChangesAsync(ct);
    }
}
