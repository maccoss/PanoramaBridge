using PanoramaBridge.Core.Infrastructure;

namespace PanoramaBridge.Tests.Infrastructure;

/// <summary>
/// One PanoramaBridge per signed-in user.
/// </summary>
/// <remarks>
/// This matters more than it looks. Two copies open the same SQLite ledger, walk the same folder
/// and race PUTs of the same acquisition to the same remote path, each unaware the other is
/// uploading it. It became reachable when closing the window stopped exiting: an icon filed under
/// hidden icons is invisible, so starting the application a second time is the natural thing to
/// do.
/// </remarks>
public sealed class SingleInstanceTests
{
    /// <summary>A name of its own per test, so a parallel run cannot collide with another.</summary>
    private static string UniqueName() => "PanoramaBridgeTest-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void The_first_instance_gets_it()
    {
        using var first = SingleInstance.Acquire(UniqueName());

        first.IsFirst.ShouldBeTrue();
    }

    [Fact]
    public void A_second_instance_is_told_it_is_not_the_first()
    {
        var name = UniqueName();

        using var first = SingleInstance.Acquire(name);
        using var second = SingleInstance.Acquire(name);

        first.IsFirst.ShouldBeTrue();
        second.IsFirst.ShouldBeFalse();
    }

    [Fact]
    public void The_second_launch_reaches_the_first_rather_than_dying_quietly()
    {
        // Exiting silently would look exactly like the shortcut being broken, which is the whole
        // reason the user is double-clicking it: they cannot see the hidden window.
        var name = UniqueName();
        using var shown = new ManualResetEventSlim(false);

        using var first = SingleInstance.Acquire(name);
        first.ListenForSecondLaunch(() => shown.Set());

        using var second = SingleInstance.Acquire(name);
        second.SignalExisting().ShouldBeTrue();

        shown.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue(
            "the running instance should have been asked to show itself");
    }

    [Fact]
    public void The_name_is_released_when_the_holder_goes_away()
    {
        // Otherwise a crash would leave the application unable to start until the user signed
        // out, which is a far worse failure than the one being prevented.
        var name = UniqueName();

        var first = SingleInstance.Acquire(name);
        first.IsFirst.ShouldBeTrue();
        first.Dispose();

        using var next = SingleInstance.Acquire(name);
        next.IsFirst.ShouldBeTrue();
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        // Disposed by the using in Main and, on some paths, again on the way out.
        var instance = SingleInstance.Acquire(UniqueName());

        instance.Dispose();

        Should.NotThrow(instance.Dispose);
    }

    [Fact]
    public void Signalling_from_the_instance_that_holds_it_does_nothing()
    {
        using var only = SingleInstance.Acquire(UniqueName());

        only.SignalExisting().ShouldBeFalse("there is no other instance to wake");
    }

    [Fact]
    public void A_listener_on_a_second_instance_is_ignored()
    {
        // The second instance's job is to signal and exit. If it also listened it would sit
        // waiting on a handle it is about to close.
        var name = UniqueName();

        using var first = SingleInstance.Acquire(name);
        using var second = SingleInstance.Acquire(name);

        Should.NotThrow(() => second.ListenForSecondLaunch(() => { }));
    }
}
