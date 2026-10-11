using System.Text.Json;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataTrace.Infrastructure.Persistence;

/// <summary>
/// 在制索引意图对账器。月库提交后、config.db 索引更新前硬退出的窗口，
/// 由同事务写入的 <see cref="SessionIndexIntent"/> 兜底：这里按产生顺序重放，
/// 每条都先和当前实态核对（会话关闭、被新流水接管、索引已更新），过期意图作废而不是盲目套用。
/// </summary>
/// <remarks>
/// 三种结局必须区分：<b>应用</b>与<b>作废</b>都写 AppliedAt（终态，不再重试）；
/// <b>暂缓</b>（载荷解析不了等自身异常）不写 AppliedAt —— 它不是过期，是"还没能力判断"，
/// 留给下一轮继续，同时记警告日志供诊断。把暂缓误标成作废会永久丢掉补偿机会。
/// </remarks>
public sealed class SessionIndexRelay : ISessionIndexRelay
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RuntimeDbFactory _runtimeFactory;
    private readonly IActiveSessionStore _activeSessions;
    private readonly ILogger<SessionIndexRelay> _logger;
    private readonly IDataMaintenanceBarrier? _maintenanceBarrier;

    public SessionIndexRelay(
        RuntimeDbFactory runtimeFactory,
        IActiveSessionStore activeSessions,
        ILogger<SessionIndexRelay> logger,
        IDataMaintenanceBarrier? maintenanceBarrier = null)
    {
        _runtimeFactory = runtimeFactory;
        _activeSessions = activeSessions;
        _logger = logger;
        _maintenanceBarrier = maintenanceBarrier;
    }

    private enum IntentOutcome
    {
        /// <summary>核对通过，索引已按意图更新。</summary>
        Applied,

        /// <summary>意图已过期（会话关闭/被新流水接管/对象已不存在），作废。</summary>
        Voided,

        /// <summary>意图自身无法解析或依赖缺失，暂缓：不写终态，下一轮继续，并留诊断。</summary>
        Deferred
    }

    public async Task<int> RelayPendingAsync(int takePerMonth, CancellationToken cancellationToken = default)
    {
        await using var maintenanceLease = _maintenanceBarrier is null
            ? null
            : await _maintenanceBarrier.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        var applied = 0;
        foreach (var monthKey in _runtimeFactory.ListMonthKeys().Where(RuntimeDbFactory.IsValidMonthKey))
        {
            await using var db = _runtimeFactory.Open(monthKey);
            var pending = await db.SessionIndexIntents
                .Where(x => x.AppliedAt == null)
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Take(takePerMonth)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (pending.Count == 0)
            {
                continue;
            }

            foreach (var intent in pending)
            {
                var outcome = await ApplyAsync(intent, cancellationToken).ConfigureAwait(false);
                if (outcome == IntentOutcome.Applied)
                {
                    applied++;
                }

                // 应用与作废都是终态；只有暂缓保持未决，等下一轮重试。
                if (outcome != IntentOutcome.Deferred)
                {
                    intent.AppliedAt = DateTime.Now;
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return applied;
    }

    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var maintenanceLease = _maintenanceBarrier is null
            ? null
            : await _maintenanceBarrier.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var monthKey in _runtimeFactory.ListMonthKeys().Where(RuntimeDbFactory.IsValidMonthKey))
        {
            await using var db = _runtimeFactory.Open(monthKey);
            count += await db.SessionIndexIntents.CountAsync(x => x.AppliedAt == null, cancellationToken).ConfigureAwait(false);
        }
        return count;
    }

    private async Task<IntentOutcome> ApplyAsync(SessionIndexIntent intent, CancellationToken cancellationToken)
    {
        var current = await _activeSessions.FindByPalletAsync(intent.PalletCode, cancellationToken)
            .ConfigureAwait(false);

        if (intent.Operation == 1)
        {
            // 移除：索引已被更新的流水接管（新事件会自己维护索引），或索引已不存在 → 作废。
            if (current is null || current.SerialNo != intent.SerialNo)
            {
                return IntentOutcome.Voided;
            }

            await _activeSessions.RemoveByPalletAsync(intent.PalletCode, cancellationToken).ConfigureAwait(false);
            return IntentOutcome.Applied;
        }

        // 更新：索引已被更新的流水接管 → 作废，绝不能用旧事件覆盖新事件。
        if (current is not null && current.SerialNo != intent.SerialNo)
        {
            return IntentOutcome.Voided;
        }

        // 月库已被保留任务整月删除 → 会话与记录一起没了，索引本就不该再指过去。
        if (!_runtimeFactory.Exists(intent.MonthKey))
        {
            return IntentOutcome.Voided;
        }

        // 载荷解析必须捕获 JsonException：坏载荷不是"过期"（作废会永久丢掉这次补偿），
        // 也不是让整轮对账崩掉的理由 —— 暂缓并留诊断，由人核查后人工处理。
        ActiveSessionIndex? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ActiveSessionIndex>(intent.PayloadJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "在制索引意图 {IntentId}（流水 {Serial}）载荷 JSON 损坏，已暂缓不重试作废；请人工核查 PayloadJson",
                intent.Id, intent.SerialNo);
            return IntentOutcome.Deferred;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.PalletCode))
        {
            // 载荷自身损坏不是"过期"：作废会永久丢掉这次补偿，暂缓并留诊断，由人来查。
            _logger.LogWarning(
                "在制索引意图 {IntentId}（流水 {Serial}）载荷无法解析，已暂缓不重试作废；请人工核查 PayloadJson",
                intent.Id, intent.SerialNo);
            return IntentOutcome.Deferred;
        }

        await using var db = _runtimeFactory.Open(intent.MonthKey);
        var session = await FindSessionAsync(db, intent, payload, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            // 按 Id 与流水号都定位不到会话：这个托盘的会话在月库里确实不存在了，
            // 索引本就不该再指过去 → 作废（与"会话已关闭"同性质）。
            return IntentOutcome.Voided;
        }

        if (session.Status == SessionStatus.Closed || session.EndTime is not null)
        {
            // 会话已关闭（末站意图已先行应用）→ 索引本就该移除，Upsert 作废。
            return IntentOutcome.Voided;
        }

        if (current?.LastActivityAt > payload.LastActivityAt)
        {
            return IntentOutcome.Voided;
        }

        payload.SessionId = session.Id;
        await _activeSessions.UpsertAsync(payload, cancellationToken).ConfigureAwait(false);
        return IntentOutcome.Applied;
    }

    /// <summary>
    /// 定位会话：优先按载荷里的 SessionId；历史意图（载荷生成于 SessionId 填充修复前）可能是 0
    /// 或指向已被重建的同 Id 会话 —— 流水号在月库有唯一索引，按它反查才是可靠的会话身份。
    /// </summary>
    private static async Task<PalletSession?> FindSessionAsync(
        RuntimeDbContext db,
        SessionIndexIntent intent,
        ActiveSessionIndex payload,
        CancellationToken cancellationToken)
    {
        if (payload.SessionId != 0)
        {
            var byId = await db.PalletSessions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == payload.SessionId, cancellationToken)
                .ConfigureAwait(false);
            if (byId is not null && byId.SerialNo == intent.SerialNo)
            {
                return byId;
            }
        }

        // Id 查不到、或查到但流水号对不上（自增 Id 被新会话复用）→ 按流水号反查。
        return await db.PalletSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SerialNo == intent.SerialNo, cancellationToken)
            .ConfigureAwait(false);
    }
}
