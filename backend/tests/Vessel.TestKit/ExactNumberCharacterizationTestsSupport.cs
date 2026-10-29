using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Common;

namespace Vessel.Tests;

public partial class ExactNumberCharacterizationTestsSupport : ExactNumberCharacterizationTestsValues
{

    /// <summary>Manual execution quantity and price (<paramref name="positive"/>) or fee.</summary>
    internal static string ManualFact(string text, bool positive)
    {
        try { return Describe(ManualLedger.Number(text, positive)); }
        catch (ProblemException e) when (e.StatusCode == 400)
        {
            return e.Message switch
            {
                "Quantity and price must be positive decimal strings." when positive => "x",
                "Fees must be nonnegative decimal strings." when !positive => "x",
                "Decimal is outside the supported range." => "range",
                "Decimal must be exactly representable, without rounding." => "inexact",
                _ => "unexpected: " + e.Message
            };
        }
    }

    /// <summary>Plan number fields on write (a new Play), kept as typed. Stored text is read tolerantly and may be saved unchanged.</summary>
    internal static string PlanText(string? text)
    {
        try { return PlayDocuments.Normalize(TestPlays.Plan() with { Size = text! }).Size == text ? "ok" : "changed"; }
        catch (ProblemException e) when (e.Message == $"Size: {PlayDocumentTestsValues.PlainDecimalRule}") { return "x"; }
    }

    /// <summary>The sizing risk setting; "rule" means it parsed but the Domain range rejected it.</summary>
    internal static async Task<string> RiskPercent(string? text)
    {
        var store = new MemorySizingStore();
        try { await new SizingService(store, new CoreOwner(Guid.NewGuid()), TimeProvider.System, TestUnitOfWork.None).UpdateSettingsAsync(new(text!), default); }
        catch (ProblemException e) when (e.Message == "Risk per trade must be a decimal percentage.") { return "x"; }
        catch (DomainValidationException) { return "rule"; }
        return Describe(store.Settings!.RiskPercent);
    }
}
