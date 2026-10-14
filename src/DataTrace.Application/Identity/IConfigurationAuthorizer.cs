namespace DataTrace.Application.Identity;

/// <summary>Validates the current interactive identity against current account state before a sensitive configuration mutation.</summary>
public interface IConfigurationAuthorizer
{
    /// <summary>Throws <see cref="ConfigurationAuthorizationException"/> when the circuit's identity is no longer allowed to edit configuration.</summary>
    Task AuthorizeChangeAsync(string actor, CancellationToken cancellationToken = default);

    /// <summary>Checks the current circuit identity against an explicit current role set.</summary>
    Task AuthorizeRoleAsync(
        string actor,
        IReadOnlyCollection<string> allowedRoles,
        CancellationToken cancellationToken = default);

    /// <summary>Checks the current circuit identity against an explicit current role set when the caller has no actor argument.</summary>
    Task AuthorizeCurrentRoleAsync(
        IReadOnlyCollection<string> allowedRoles,
        CancellationToken cancellationToken = default);
}

/// <summary>A sensitive action was rejected because the current account or circuit is no longer authorized.</summary>
public sealed class ConfigurationAuthorizationException(string message) : Exception(message);
