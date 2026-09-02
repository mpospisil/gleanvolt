using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Vehicles;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The host half of issue #140: run each registered service on the delay <b>it</b> asks for, write
/// what comes back into the holder, and stop asking one that is blocked on its owner.
///
/// <para>The clock is a fake whose timers fire at once, so a fifteen-minute cadence is asserted by
/// what the worker <i>asked</i> for rather than waited for.</para>
/// </summary>
public class VehicleUpdateWorkerTests
{
    /// <summary>Every delay is granted immediately, and every one of them is remembered.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        private readonly List<TimeSpan> _delays = [];

        public IReadOnlyList<TimeSpan> Delays
        {
            get { lock (_delays) { return _delays.ToList(); } }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_delays)
            {
                _delays.Add(dueTime);
            }

            // Queued rather than called here: firing inside CreateTimer would run the awaiting
            // continuation on this stack and recurse once per loop iteration.
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A service that answers from a script and says when it has been asked enough times.</summary>
    private sealed class StubService(Func<int, VehicleState?> answer) : IVehicleUpdateService
    {
        private int _fetches;

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AskedEnough { get; init; } = 1;

        public int Fetches => Volatile.Read(ref _fetches);

        public string VehicleId => "id4";

        public string Manufacturer => "vw-group";

        public VehicleSourceHealth Health { get; set; } = VehicleSourceHealth.Starting;

        public TimeSpan NextDelay { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>How the service moves its own cadence, if the test is about that.</summary>
        public Func<int, TimeSpan>? DelayAfter { get; init; }

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _fetches);

            if (DelayAfter is not null)
            {
                NextDelay = DelayAfter(count);
            }

            if (count >= AskedEnough)
            {
                Asked.TrySetResult();
            }

            return Task.FromResult(answer(count));
        }
    }

    private static VehicleState Reading(double soc) =>
        new(new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero), SocPercent: soc, SourceId: "vw-group");

    private static VehicleUpdateWorker Worker(
        VehicleStateHolder holder, TimeProvider time, bool mqttFeedEnabled = false,
        params IVehicleUpdateService[] services) =>
        new(services,
            holder,
            NullLogger<VehicleUpdateWorker>.Instance,
            Options.Create(new VehicleOptions { Enabled = mqttFeedEnabled }),
            time);

    /// <summary>Starts the worker, waits for the service to have been asked, then stops it.</summary>
    private static async Task RunAsync(VehicleUpdateWorker worker, StubService service)
    {
        await worker.StartAsync(CancellationToken.None);
        await service.Asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task What_a_service_returns_reaches_the_holder()
    {
        var holder = new VehicleStateHolder();
        var service = new StubService(_ => Reading(57));

        await RunAsync(Worker(holder, new ImmediateTimeProvider(), services: service), service);

        Assert.Equal(57, holder.GetCurrentState()?.SocPercent);
    }

    [Fact]
    public async Task A_fetch_that_produced_nothing_leaves_the_last_reading_standing()
    {
        // #73's rule, kept: "the reading is getting older" is diagnosable, whereas blanking it looks
        // exactly like having no car at all.
        var holder = new VehicleStateHolder();
        var service = new StubService(count => count == 1 ? Reading(57) : null) { AskedEnough = 3 };

        await RunAsync(Worker(holder, new ImmediateTimeProvider(), services: service), service);

        Assert.Equal(57, holder.GetCurrentState()?.SocPercent);
    }

    [Fact]
    public async Task The_delay_is_the_services_own_and_is_re_read_after_every_fetch()
    {
        // The interval belongs to the service, and moves: a backoff is the service raising its own
        // delay, and the host honouring whatever it says next. Nothing here states a cadence.
        var time = new ImmediateTimeProvider();
        var service = new StubService(_ => null)
        {
            AskedEnough = 3,
            DelayAfter = count => count == 1 ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(1),
        };

        await RunAsync(Worker(new VehicleStateHolder(), time, services: service), service);

        Assert.Contains(TimeSpan.FromMinutes(15), time.Delays);
        Assert.Contains(TimeSpan.FromHours(1), time.Delays);
    }

    [Fact]
    public async Task A_delay_below_the_floor_is_raised_rather_than_spun_on()
    {
        // A service asking for nothing must not become a loop against somebody's identity provider.
        var time = new ImmediateTimeProvider();
        var service = new StubService(_ => null)
        {
            AskedEnough = 2,
            NextDelay = TimeSpan.Zero,
        };

        await RunAsync(Worker(new VehicleStateHolder(), time, services: service), service);

        Assert.All(time.Delays, delay => Assert.True(delay >= VehicleUpdateWorker.MinimumDelay));
    }

    [Fact]
    public async Task A_service_blocked_on_its_owner_is_asked_exactly_once()
    {
        // The rule the whole design turns on: a refused password or a consent screen is not asked
        // again, however patiently.
        var service = new StubService(_ => null)
        {
            Health = VehicleSourceHealth.NeedsOwner("The portal wants you in a browser."),
        };

        var worker = Worker(new VehicleStateHolder(), new ImmediateTimeProvider(), services: service);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, service.Fetches);
    }

    [Fact]
    public async Task A_service_that_throws_is_asked_again_rather_than_taking_the_host_down()
    {
        var holder = new VehicleStateHolder();
        var service = new StubService(count =>
            count == 1 ? throw new InvalidOperationException("a bug in a service") : Reading(41))
        { AskedEnough = 2 };

        await RunAsync(Worker(holder, new ImmediateTimeProvider(), services: service), service);

        Assert.Equal(41, holder.GetCurrentState()?.SocPercent);
    }

    [Fact]
    public async Task With_no_service_configured_it_stops_at_once_and_writes_nothing()
    {
        // The guarantee (#140): a car with no update service configured behaves exactly as it did
        // before any of this existed. The worker is registered regardless, and this is what it does.
        var holder = new VehicleStateHolder();
        var worker = Worker(holder, new ImmediateTimeProvider());

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Null(holder.GetCurrentState());
    }

    [Fact]
    public async Task Two_services_are_paced_independently()
    {
        // One manufacturer's cloud timing out must not hold up another car behind it.
        var holder = new VehicleStateHolder();
        var slow = new StubService(_ => null) { NextDelay = TimeSpan.FromHours(6) };
        var quick = new StubService(_ => Reading(62)) { AskedEnough = 2 };

        var worker = Worker(holder, new ImmediateTimeProvider(), services: [slow, quick]);

        await worker.StartAsync(CancellationToken.None);
        await quick.Asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(62, holder.GetCurrentState()?.SocPercent);
        Assert.True(quick.Fetches >= 2);
    }
}
