using System.Diagnostics;
using Xunit.Abstractions;

namespace DataTrace.Tests;

public sealed class CrashRecoveryProbeTests(ITestOutputHelper output)
{
    private static readonly string ProbeAssembly = Path.Combine(AppContext.BaseDirectory, "DataTrace.CrashProbe.dll");

    [Fact]
    [Trait("Category", "CrashProbe")]
    public Task Kill_before_runtime_commit_rolls_back_the_uncommitted_write()
        => KillAtReadyAndVerifyAsync("crash-before-commit", "verify-empty");

    [Fact]
    [Trait("Category", "CrashProbe")]
    public Task Kill_after_commit_preserves_record_and_pending_confirmation()
        => KillAtReadyAndVerifyAsync("crash-after-commit", "verify-committed");

    [Fact]
    [Trait("Category", "CrashProbe")]
    public Task Spool_file_survives_process_kill_and_replays_once_after_restart()
        => KillAtReadyAndVerifyAsync("crash-after-spool", "recover-spool");

    [Fact]
    [Trait("Category", "CrashProbe")]
    public Task Committed_record_with_undeleted_spool_is_deduplicated_after_restart()
        => KillAtReadyAndVerifyAsync("crash-postcommit-before-spool-delete", "recover-spool");

    private async Task KillAtReadyAndVerifyAsync(string crashScenario, string recoveryScenario)
    {
        Assert.True(File.Exists(ProbeAssembly), $"Crash probe assembly not found at {ProbeAssembly}");
        using var workspace = new TempWorkspace();
        var process = StartProbe(crashScenario, workspace.Root);
        string? ready;
        try
        {
            ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Assert.StartsWith("READY|", ready);
            output.WriteLine($"Reached deterministic crash point: {ready}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }

        using var recovery = StartProbe(recoveryScenario, workspace.Root);
        var stdoutTask = recovery.StandardOutput.ReadToEndAsync();
        var stderrTask = recovery.StandardError.ReadToEndAsync();
        try
        {
            await recovery.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally
        {
            if (!recovery.HasExited)
            {
                recovery.Kill(entireProcessTree: true);
                await recovery.WaitForExitAsync();
            }
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.True(recovery.ExitCode == 0, $"Recovery probe failed ({recovery.ExitCode}). stdout: {stdout}\nstderr: {stderr}");
        Assert.Contains("VERIFIED|", stdout);
        output.WriteLine(stdout.Trim());
    }

    private static Process StartProbe(string scenario, string root)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnet)) dotnet = "dotnet";
        var start = new ProcessStartInfo
        {
            FileName = dotnet,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(ProbeAssembly);
        start.ArgumentList.Add(scenario);
        start.ArgumentList.Add(root);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not launch crash probe process");
    }
}
