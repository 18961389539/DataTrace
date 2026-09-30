using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Application.Evaluation;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Domain.Validation;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

partial class ConfigRepository
{
    public Task<IReadOnlyList<PlcConnection>> GetPlcConnectionsAsync(CancellationToken cancellationToken = default)
        => ReadListAsync(
            db => db.PlcConnections.AsNoTracking().Include(x => x.Heartbeat).ToListAsync(cancellationToken),
            cancellationToken);

    /// <summary>
    /// 单个 PLC 的详情读：与 <see cref="GetStationAsync"/> 一样保留在 scoped 上下文里，本次不动。
    /// </summary>
    public Task<PlcConnection?> GetPlcConnectionAsync(int id, CancellationToken cancellationToken = default)
        => _db.PlcConnections.Include(x => x.Heartbeat).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<int> SavePlcConnectionAsync(SavePlcConnectionCommand command, CancellationToken cancellationToken = default)
    {
        var connection = command.ToEntity();
        connection.Name = connection.Name.Trim();

        // 库里 Name 上有唯一索引，重名会抛出 SQLite 的原始错误；先在这里查出来，
        // 用户看到的才是"哪台重名"而不是一串 SQL 报错（与型号保存同一套做法）。
        if (await _db.PlcConnections.AnyAsync(
                x => x.Id != connection.Id && x.Name == connection.Name,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"PLC 名称「{connection.Name}」已存在");
        }

        if (connection.Id == 0)
        {
            _db.PlcConnections.Add(connection);
        }
        else
        {
            var existing = await _db.PlcConnections
                .Include(x => x.Heartbeat)
                .FirstOrDefaultAsync(x => x.Id == connection.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                _db.PlcConnections.Add(connection);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(connection);
                ApplyHeartbeat(existing, connection.Heartbeat);
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
        return connection.Id;
    }

    private void ApplyHeartbeat(PlcConnection existing, HeartbeatSettings? incoming)
    {
        if (incoming is null)
        {
            if (existing.Heartbeat is not null)
            {
                _db.Heartbeats.Remove(existing.Heartbeat);
                existing.Heartbeat = null;
            }

            return;
        }

        if (existing.Heartbeat is null)
        {
            incoming.Id = 0;
            incoming.PlcConnectionId = existing.Id;
            existing.Heartbeat = incoming;
            return;
        }

        incoming.Id = existing.Heartbeat.Id;
        incoming.PlcConnectionId = existing.Id;
        _db.Entry(existing.Heartbeat).CurrentValues.SetValues(incoming);
    }

    public async Task DeletePlcConnectionAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _db.PlcConnections.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        _db.PlcConnections.Remove(item);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveHeartbeatAsync(SaveHeartbeatCommand command, CancellationToken cancellationToken = default)
    {
        var heartbeat = command.ToEntity();
        if (heartbeat.Id == 0)
        {
            _db.Heartbeats.Add(heartbeat);
        }
        else
        {
            var existing = await _db.Heartbeats
                .FirstOrDefaultAsync(x => x.Id == heartbeat.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                _db.Heartbeats.Add(heartbeat);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(heartbeat);
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }
}
