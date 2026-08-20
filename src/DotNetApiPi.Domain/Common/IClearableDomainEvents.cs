namespace DotNetApiPi.Domain.Common;

/// <summary>
/// Non-public companion to <see cref="IHasDomainEvents"/> that allows the
/// unit of work to clear pending domain events after they have been
/// dispatched.
/// <para>
/// Keeping the "clear" operation <em>internal</em> (and exposing it only to
/// the trusted infrastructure assembly via <c>InternalsVisibleTo</c>) means
/// that arbitrary application or API code can read pending events but cannot
/// wipe them, preserving the "dispatch exactly once per unit of work"
/// guarantee.
/// </para>
/// </summary>
internal interface IClearableDomainEvents
{
    /// <summary>
    /// Removes all pending domain events. Invoked by the event dispatcher
    /// after the events have been dispatched.
    /// </summary>
    void ClearDomainEvents();
}
