using FluentAssertions;
using IrisAura.Contracts;

namespace IrisAura.Island.Tests;

/// <summary>Pipe names must match BLUEPRINT §0.2 exactly and reject malformed SIDs.</summary>
public sealed class PipeNamesTests
{
    [Fact]
    public void Island_activities_pipe_appends_the_user_sid()
    {
        PipeNames.IslandActivities("S-1-5-21-1004336348-1177238915-682003330-1001")
            .Should().Be("IrisAura.Island.Activities.S-1-5-21-1004336348-1177238915-682003330-1001");
    }

    [Fact]
    public void Face_pipe_names_match_the_blueprint()
    {
        PipeNames.FaceAuth.Should().Be("IrisAura.Face.Auth");
        PipeNames.FaceAdmin.Should().Be("IrisAura.Face.Admin");
        PipeNames.FaceEvents.Should().Be("IrisAura.Face.Events");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-")]
    [InlineData("S-1-5")]
    [InlineData("s-1-5-18")]
    [InlineData("S-1-5-21-..\\evil")]
    [InlineData("S-1-5-21-1-2-3-1001\\..\\other")]
    [InlineData("S-1-5--18")]
    [InlineData("S-1-5-18-")]
    [InlineData("S-1-5-99999999999")]
    public void Malformed_sids_are_rejected(string? sid)
    {
        var act = () => PipeNames.IslandActivities(sid!);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-1013")]
    public void Well_formed_sids_are_accepted(string sid)
    {
        var act = () => PipeNames.IslandActivities(sid);
        act.Should().NotThrow();
    }

    [Fact]
    public void Protocol_versions_start_at_one()
    {
        ProtocolVersions.IslandSdk.Should().Be(1);
        ProtocolVersions.Face.Should().Be(1);
    }
}
