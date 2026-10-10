using DataTrace.Application.Mes;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

public sealed class MesOutboxReliabilityTests
{
    [Fact]
    public async Task Pending_query_filters_backoff_before_applying_the_batch_limit()
    {
        using var workspace = new TempWorkspace();
        await using var config = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var now = DateTime.Now;
        for (var index = 1; index <= 201; index++)
        {
            config.MesOutbox.Add(new MesOutboxItem
            {
                EventId = $"event-{index}",
                MonthKey = "202610",
                SerialNo = $"S{index}",
                PalletCode = $"P{index}",
                CreatedAt = now.AddSeconds(index),
                NextAttemptAt = index <= 200 ? now.AddHours(1) : null,
                Status = MesOutboxStatus.Pending,
                PayloadJson = "{}"
            });
        }

        await config.SaveChangesAsync();
        var queue = new MesOutboxQueue(TestDatabase.FactoryFor(config));

        var pending = await queue.TakePendingAsync(200);

        Assert.Single(pending);
        Assert.Equal("S201", pending[0].SerialNo);
    }

    [Fact]
    public async Task Relay_retries_idempotently_after_enqueue_before_marking_the_runtime_intent()
    {
        using var workspace = new TempWorkspace();
        await using var config = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var runtime = new RuntimeDbFactory(workspace.Path("runtime"));
        await using (var db = runtime.Open("202610"))
        {
            db.MesOutboxIntents.Add(new MesOutboxIntent
            {
                EventId = "event-1",
                MonthKey = "202610",
                PalletSessionId = 12,
                SerialNo = "S1",
                PalletCode = "P1",
                CreatedAt = DateTime.Now,
                PayloadJson = "{}"
            });
            await db.SaveChangesAsync();
        }

        var queue = new MesOutboxQueue(TestDatabase.FactoryFor(config));
        var interruptedRelay = new MesOutboxRelay(runtime, new ThrowAfterEnqueueOnce(queue));

        await Assert.ThrowsAsync<IOException>(() => interruptedRelay.RelayPendingAsync(10));
        Assert.Equal(1, await config.MesOutbox.CountAsync());

        var relay = new MesOutboxRelay(runtime, queue);
        Assert.Equal(1, await relay.RelayPendingAsync(10));
        Assert.Equal(0, await relay.RelayPendingAsync(10));
        Assert.Equal("event-1", (await config.MesOutbox.SingleAsync()).EventId);
        await using var verifyRuntime = runtime.Open("202610");
        Assert.NotNull((await verifyRuntime.MesOutboxIntents.SingleAsync()).PublishedAt);
    }

    [Fact]
    public async Task Spool_replay_persists_the_mes_intent_with_the_recovered_collect()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var spool = ctx.Provider.GetRequiredService<ISpoolStore>();
        var now = DateTime.Now;
        var request = new CollectSaveRequest
        {
            MonthKey = "202610",
            UpsertSession = new PalletSession
            {
                SerialNo = "S-REPLAY",
                PalletCode = "P-REPLAY",
                StartTime = now,
                Status = SessionStatus.Closed
            },
            CloseSession = true,
            RemoveActiveSession = true,
            Record = new CollectRecord
            {
                SerialNo = "S-REPLAY",
                PalletCode = "P-REPLAY",
                StationId = 10,
                StationCode = "ST010",
                TriggerTime = now,
                CompleteTime = now,
                ResultCode = ResultCodes.Success,
                Judgement = Judgement.Ok
            },
            MesOutbox = new MesOutboxIntent
            {
                EventId = "event-replay",
                MonthKey = "202610",
                SerialNo = "S-REPLAY",
                PalletCode = "P-REPLAY",
                CreatedAt = now,
                PayloadJson = "{\"serialNo\":\"S-REPLAY\"}"
            }
        };
        await spool.SaveAsync(request);

        var replay = new SpoolReplayRunner(ctx.ScopeFactory, spool, ctx.Logger<SpoolReplayRunner>());
        var replayed = await replay.ReplayAllAsync();
        var failures = await spool.ListEntriesAsync();
        Assert.True(replayed == 1, failures.FirstOrDefault()?.LastError);

        var factory = ctx.Provider.GetRequiredService<RuntimeDbFactory>();
        await using var db = factory.Open("202610");
        var intent = await db.MesOutboxIntents.SingleAsync();
        var record = await db.CollectRecords.SingleAsync();
        Assert.Equal("event-replay", intent.EventId);
        Assert.Equal("S-REPLAY", intent.SerialNo);
        Assert.Equal(record.PalletSessionId, intent.PalletSessionId);
    }

    private sealed class ThrowAfterEnqueueOnce(IMesOutboxQueue inner) : IMesOutboxQueue
    {
        private bool _thrown;

        public async Task EnqueueIfMissingAsync(MesOutboxMessage message, CancellationToken cancellationToken = default)
        {
            await inner.EnqueueIfMissingAsync(message, cancellationToken);
            if (!_thrown)
            {
                _thrown = true;
                throw new IOException("simulated process interruption");
            }
        }

        public Task<IReadOnlyList<MesOutboxPending>> TakePendingAsync(int take, CancellationToken cancellationToken = default)
            => inner.TakePendingAsync(take, cancellationToken);

        public Task SaveAttemptsAsync(IReadOnlyList<MesOutboxAttempt> attempts, CancellationToken cancellationToken = default)
            => inner.SaveAttemptsAsync(attempts, cancellationToken);
    }
}