using DataTrace.Application.Alarms;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

partial class RuntimeStore
{
    public IReadOnlyList<string> ListMonthKeys() => _factory.ListMonthKeys();

    public Task DeleteMonthAsync(string monthKey, CancellationToken cancellationToken = default)
    {
        _factory.DeleteMonth(monthKey);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sessions keep their records in the month database where the session began.
    /// Time-based queries must therefore search every retained database and filter by TriggerTime.
    /// </summary>
    private List<string> ExistingMonthKeys()
        => _factory.ListMonthKeys()
            .Where(RuntimeDbFactory.IsValidMonthKey)
            .Where(_factory.Exists)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
}
