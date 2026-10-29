namespace Vessel.Tests.Identity;

// Collection definitions are assembly-local. Keep the original scheduling rule in both split suites.
[CollectionDefinition("Identity streams", DisableParallelization = true)]
public sealed class IdentityStreamCollection;
