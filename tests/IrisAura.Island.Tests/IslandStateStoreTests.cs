using System.IO;
using FluentAssertions;
using IrisAura.Island.Engine;

namespace IrisAura.Island.Tests;

/// <summary>Persisted Island state (quiet mode, hide-until).</summary>
public sealed class IslandStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "irisaura-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var store = new IslandStateStore(Path.Combine(_dir, "state.json"));
        store.Load().Should().Be(new IslandState());
    }

    [Fact]
    public void State_round_trips_and_creates_the_folder()
    {
        var path = Path.Combine(_dir, "nested", "state.json");
        var store = new IslandStateStore(path);
        var state = new IslandState { QuietMode = true, HiddenUntil = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero) };

        store.Save(state);

        new IslandStateStore(path).Load().Should().Be(state);
        File.Exists(path + ".tmp").Should().BeFalse("written atomically via a temp file");
    }

    [Fact]
    public void Corrupt_file_gives_defaults_instead_of_crashing()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{ not json");

        new IslandStateStore(path).Load().Should().Be(new IslandState());
    }

    [Fact]
    public void State_file_contains_no_activity_content()
    {
        var path = Path.Combine(_dir, "state.json");
        new IslandStateStore(path).Save(new IslandState { QuietMode = true });

        var json = File.ReadAllText(path);
        json.Should().Contain("QuietMode").And.Contain("HiddenUntil");
        json.Split('"').Where((_, i) => i % 2 == 1).Should().OnlyContain(s => s == "QuietMode" || s == "HiddenUntil");
    }
}
