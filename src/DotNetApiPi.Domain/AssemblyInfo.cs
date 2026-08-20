using System.Runtime.CompilerServices;

// The persistence layer (DotNetApiPi.Infrastructure) must be able to
// reconstitute aggregates from their persisted state and clear pending
// domain events. The reconstitution factory on Resource and the
// ClearDomainEvents operation are kept `internal` so that only trusted
// infrastructure code can bypass the aggregate's public behaviour;
// application and API code must go through the repository and the
// aggregate's own methods.
//
// The test assemblies are also granted internal access so they can exercise
// the reconstitution path and the event-clearing contract directly.
[assembly: InternalsVisibleTo("DotNetApiPi.Infrastructure")]
[assembly: InternalsVisibleTo("DotNetApiPi.Domain.Tests")]
[assembly: InternalsVisibleTo("DotNetApiPi.Infrastructure.Tests")]
[assembly: InternalsVisibleTo("DotNetApiPi.Api.Integration.Tests")]
