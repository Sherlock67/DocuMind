using System.Security.Cryptography;
using System.Text;
using Anthropic;
using DocuMind.Application.Abstractions.AI;
using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Ingestion;
using DocuMind.Application.Abstractions.Search;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Infrastructure.AI;
using DocuMind.Infrastructure.Identity;
using DocuMind.Infrastructure.Ingestion;
using DocuMind.Infrastructure.Persistence;
using DocuMind.Infrastructure.Search;
using DocuMind.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DocuMind.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        AddPersistence(services, configuration);
        AddIdentity(services, configuration);
        AddIngestion(services, configuration);
        AddAi(services, configuration);
        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var writeConnection = configuration.GetConnectionString("SqlServer") is { Length: > 0 } w
            ? w
            : throw new InvalidOperationException("ConnectionStrings:SqlServer is not configured.");
        var readConnection = configuration.GetConnectionString("SqlServerRead") is { Length: > 0 } r ? r : writeConnection;

        services.AddSingleton(new SqlConnections(writeConnection, readConnection));

        services.AddDbContext<AppDbContext>(o => o.UseDocuMindSqlServer(writeConnection));

        services.AddDbContext<ReadDbContext>(o => o
            .UseDocuMindSqlServer(readConnection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IReadDbContext>(sp => sp.GetRequiredService<ReadDbContext>());
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IChatSessionRepository, ChatSessionRepository>();
        services.AddScoped<IChunkSearchService, SqlChunkSearchService>();
        services.AddSingleton<IKeywordTokenizer, KeywordTokenizer>();
    }

    private static void AddIdentity(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .PostConfigure<IHostEnvironment>((o, env) =>
            {
                // Development convenience only: a random key means tokens die with the process.
                if (string.IsNullOrWhiteSpace(o.SigningKey) && env.IsDevelopment())
                    o.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            })
            .Validate(o => Encoding.UTF8.GetByteCount(o.SigningKey) >= 32,
                "Auth:SigningKey must be at least 32 bytes. Set it with user-secrets or the Auth__SigningKey environment variable.")
            .Validate(o => o.AccessTokenMinutes is > 0 and <= 60, "Auth:AccessTokenMinutes must be between 1 and 60.")
            .ValidateOnStart();

        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<AppDbContext>();

        services.AddMemoryCache();
        services.AddSingleton<JwtTokenIssuer>();
        services.AddSingleton<SessionValidator>();
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IPermissionStore, PermissionStore>();
        services.AddSingleton<IAuditLogWriter, SqlServerAuditLogWriter>();
    }

    private static void AddIngestion(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName));
        services.AddOptions<IngestionOptions>().Bind(configuration.GetSection(IngestionOptions.SectionName));

        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IIngestionQueue, ChannelIngestionQueue>();
        services.AddSingleton<ITextChunker, RecursiveTextChunker>();
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxTextExtractor>();
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddHostedService<IngestionWorker>();
    }

    private static void AddAi(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AnthropicOptions>().Bind(configuration.GetSection(AnthropicOptions.SectionName));
        services.AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .Validate(o => o.Dimensions > 0, "Embeddings:Dimensions must be positive.");

        services.AddSingleton(sp =>
        {
            var apiKey = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value.ApiKey;
            return string.IsNullOrWhiteSpace(apiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = apiKey };
        });
        services.AddScoped<IChatCompletionService, AnthropicChatCompletionService>();

        var embedding = configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>() ?? new EmbeddingOptions();
        if (embedding.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<ITextEmbeddingService, OllamaEmbeddingService>(http =>
                {
                    http.BaseAddress = new Uri(NullIfEmpty(embedding.BaseUrl) ?? "http://localhost:11434/");
                    http.Timeout = TimeSpan.FromMinutes(5); // local models on CPU can be slow
                });
        }
        else
        {
            services.AddHttpClient<ITextEmbeddingService, VoyageEmbeddingService>(http =>
                {
                    http.BaseAddress = new Uri(NullIfEmpty(embedding.BaseUrl) ?? "https://api.voyageai.com/");
                    var key = NullIfEmpty(embedding.ApiKey) ?? Environment.GetEnvironmentVariable("VOYAGE_API_KEY");
                    if (!string.IsNullOrWhiteSpace(key))
                        http.DefaultRequestHeaders.Authorization = new("Bearer", key);
                })
                .AddStandardResilienceHandler(); // retries 429/5xx with backoff + circuit breaker
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
