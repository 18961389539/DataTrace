using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Validation;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

/// <summary>
/// 工单档案。刻意不参与 <see cref="GetSnapshotAsync"/> 与配置修订：它不影响采集判定，
/// 修改工单不应该让采集器重载，也不应该出现在"回滚整份配置"的范围里。
/// </summary>
partial class ConfigRepository
{
    public Task<IReadOnlyList<WorkOrder>> GetWorkOrdersAsync(CancellationToken cancellationToken = default)
        => ReadListAsync(
            db => db.WorkOrders.AsNoTracking()
                .Include(x => x.Recipe)
                .OrderBy(x => x.Status)
                .ThenByDescending(x => x.CreatedAt)
                .ThenBy(x => x.Code)
                .ToListAsync(cancellationToken),
            cancellationToken);

    public Task<WorkOrder?> GetWorkOrderAsync(int id, CancellationToken cancellationToken = default)
        => ReadAsync(
            db => db.WorkOrders.AsNoTracking()
                .Include(x => x.Recipe)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken),
            cancellationToken);

    public async Task SaveWorkOrderAsync(WorkOrder workOrder, CancellationToken cancellationToken = default)
    {
        workOrder.Code = (workOrder.Code ?? "").Trim();
        workOrder.Model = string.IsNullOrWhiteSpace(workOrder.Model) ? null : workOrder.Model.Trim();
        workOrder.Remark = string.IsNullOrWhiteSpace(workOrder.Remark) ? null : workOrder.Remark.Trim();
        workOrder.StartDate = workOrder.StartDate?.Date;
        workOrder.EndDate = workOrder.EndDate?.Date;

        if (WorkOrderRules.CodeError(workOrder.Code) is { } codeError)
        {
            throw new InvalidOperationException(codeError);
        }

        if (WorkOrderRules.PlannedQuantityError(workOrder.PlannedQuantity) is { } quantityError)
        {
            throw new InvalidOperationException(quantityError);
        }

        if (WorkOrderRules.DateRangeError(workOrder.StartDate, workOrder.EndDate) is { } dateError)
        {
            throw new InvalidOperationException(dateError);
        }

        if (WorkOrderRules.StatusError(workOrder.Status) is { } statusError)
        {
            throw new InvalidOperationException(statusError);
        }

        if (WorkOrderRules.RemarkError(workOrder.Remark) is { } remarkError)
        {
            throw new InvalidOperationException(remarkError);
        }

        if (ProductModelRules.Error(workOrder.Model) is { } modelError)
        {
            throw new InvalidOperationException(modelError);
        }

        if (workOrder.RecipeId is int recipeId
            && !await _db.Recipes.AsNoTracking().AnyAsync(x => x.Id == recipeId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("所选产品型号不存在，可能已被删除");
        }

        var conflict = await _db.WorkOrders.AsNoTracking()
            .AnyAsync(x => x.Id != workOrder.Id && x.Code.ToLower() == workOrder.Code.ToLower(), cancellationToken)
            .ConfigureAwait(false);
        if (conflict)
        {
            throw new InvalidOperationException($"工单号「{workOrder.Code}」已存在");
        }

        // 导航属性可能指着列表里那份未跟踪的型号；只按外键落库，避免 EF 试图把型号一起插入。
        workOrder.Recipe = null;

        if (workOrder.Id == 0)
        {
            workOrder.CreatedAt = DateTime.Now;
            workOrder.IsCurrent = false;
            _db.WorkOrders.Add(workOrder);
        }
        else
        {
            var existing = await _db.WorkOrders
                .FirstOrDefaultAsync(x => x.Id == workOrder.Id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("工单不存在，可能已被删除");
            existing.Code = workOrder.Code;
            existing.RecipeId = workOrder.RecipeId;
            existing.Model = workOrder.Model;
            existing.PlannedQuantity = workOrder.PlannedQuantity;
            existing.Status = workOrder.Status;
            existing.StartDate = workOrder.StartDate;
            existing.EndDate = workOrder.EndDate;
            existing.Remark = workOrder.Remark;

            // 当前工单必须处于生产中；改完成/取消/待生产即自动摘掉当前标记。
            if (existing.Status != WorkOrderStatus.InProgress)
            {
                existing.IsCurrent = false;
                existing.CurrentSince = null;
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 设置当前工单：传 null 表示取消。设为当前时状态一并改为生产中，
    /// 保证全系统最多一张当前工单。
    /// </summary>
    public async Task SetCurrentWorkOrderAsync(int? id, CancellationToken cancellationToken = default)
    {
        var current = await _db.WorkOrders
            .Where(x => x.IsCurrent)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var order in current)
        {
            order.IsCurrent = false;
            order.CurrentSince = null;
        }

        if (id is int workOrderId)
        {
            var target = await _db.WorkOrders
                .FirstOrDefaultAsync(x => x.Id == workOrderId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("工单不存在，可能已被删除");
            target.IsCurrent = true;
            target.CurrentSince = DateTime.Now;
            target.Status = WorkOrderStatus.InProgress;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteWorkOrderAsync(int id, CancellationToken cancellationToken = default)
    {
        var existing = await _db.WorkOrders
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        _db.WorkOrders.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
