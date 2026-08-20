namespace DotNetApiPi.Domain.Enums;

/// <summary>
/// The lifecycle state of a resource.
/// </summary>
public enum ResourceStatus
{
    /// <summary>
    /// The resource has not yet been activated.
    /// </summary>
    Draft = 0,

    /// <summary>
    /// The resource is active and available.
    /// </summary>
    Active = 1,

    /// <summary>
    /// The resource has been deactivated and is no longer available.
    /// </summary>
    Archived = 2
}
