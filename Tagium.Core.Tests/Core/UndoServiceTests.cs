using AwesomeAssertions;
using Tagium.Core;

namespace Tagium.Core.Tests.Core;

public class UndoServiceTests
{
    private sealed class FakeCommand : IUndoCommand
    {
        private readonly int _id;

        public FakeCommand(int id)
        {
            _id = id;
        }

        public int UndoCount { get; private set; }
        public Action? OnUndo { get; set; }

        public void Undo()
        {
            UndoCount++;
            OnUndo?.Invoke();
        }

        public override string ToString()
        {
            return $"cmd-{_id}";
        }
    }

    [Fact]
    public void CanUndo_IsFalseWhenHistoryIsEmpty()
    {
        UndoService service = new();

        service.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Push_MakesCommandUndoable()
    {
        UndoService service = new();

        service.Push(new FakeCommand(1));

        service.CanUndo.Should().BeTrue();
    }

    [Fact]
    public void Undo_ExecutesMostRecentCommandFirst()
    {
        UndoService service = new();
        FakeCommand first = new(1);
        FakeCommand second = new(2);
        FakeCommand third = new(3);
        service.Push(first);
        service.Push(second);
        service.Push(third);

        service.Undo();

        third.UndoCount.Should().Be(1);
        second.UndoCount.Should().Be(0);
        first.UndoCount.Should().Be(0);
        service.CanUndo.Should().BeTrue();

        service.Undo();
        service.Undo();

        first.UndoCount.Should().Be(1);
        second.UndoCount.Should().Be(1);
        third.UndoCount.Should().Be(1);
        service.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Undo_OnEmptyHistory_DoesNothing()
    {
        UndoService service = new();

        service.Undo();

        service.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesAllCommands()
    {
        UndoService service = new();
        FakeCommand command = new(1);
        service.Push(command);
        service.Push(new FakeCommand(2));

        service.Clear();
        service.Undo();

        service.CanUndo.Should().BeFalse();
        command.UndoCount.Should().Be(0);
    }

    [Fact]
    public void Push_WhenHistoryIsFull_DropsOldestCommands()
    {
        UndoService service = new();
        List<FakeCommand> commands = [];
        for (int i = 0; i < 55; i++)
        {
            FakeCommand command = new(i);
            commands.Add(command);
            service.Push(command);
        }

        int undoCount = 0;
        while (service.CanUndo)
        {
            service.Undo();
            undoCount++;
        }

        undoCount.Should().Be(50);

        // The oldest five commands were evicted and never ran
        foreach (FakeCommand command in commands.Take(5))
        {
            command.UndoCount.Should().Be(0);
        }

        // The newest fifty commands were all undone exactly once
        foreach (FakeCommand command in commands.Skip(5))
        {
            command.UndoCount.Should().Be(1);
        }
    }

    [Fact]
    public void Undo_DoesNotAllowCommandsPushedDuringUndo()
    {
        UndoService service = new();
        FakeCommand inner = new(99);
        FakeCommand outer = new(1) { OnUndo = () => service.Push(inner) };
        service.Push(outer);

        service.Undo();

        outer.UndoCount.Should().Be(1);
        inner.UndoCount.Should().Be(0);
        service.CanUndo.Should().BeFalse();
    }
}
