namespace DotNetApiPi.Application.Common;

/// <summary>
/// Defines a handler that executes a command and returns a result.
/// </summary>
/// <typeparam name="TCommand">The type of the command being handled.</typeparam>
/// <typeparam name="TResult">The type of the result produced by the handler.</typeparam>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand
{
    /// <summary>
    /// Asynchronously handles the given command and returns a result.
    /// </summary>
    /// <param name="command">The command to handle.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation, containing the result.</returns>
    Task<TResult> HandleAsync(
        TCommand command,
        CancellationToken cancellationToken = default);
}
