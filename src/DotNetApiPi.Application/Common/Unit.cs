namespace DotNetApiPi.Application.Common;

/// <summary>
/// A marker type used as the result of commands that do not produce a
/// meaningful value (an equivalent of <c>void</c> for the CQRS pattern).
/// </summary>
public readonly struct Unit
{
    /// <summary>
    /// Gets the single, shared instance of the <see cref="Unit"/> struct.
    /// </summary>
    public static Unit Value { get; } = new();
}
