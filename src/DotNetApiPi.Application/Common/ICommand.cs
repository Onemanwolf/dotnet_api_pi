namespace DotNetApiPi.Application.Common;

/// <summary>
/// Marker interface for a command. A command represents an intention to
/// change the state of the system (a "write" operation in CQRS terms).
/// </summary>
public interface ICommand
{
}

/// <summary>
/// Marker interface for a query. A query represents an intention to read
/// data from the system without changing its state (a "read" operation
/// in CQRS terms).
/// </summary>
public interface IQuery
{
}
