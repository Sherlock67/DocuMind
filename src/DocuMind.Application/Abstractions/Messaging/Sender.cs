using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace DocuMind.Application.Abstractions.Messaging;

/// <summary>
/// Minimal in-process mediator. Resolves the handler for a request from DI and wraps it in
/// every registered <see cref="IPipelineBehavior{TRequest,TResponse}"/>. Generic wrappers are
/// built once per request type and cached, so dispatch costs one dictionary lookup.
/// </summary>
internal sealed class Sender(IServiceProvider serviceProvider) : ISender
{
    private static readonly ConcurrentDictionary<Type, object> RequestWrappers = new();

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (RequestWrapper<TResponse>)RequestWrappers.GetOrAdd(
            request.GetType(),
            static (requestType, responseType) => Activator.CreateInstance(
                typeof(RequestWrapper<,>).MakeGenericType(requestType, responseType))!,
            typeof(TResponse));

        return wrapper.Handle(request, serviceProvider, cancellationToken);
    }

    private abstract class RequestWrapper<TResponse>
    {
        public abstract Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider services, CancellationToken ct);
    }

    private sealed class RequestWrapper<TRequest, TResponse> : RequestWrapper<TResponse>
        where TRequest : IRequest<TResponse>
    {
        public override Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider services, CancellationToken ct)
        {
            var typed = (TRequest)request;
            // Resolve the handler lazily so a request rejected by validation never builds its dependencies.
            RequestHandlerDelegate<TResponse> pipeline = () =>
                services.GetRequiredService<IRequestHandler<TRequest, TResponse>>().Handle(typed, ct);

            // Registration order = outermost first, so wrap in reverse.
            foreach (var behavior in services.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
            {
                var next = pipeline;
                pipeline = () => behavior.Handle(typed, next, ct);
            }

            return pipeline();
        }
    }
}
