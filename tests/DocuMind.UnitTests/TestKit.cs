using DocuMind.Application;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Domain.Auditing;
using DocuMind.Domain.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DocuMind.UnitTests;

public sealed class FakeCurrentUser : ICurrentUser
{
    private readonly Guid? _id;

    private FakeCurrentUser(Guid? id, UserRole role, bool isSystem)
    {
        _id = id;
        Role = role;
        IsSystem = isSystem;
    }

    public static FakeCurrentUser Anonymous() => new(null, UserRole.User, isSystem: false);
    public static FakeCurrentUser System() => new(null, UserRole.User, isSystem: true);
    public static FakeCurrentUser As(UserRole role, Guid? id = null) => new(id ?? Guid.NewGuid(), role, isSystem: false);

    public bool IsAuthenticated => _id is not null;
    public bool IsSystem { get; }
    public Guid Id => _id ?? throw new InvalidOperationException("Anonymous");
    public string? Email => _id is null ? null : "user@example.test";
    public UserRole Role { get; }
    public Guid? SessionId => _id is null ? null : Guid.Empty;
    public string? IpAddress => "127.0.0.1";
    public string? UserAgent => "tests";
}

public sealed class RecordingAuditWriter : IAuditLogWriter
{
    public List<AuditEntry> Entries { get; } = [];

    public Task WriteAsync(IReadOnlyList<AuditEntry> entries, CancellationToken ct)
    {
        Entries.AddRange(entries);
        return Task.CompletedTask;
    }
}

/// <summary>Builds the real Application pipeline with test doubles for every port.</summary>
public sealed class TestHost
{
    public TestHost(ICurrentUser user)
    {
        User = user;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddApplication();
        Services.AddSingleton(user);
        Services.AddSingleton<IAuditLogWriter>(Audit);
        Services.AddSingleton(Permissions);
    }

    public ICurrentUser User { get; }
    public ServiceCollection Services { get; } = new();
    public RecordingAuditWriter Audit { get; } = new();
    public IPermissionStore Permissions { get; } = Substitute.For<IPermissionStore>();

    public ISender Sender() => Services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ISender>();

    public T Get<T>() where T : notnull => Services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<T>();
}
