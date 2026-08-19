using Modgud.Api.Features.ChangeFeed;
using Modgud.Infrastructure.ChangeFeed;

namespace Modgud.Tests.Unit.ChangeFeed;

public class AppChangeFeedCursorTests
{
    [Fact]
    public void Cursor_round_trips_all_boundary_fields()
    {
        var appId = Guid.NewGuid();
        var encoded = AppChangeFeedCursor.Encode(appId, 3, 9_876_543_210, 42);

        var parsed = AppChangeFeedCursor.TryDecode(encoded, out var cursor);

        Assert.True(parsed);
        Assert.Equal(appId, cursor.AppId);
        Assert.Equal(3, cursor.Generation);
        Assert.Equal(9_876_543_210, cursor.Sequence);
        Assert.Equal(42, cursor.Ordinal);
    }

    [Fact]
    public void Checkpoint_uses_the_state_high_water_and_max_ordinal()
    {
        var state = new AppChangeFeedState
        {
            Id = Guid.NewGuid(),
            Generation = 7,
            LastProcessedSequence = 1234,
        };

        Assert.True(AppChangeFeedCursor.TryDecode(
            AppChangeFeedCursor.EncodeCheckpoint(state), out var cursor));
        Assert.Equal(state.Id, cursor.AppId);
        Assert.Equal(7, cursor.Generation);
        Assert.Equal(1234, cursor.Sequence);
        Assert.Equal(int.MaxValue, cursor.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("AQ")]
    public void Malformed_cursors_are_rejected_without_throwing(string value)
    {
        Assert.False(AppChangeFeedCursor.TryDecode(value, out _));
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, -1, 0)]
    [InlineData(1, 1, -2)]
    public void Semantically_invalid_cursors_are_rejected(
        int generation,
        long sequence,
        int ordinal)
    {
        var encoded = AppChangeFeedCursor.Encode(
            Guid.NewGuid(), generation, sequence, ordinal);

        Assert.False(AppChangeFeedCursor.TryDecode(encoded, out _));
    }
}
