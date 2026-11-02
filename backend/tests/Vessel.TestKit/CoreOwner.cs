using Vessel.Application.Ownership;

namespace Vessel.Tests;

internal sealed record CoreOwner(Guid OwnerId) : IJournalOwnerContext;
