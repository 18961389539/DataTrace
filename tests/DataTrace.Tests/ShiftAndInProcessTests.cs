using DataTrace.Application.Runtime;
using DataTrace.Domain;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Validation;

namespace DataTrace.Tests;

public class ShiftAndInProcessTests
{
    [Fact]
    public void Day_shift_starts_at_the_configured_hour()
    {
        var window = ShiftWindow.Containing(new DateTime(2026, 9, 28, 10, 0, 0), 8, 12);

        Assert.Equal(new DateTime(2026, 9, 28, 8, 0, 0), window.Start);
        Assert.Equal(new DateTime(2026, 9, 28, 20, 0, 0), window.End);
        Assert.Equal("09-28 08:00–20:00", window.Label);
    }

    [Fact]
    public void A_time_before_the_start_hour_belongs_to_the_shift_that_crossed_midnight()
    {
        var window = ShiftWindow.Containing(new DateTime(2026, 9, 28, 3, 0, 0), SystemDefaults.ShiftStartHour, SystemDefaults.ShiftLengthHours);

        Assert.Equal(new DateTime(2026, 9, 27, 20, 0, 0), window.Start);
        Assert.Equal(new DateTime(2026, 9, 28, 8, 0, 0), window.End);
        Assert.Equal("09-27 20:00–09-28 08:00", window.Label);
    }

    [Fact]
    public void The_end_instant_belongs_to_the_next_shift()
    {
        var window = ShiftWindow.Containing(new DateTime(2026, 9, 28, 20, 0, 0), 8, 12);

        Assert.Equal(new DateTime(2026, 9, 28, 20, 0, 0), window.Start);
    }

    [Fact]
    public void An_illegal_length_falls_back_to_the_default_shift()
    {
        var window = ShiftWindow.Containing(new DateTime(2026, 9, 28, 10, 0, 0), 8, 10);

        Assert.Equal(new DateTime(2026, 9, 28, 8, 0, 0), window.Start);
        Assert.Equal(12, SystemDefaults.ShiftLengthHours);
        Assert.Equal("班次时长只能是 8、12 或 24 小时", SettingsLimits.ShiftLengthHoursError(10));
    }

    [Fact]
    public void In_process_note_appears_only_when_the_frozen_recipe_differs()
    {
        var rows = InProcessRecipes.From(
        [
            new ActiveSessionIndex { PalletCode = "P1", RecipeCode = "A100" },
            new ActiveSessionIndex { PalletCode = "P2", RecipeCode = "A100" },
            new ActiveSessionIndex { PalletCode = "P3", RecipeCode = "" }
        ]);

        Assert.Equal("在制 1 件仍用点位默认限值，2 件仍用A100。下一件起用B200。", InProcessRecipes.Note("B200", rows));
        Assert.Null(InProcessRecipes.Note("A100", [new InProcessRecipe("A100", 2)]));
        Assert.Null(InProcessRecipes.Note("B200", []));
    }
}
