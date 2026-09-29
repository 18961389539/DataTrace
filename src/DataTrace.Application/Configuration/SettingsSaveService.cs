using DataTrace.Domain.Validation;

namespace DataTrace.Application.Configuration;

public sealed class SettingsSaveService : ISettingsSave
{
    private readonly IConfigRepository _repository;
    private readonly IConfigurationChanges _changes;

    public SettingsSaveService(IConfigRepository repository, IConfigurationChanges changes)
    {
        _repository = repository;
        _changes = changes;
    }

    public async Task<SettingsSaveResult> SaveAsync(
        SettingsEdit edit,
        SettingsEdit loaded,
        string userName,
        bool canChangeRetention,
        CancellationToken cancellationToken = default)
    {
        // 保留年数一改小，后台清理任务就会按更近的月份删掉整月记录库与归档——这是删除授权，
        // 不是普通配置项。判定基准与 SettingsEditRules.Apply 一致（本页手上这份 vs 载入时那份），
        // 否则"改了但页面看不出改"会绕过这道门。
        if (!canChangeRetention
            && (edit.RetentionYears != loaded.RetentionYears
                || edit.AuditRetentionYears != loaded.AuditRetentionYears))
        {
            return new SettingsSaveResult { ValidationMessage = "只有管理员可以修改历史数据保留策略（改小会删除历史记录）" };
        }

        if (SettingsEditRules.UrlError(edit) is { } urlError)
        {
            return new SettingsSaveResult { ValidationMessage = urlError };
        }

        if (SettingsEditRules.RequiredError(edit) is { } required)
        {
            return new SettingsSaveResult { ValidationMessage = required };
        }

        var latest = (await _repository.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Settings.Clone();
        var target = SettingsEditRules.Apply(latest, edit, loaded);
        if (SettingsLimits.Error(target) is { } limitError)
        {
            return new SettingsSaveResult { ValidationMessage = limitError };
        }

        try
        {
            var audit = new ConfigAudit(
                userName,
                "Save",
                "SystemSettings",
                "settings",
                SettingsEditRules.Summarize(latest),
                SettingsEditRules.Summarize(target));
            await _changes.SaveSettingsAsync(target, audit, cancellationToken).ConfigureAwait(false);
            return new SettingsSaveResult { Saved = SystemSettingsSnapshot.From(target) };
        }
        catch (Exception ex)
        {
            return new SettingsSaveResult { Failure = ex.Message };
        }
    }
}
