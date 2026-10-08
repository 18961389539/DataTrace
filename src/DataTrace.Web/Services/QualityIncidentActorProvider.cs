using DataTrace.Application.Quality;
using DataTrace.Domain.Constants;
using Microsoft.AspNetCore.Components.Authorization;

namespace DataTrace.Web.Services;

public sealed class QualityIncidentActorProvider(AuthenticationStateProvider authenticationStateProvider)
    : IQualityIncidentActorProvider
{
    public async Task<QualityIncidentActor> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var user = (await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        var userName = user.Identity?.Name;
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new UnauthorizedAccessException("请先登录后再处理质量异常。");
        }

        var roles = AppRoles.All.Where(user.IsInRole).ToArray();
        return new QualityIncidentActor(userName, roles);
    }
}