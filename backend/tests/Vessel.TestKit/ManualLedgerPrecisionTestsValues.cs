using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public partial class ManualLedgerPrecisionTestsValues
{
    internal const string Large = "10000000000000000000000000000";
    internal const string LargePlusOne = "10000000000000000000000000001";
    internal const string TwiceLarge = "20000000000000000000000000000";
    internal static readonly DateTimeOffset At = ReviewClock.Now.AddDays(-1);

    internal static Play CreatePlay(string direction, Account? account = null)
    {
        account ??= new Account(Guid.NewGuid(), Guid.NewGuid(), "manual", "Precision");
        return new(Guid.NewGuid(), account, new("manual", "BTC"), InstrumentSource.Manual,
            PlayDocuments.Serialize(TestPlays.Plan() with { Direction = direction }), At.AddSeconds(-1));
    }

    internal static ManualExecution Row(Play play, string role, string quantity, string price, int seconds)
    {
        var side = PlayDocuments.Read(play.Plan).Direction == "long" ? "buy" : "sell";
        return new()
        {
            Id = Guid.NewGuid(), OwnerId = play.OwnerId, PlayId = play.Id, Role = role,
            EntryId = role == "entry" ? "entry-1" : null,
            Side = role == "entry" ? side : side == "buy" ? "sell" : "buy",
            Quantity = quantity, Price = price, Fee = "0", FeeCurrency = "USDC",
            Source = "Paper broker", OccurredAtUtc = At.AddSeconds(seconds)
        };
    }
}
