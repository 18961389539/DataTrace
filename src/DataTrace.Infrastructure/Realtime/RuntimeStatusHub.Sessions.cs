using DataTrace.Application.Alarms;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Infrastructure.Realtime;

partial class RuntimeStatusHub
{
    public IReadOnlyList<OpenSessionNotice> OpenSessions
    {
        get
        {
            lock (_gate)
            {
                return _openSessions;
            }
        }
    }

    public void ReplaceOpenSessions(IReadOnlyList<OpenSessionNotice> sessions)
    {
        var next = sessions.ToList();
        lock (_gate)
        {
            if (SameOpen(_openSessions, next))
            {
                return;
            }

            _openSessions = next;
        }

        ScheduleChanged();
    }

    public IReadOnlyList<InProcessRecipe> InProcessRecipes
    {
        get
        {
            lock (_gate)
            {
                return _inProcessRecipes;
            }
        }
    }

    public void ReplaceInProcessRecipes(IReadOnlyList<InProcessRecipe> recipes)
    {
        var next = recipes.ToList();
        lock (_gate)
        {
            if (SameRecipes(_inProcessRecipes, next))
            {
                return;
            }

            _inProcessRecipes = next;
        }

        ScheduleChanged();
    }

    private static bool SameRecipes(IReadOnlyList<InProcessRecipe> left, IReadOnlyList<InProcessRecipe> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Code, right[i].Code, StringComparison.Ordinal) || left[i].Count != right[i].Count)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameOpen(IReadOnlyList<OpenSessionNotice> left, IReadOnlyList<OpenSessionNotice> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].SessionId != right[i].SessionId
                || left[i].PalletCode != right[i].PalletCode
                || left[i].StationCode != right[i].StationCode
                || left[i].OpenMinutes != right[i].OpenMinutes)
            {
                return false;
            }
        }

        return true;
    }
}
