using DocuMind.Application;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Features.Chat.Commands;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocuMind.UnitTests.Messaging;

public class SenderTests
{
    private static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var host = new TestHost(FakeCurrentUser.As(DocuMind.Domain.Security.UserRole.User));
        configure?.Invoke(host.Services);
        return host.Services.BuildServiceProvider();
    }

    [Fact]
    public async Task Validation_behavior_rejects_invalid_commands_before_the_handler_runs()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            sender.Send(new AskQuestionCommand(Guid.Empty, "")));

        Assert.Contains(ex.Errors, e => e.PropertyName == nameof(AskQuestionCommand.SessionId));
        Assert.Contains(ex.Errors, e => e.PropertyName == nameof(AskQuestionCommand.Question));
    }

    [Fact]
    public async Task Behaviors_wrap_the_handler_in_registration_order()
    {
        var log = new List<string>();
        await using var provider = BuildProvider(s =>
        {
            s.AddSingleton(log);
            s.AddScoped<IRequestHandler<Ping, string>, PingHandler>();
            s.AddTransient<IPipelineBehavior<Ping, string>, OuterBehavior>();
            s.AddTransient<IPipelineBehavior<Ping, string>, InnerBehavior>();
        });
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Ping());

        Assert.Equal("pong", result);
        Assert.Equal(["outer:before", "inner:before", "handler", "inner:after", "outer:after"], log);
    }

    public sealed record Ping : IQuery<string>;

    private sealed class PingHandler(List<string> log) : IQueryHandler<Ping, string>
    {
        public Task<string> Handle(Ping request, CancellationToken cancellationToken)
        {
            log.Add("handler");
            return Task.FromResult("pong");
        }
    }

    private abstract class RecordingBehavior(List<string> log, string name) : IPipelineBehavior<Ping, string>
    {
        public async Task<string> Handle(Ping request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            log.Add($"{name}:before");
            var response = await next();
            log.Add($"{name}:after");
            return response;
        }
    }

    private sealed class OuterBehavior(List<string> log) : RecordingBehavior(log, "outer");

    private sealed class InnerBehavior(List<string> log) : RecordingBehavior(log, "inner");
}
