using Vessel.Domain.Common;
using System.Text.Json;

namespace Vessel.Domain.Paper;

/// <summary>Owner opt-in, independent of the real account monitor.</summary>
public sealed class PaperSettings : IOwned
{
    public Guid OwnerId { get; set; }
    public bool Enabled { get; set; }
    public int CheckIntervalMinutes { get; set; } = 60;
}

/// <summary>Append-only virtual cash facts. The seed is capital, never execution profit.</summary>
public sealed class PaperLedgerEntry : IOwned
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string TradingMode { get; private set; } = "paper";
    public Guid AccountId { get; private set; }
    public Guid? PlayId { get; private set; }
    public Guid? ExecutionId { get; private set; }
    public string Kind { get; private set; } = null!;
    public decimal AmountUsd { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }

    private PaperLedgerEntry() { }

    public static PaperLedgerEntry Seed(Guid ownerId, Guid accountId, decimal amount, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty || accountId == Guid.Empty || amount < 0)
            throw new ArgumentException("A paper seed needs an owner, account and nonnegative capital.");
        return new() { Id = Guid.NewGuid(), OwnerId = ownerId, AccountId = accountId, Kind = "seed", AmountUsd = amount, OccurredAtUtc = now };
    }

    public static PaperLedgerEntry Realized(PaperExecution execution) => new()
    {
        Id = Guid.NewGuid(), OwnerId = execution.OwnerId, AccountId = execution.AccountId, PlayId = execution.PlayId,
        ExecutionId = execution.Id, Kind = "realized-pnl", AmountUsd = execution.PnlUsd, OccurredAtUtc = execution.OccurredAtUtc
    };
}

/// <summary>A simulated execution, not a venue/import/manual fact. Prices are defined levels; costs are zero.</summary>
public sealed class PaperExecution : IOwned
{
    /// <summary>Closed-one-minute defined-price touches; zero fees/slippage; funding and liquidation not modeled.</summary>
    public int ModelVersion { get; private set; } = 1;
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string TradingMode { get; private set; } = "paper";
    public Guid AccountId { get; set; }
    public Guid PlayId { get; set; }
    public string EntryId { get; set; } = null!;
    public string? LevelId { get; set; }
    public string Role { get; set; } = null!;
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal PnlUsd { get; set; }
    /// <summary>Committed margin change: positive on entry, negative on exit.</summary>
    public decimal MarginUsd { get; set; }
    public int PlanVersion { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset CandleOpenAtUtc { get; set; }
    public DateTimeOffset CandleCloseAtUtc { get; set; }
}

/// <summary>Durable evaluation frontier. A gap or ambiguity holds all remaining capital and cannot be guessed past.</summary>
public sealed class PaperSimulation : IOwned
{
    public Guid PlayId { get; set; }
    public Guid OwnerId { get; set; }
    public string TradingMode { get; private set; } = "paper";
    public Guid AccountId { get; set; }
    public string State { get; set; } = "waiting";
    public DateTimeOffset? CheckedAtUtc { get; set; }
    public DateTimeOffset? NextCheckAtUtc { get; set; }
    public DateTimeOffset? CheckpointUtc { get; set; }
    public decimal? PreviousClose { get; set; }
    public DateTimeOffset? PreviousCloseAtUtc { get; set; }
    public string IssuesJson { get; set; } = "[]";
    public string DeclinedEntriesJson { get; set; } = "[]";
    public string SuspensionsJson { get; set; } = "[]";

    public IReadOnlyList<PaperDeclinedEntry> DeclinedEntries() =>
        JsonSerializer.Deserialize<PaperDeclinedEntry[]>(DeclinedEntriesJson) ?? [];
    public IReadOnlyList<PaperSuspension> Suspensions() =>
        JsonSerializer.Deserialize<PaperSuspension[]>(SuspensionsJson) ?? [];

    public void Suspend(string reason, DateTimeOffset at)
    {
        if (NextCheckAtUtc is null || State is not ("waiting" or "ready")) return;
        SuspensionsJson = JsonSerializer.Serialize(Suspensions().Append(new PaperSuspension(reason, at, null)));
        State = "suspended";
        NextCheckAtUtc = null;
    }

    public void Resume(DateTimeOffset boundary, int interval)
    {
        if (State != "suspended") return;
        SuspensionsJson = JsonSerializer.Serialize(Suspensions().Select(s =>
            s.ResumedAtUtc is null ? s with { ResumedAtUtc = boundary } : s));
        CheckpointUtc = boundary;
        PreviousClose = null;
        PreviousCloseAtUtc = null;
        IssuesJson = "[]";
        State = "waiting";
        NextCheckAtUtc = boundary.AddMinutes(interval);
    }
}

public sealed record PaperDeclinedEntry(string EntryId, string Reason, DateTimeOffset DeclinedAtUtc);
public sealed record PaperSuspension(string Reason, DateTimeOffset SuspendedAtUtc, DateTimeOffset? ResumedAtUtc);

/// <summary>A complete plan snapshot effective at a full minute open; prior snapshots remain immutable.</summary>
public sealed class PaperPlanActivation : IOwned
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string TradingMode { get; private set; } = "paper";
    public Guid AccountId { get; set; }
    public Guid PlayId { get; set; }
    public int PlanVersion { get; set; }
    public string PlanJson { get; set; } = null!;
    public DateTimeOffset EffectiveAtUtc { get; set; }
}

/// <summary>Unentered capital, separate from the margin already committed by entry executions.</summary>
public sealed class PaperReservation : IOwned
{
    public Guid PlayId { get; set; }
    public Guid OwnerId { get; set; }
    public string TradingMode { get; private set; } = "paper";
    public Guid AccountId { get; set; }
    public decimal AmountUsd { get; set; }
}
