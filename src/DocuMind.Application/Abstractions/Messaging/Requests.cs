namespace DocuMind.Application.Abstractions.Messaging;

/// <summary>Anything that can be dispatched through <see cref="ISender"/>.</summary>
public interface IRequest<TResponse>;

/// <summary>A request that changes state. Handlers may write through repositories and the unit of work.</summary>
public interface ICommand<TResponse> : IRequest<TResponse>;

/// <summary>A side-effect free request. Handlers read through the no-tracking read side only.</summary>
public interface IQuery<TResponse> : IRequest<TResponse>;

/// <summary>Return type for commands that produce no value.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = new();
}

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>;

public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>;

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>Cross-cutting step wrapped around every request handler (validation, logging, ...).</summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

/// <summary>
/// Dispatches commands and queries to their single handler. Streaming results are modelled as
/// a request whose response is an <see cref="IAsyncEnumerable{T}"/>, so validation and lookups
/// run (and can fail cleanly) before the first item is produced.
/// </summary>
public interface ISender
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
