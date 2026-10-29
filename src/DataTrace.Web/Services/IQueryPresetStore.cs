using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataTrace.Web.Services;

/// <summary>
/// 查询方案账号级存储：落在数据根目录，同一账号换浏览器/换机仍能读到。
/// </summary>
public interface IQueryPresetStore
{
    Task<IReadOnlyList<QueryPreset>> ListAsync(string account, CancellationToken cancellationToken = default);

    Task SaveAllAsync(string account, IReadOnlyList<QueryPreset> presets, CancellationToken cancellationToken = default);
}
