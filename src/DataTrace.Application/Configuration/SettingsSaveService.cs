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
        CancellationToken cancellationToken = default)
    {
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
            var saved = await _changes.SaveSettingsAsync(target, audit, cancellationToken).ConfigureAwait(false);
            return new SettingsSaveResult
            {
                Saved = SystemSettingsSnapshot.From(target),
                AuditError = saved.AuditError
            };
        }
        catch (Exception ex)
        {
            return new SettingsSaveResult { Failure = ex.Message };
        }
    }
}
