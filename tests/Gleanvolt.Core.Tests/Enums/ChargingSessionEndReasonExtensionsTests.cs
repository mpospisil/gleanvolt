using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Tests.Enums;

public class ChargingSessionEndReasonExtensionsTests
{
    [Fact]
    public void EveryReasonHasItsOwnDescription()
    {
        // The log, the session's end event and the session page all read this. A member appended without
        // a description would throw the first time a session ended with it, and two sharing one would put
        // back exactly the ambiguity #198 removed.
        var descriptions = Enum.GetValues<ChargingSessionEndReason>().Select(reason => reason.Describe()).ToList();

        Assert.All(descriptions, description => Assert.False(string.IsNullOrWhiteSpace(description)));
        Assert.Equal(descriptions.Count, descriptions.Distinct().Count());
    }

    [Fact]
    public void TheSentenceFormOnlyCapitalisesTheClause()
    {
        Assert.Equal("no sun was left to charge on today", ChargingSessionEndReason.NoSunLeftToday.Describe());
        Assert.Equal("No sun was left to charge on today", ChargingSessionEndReason.NoSunLeftToday.DescribeSentence());
    }
}
