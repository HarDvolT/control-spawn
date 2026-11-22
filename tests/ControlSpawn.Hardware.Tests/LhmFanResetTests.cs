using ControlSpawn.Hardware;
using Xunit;

namespace ControlSpawn.Hardware.Tests;

public sealed class LhmFanResetTests
{
    [Fact]
    public void FirstOpenFailureRetriesOnceWithANewComputerAndResets()
    {
        var control = new FakeControl();
        var first = new FakeComputer { OpenError = new IOException("still shutting down") };
        var second = new FakeComputer();
        second.Hardware.Add(new FakeHardware(control));
        var computers = new Queue<FakeComputer>([first, second]);
        var waits = new List<TimeSpan>();

        LhmFanReset.RestoreAll(() => computers.Dequeue(), waits.Add);

        Assert.Equal(1, first.Opens);
        Assert.Equal(1, first.Closes);
        Assert.Equal(1, second.Opens);
        Assert.Equal(1, second.Closes);
        Assert.Equal(1, control.Resets);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(500) }, waits);
    }

    [Fact]
    public void SecondOpenFailurePropagatesAndClosesBothComputers()
    {
        var first = new FakeComputer { OpenError = new IOException("first") };
        var second = new FakeComputer { OpenError = new IOException("second") };
        var computers = new Queue<FakeComputer>([first, second]);

        Assert.Throws<IOException>(() => LhmFanReset.RestoreAll(() => computers.Dequeue(), _ => { }));

        Assert.Equal(1, first.Closes);
        Assert.Equal(1, second.Closes);
        Assert.Empty(computers);
    }

    [Fact]
    public void AThrowingCloseDoesNotHideTheOpenFailure()
    {
        var openError = new IOException("open");
        var first = new FakeComputer { OpenError = openError, CloseError = new InvalidOperationException("first close") };
        var second = new FakeComputer { OpenError = openError, CloseError = new InvalidOperationException("second close") };
        var computers = new Queue<FakeComputer>([first, second]);

        var thrown = Assert.Throws<IOException>(() => LhmFanReset.RestoreAll(() => computers.Dequeue(), _ => { }));

        Assert.Same(openError, thrown);
    }

    [Fact]
    public void AThrowingControlDoesNotSkipTheRemainingControls()
    {
        var bad = new FakeControl { Error = new InvalidOperationException("control") };
        var good = new FakeControl();
        var nested = new FakeControl();
        var child = new FakeHardware(nested);
        var root = new FakeHardware(bad, good);
        root.Children.Add(child);
        var computer = new FakeComputer();
        computer.Hardware.Add(root);

        LhmFanReset.RestoreAll(() => computer, _ => { });

        Assert.Equal(1, bad.Resets);
        Assert.Equal(1, good.Resets);
        Assert.Equal(1, nested.Resets);
        Assert.Equal(1, child.Updates);
    }

    private sealed class FakeComputer : LhmFanReset.IFanResetComputer
    {
        public Exception? OpenError { get; init; }
        public Exception? CloseError { get; init; }
        public List<FakeHardware> Hardware { get; } = [];
        public int Opens { get; private set; }
        public int Closes { get; private set; }
        public void Open() { Opens++; if (OpenError is not null) throw OpenError; }
        public void Close() { Closes++; if (CloseError is not null) throw CloseError; }
        IEnumerable<LhmFanReset.IFanResetHardware> LhmFanReset.IFanResetComputer.Hardware => Hardware;
    }

    private sealed class FakeHardware(params FakeControl[] controls) : LhmFanReset.IFanResetHardware
    {
        public List<FakeControl> Controls { get; } = [.. controls];
        public List<FakeHardware> Children { get; } = [];
        public int Updates { get; private set; }
        public void Update() => Updates++;
        IEnumerable<LhmFanReset.IFanResetControl> LhmFanReset.IFanResetHardware.Controls => Controls;
        IEnumerable<LhmFanReset.IFanResetHardware> LhmFanReset.IFanResetHardware.SubHardware => Children;
    }

    private sealed class FakeControl : LhmFanReset.IFanResetControl
    {
        public Exception? Error { get; init; }
        public int Resets { get; private set; }
        public void SetDefault() { Resets++; if (Error is not null) throw Error; }
    }
}
