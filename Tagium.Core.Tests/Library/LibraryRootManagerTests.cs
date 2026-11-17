using AwesomeAssertions;
using Tagium.Core.Library;

namespace Tagium.Core.Tests.Library;

public class LibraryRootManagerTests
{
    private sealed class Roots
    {
        public List<LibraryRoot> Items { get; } = [];
        public List<string> Operations { get; } = [];
        public int Notifications { get; private set; }

        public void Existing(string path) => Items.Add(new LibraryRoot { Id = Guid.NewGuid(), Path = path });

        public LibraryRootManager Create() => new(
            () => Items.ToList(),
            path => { Operations.Add($"add:{path}"); Items.Add(new LibraryRoot { Id = Guid.NewGuid(), Path = path }); },
            (id, path) => { Operations.Add($"delete:{path}"); Items.RemoveAll(root => root.Id == id); },
            () => Notifications++);
    }

    [Fact]
    public void EmptyPath_DoesNotChangeRootsOrNotify()
    {
        Roots roots = new();

        AddRootResult result = roots.Create().AddWithValidation("   ");

        result.Status.Should().Be(AddRootStatus.Duplicate);
        result.Message.Should().Be("Invalid path.");
        roots.Operations.Should().BeEmpty();
        roots.Notifications.Should().Be(0);
    }

    [Fact]
    public void Duplicate_IgnoresCaseAndTrailingSeparator()
    {
        Roots roots = new();
        string path = Path.Combine("library", "photos");
        roots.Existing(path);

        AddRootResult result = roots.Create().AddWithValidation(path.ToUpperInvariant() + Path.DirectorySeparatorChar);

        result.Status.Should().Be(AddRootStatus.Duplicate);
        roots.Operations.Should().BeEmpty();
        roots.Notifications.Should().Be(0);
    }

    [Fact]
    public void NestedRoot_IsRejectedWithoutChanges()
    {
        Roots roots = new();
        string parent = Path.Combine("library", "photos");
        roots.Existing(parent);

        AddRootResult result = roots.Create().AddWithValidation(Path.Combine(parent, "2024"));

        result.Status.Should().Be(AddRootStatus.NestedInsideExisting);
        result.Message.Should().Contain(parent);
        roots.Operations.Should().BeEmpty();
        roots.Notifications.Should().Be(0);
    }

    [Fact]
    public void ParentRoot_ConsolidatesChildrenBeforeAddingAndNotifiesOnce()
    {
        Roots roots = new();
        string parent = Path.Combine("library", "photos");
        string first = Path.Combine(parent, "2023");
        string second = Path.Combine(parent, "2024");
        string sibling = parent + "2";
        roots.Existing(first);
        roots.Existing(second);
        roots.Existing(sibling);

        AddRootResult result = roots.Create().AddWithValidation($"  {parent}  ");

        result.Status.Should().Be(AddRootStatus.ConsolidatedChildren);
        result.ConsolidatedPaths.Should().Equal(first, second);
        roots.Operations.Should().Equal($"delete:{first}", $"delete:{second}", $"add:{parent}");
        roots.Items.Select(root => root.Path).Should().BeEquivalentTo(new[] { sibling, parent });
        roots.Notifications.Should().Be(1);
    }

    [Fact]
    public void SiblingWithSamePrefix_IsAddedWithoutConsolidation()
    {
        Roots roots = new();
        string path = Path.Combine("library", "photos");
        roots.Existing(path + "2");

        AddRootResult result = roots.Create().AddWithValidation(path);

        result.Status.Should().Be(AddRootStatus.Success);
        roots.Operations.Should().Equal($"add:{path}");
        roots.Notifications.Should().Be(1);
    }
}
