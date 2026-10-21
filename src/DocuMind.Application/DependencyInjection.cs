using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Behaviors;
using DocuMind.Application.Common.Options;
using DocuMind.Application.Common.Security;
using DocuMind.Application.Features.Chat;
using DocuMind.Application.Features.Search;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DocuMind.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddScoped<ISender, Sender>();
        RegisterHandlers(services);

        // Outermost first: log -> persist audit trail -> authorize -> validate -> handler.
        // Audit wraps authorization so denied attempts are recorded too.
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AuthorizationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        services.AddScoped<AuditTrail>();
        services.AddScoped<IAuditTrail>(sp => sp.GetRequiredService<AuditTrail>());
        services.AddScoped<IAccessControl, AccessControl>();
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        services.AddOptions<RagOptions>().BindConfiguration(RagOptions.SectionName);
        services.AddOptions<DocumentOptions>().BindConfiguration(DocumentOptions.SectionName);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<DocumentFileCleanup>();
        services.AddScoped<IHybridRetriever, HybridRetriever>();
        services.AddScoped<RagChatService>();

        return services;
    }

    /// <summary>Registers every concrete IRequestHandler&lt;,&gt; in this assembly (commands and queries alike).</summary>
    private static void RegisterHandlers(IServiceCollection services)
    {
        var handlerInterface = typeof(IRequestHandler<,>);

        var registrations =
            from type in typeof(DependencyInjection).Assembly.GetTypes()
            where type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            from iface in type.GetInterfaces()
            where iface.IsGenericType && iface.GetGenericTypeDefinition() == handlerInterface
            select (iface, type);

        foreach (var (iface, type) in registrations)
            services.AddScoped(iface, type);
    }
}
