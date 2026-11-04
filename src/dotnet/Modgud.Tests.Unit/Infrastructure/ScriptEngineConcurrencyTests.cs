using System.Collections.Concurrent;
using System.Diagnostics;
using Cocoar.JsEval.Engine;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Infrastructure;

namespace Modgud.Tests.Unit.Infrastructure;

/// <summary>
/// 698c — up to Cocoar.JsEval 4.1 every <see cref="JsEngine"/> shared one Jint <c>Options</c>.
/// Engines built at the same time could break construction for the whole process (an NRE in
/// <c>Jint.Engine.ResetConstraints</c>), and the execution timeout and statement limit were one
/// instance for all engines, so any evaluation restarted the limits of every running one.
/// These tests pin both on Modgud's own script-engine configuration.
/// </summary>
public sealed class ScriptEngineConcurrencyTests
{
    private static ServiceProvider ScriptServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModgudScriptEngine();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Engines_built_at_the_same_time_all_evaluate()
    {
        using var root = ScriptServices();
        var failures = new ConcurrentBag<Exception>();

        for (var burst = 0; burst < 5; burst++)
        {
            Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 32 }, _ =>
            {
                try
                {
                    using var scope = root.CreateScope();
                    scope.ServiceProvider.GetRequiredService<JsEngine>().Evaluate("var x = [1, 2, 3].map(v => v * 2);");
                }
                catch (Exception ex) { failures.Add(ex); }
            });
        }

        Assert.Empty(failures.Select(f => $"{(f.InnerException ?? f).GetType().Name}: {(f.InnerException ?? f).Message}").Distinct());
    }

    [Fact]
    public async Task The_execution_limits_hold_while_another_engine_evaluates()
    {
        using var root = ScriptServices();
        using var longScope = root.CreateScope();
        using var otherScope = root.CreateScope();
        var longRunning = longScope.ServiceProvider.GetRequiredService<JsEngine>();
        var other = otherScope.ServiceProvider.GetRequiredService<JsEngine>();

        using var stop = new CancellationTokenSource();
        var pinger = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                other.Evaluate("var y = 1;");
                await Task.Delay(100);
            }
        });

        var watch = Stopwatch.StartNew();
        try
        {
            // 6 s of work against Modgud's 2 s timeout (and the default statement cap, which a
            // busy loop reaches first). Under the shared limits neither ever tripped: the other
            // engine's evaluations kept restarting both.
            var ex = Record.Exception(() => longRunning.Evaluate("var t = Date.now(); while (Date.now() - t < 6000) {}"));
            watch.Stop();
            Assert.NotNull(ex);
            Assert.True(ex is TimeoutException or Jint.Runtime.StatementsCountOverflowException, ex.GetType().FullName);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"stopped only after {watch.Elapsed}");
        }
        finally
        {
            stop.Cancel();
            await pinger;
        }
    }
}
