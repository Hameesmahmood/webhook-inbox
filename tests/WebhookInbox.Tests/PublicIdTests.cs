using WebhookInbox.Core;

namespace WebhookInbox.Tests;

public sealed class PublicIdTests
{
    [Theory]
    [InlineData("evt_1", true, 1)]
    [InlineData("evt_42", true, 42)]
    [InlineData("evt_0", false, 0)]
    [InlineData("evt_-1", false, 0)]
    [InlineData("evt_ 1", false, 0)]
    [InlineData("evt_", false, 0)]
    [InlineData("dlv_1", false, 0)]
    [InlineData("1", false, 0)]
    [InlineData("evt_99999999999999999999", false, 0)]
    public void Parses_only_well_formed_event_ids(string value, bool valid, long expected)
    {
        Assert.Equal(valid, PublicId.TryParseEvent(value, out var id));
        Assert.Equal(expected, id);
    }
}
