using Moderato.Effects;

// ---- Errors ----
public record NotFound(int Id);
public record Forbidden(string Reason);

[Error]
public partial union AppError(NotFound, Forbidden, TimeoutError);

[Error, RestOf<AppError>]
public partial union AccessError(Forbidden, TimeoutError);

// ---- Services and the environment ----
public record User(int Id, string Name);
public record Profile(string Name);

public interface IUserRepository
{
    ValueTask<User?> FindAsync(int id, CancellationToken cancellationToken);
}

public interface IPermissionService
{
    ValueTask<bool> CanViewAsync(User user, CancellationToken cancellationToken);
}

public interface IAuditLogger
{
    void Write(string message);
}

public interface IProfileDeps : IHas<IUserRepository>, IHas<IPermissionService>, IHas<TimeProvider>, IHas<IAuditLogger>;

[Environment]
public sealed partial record AppEnv(IUserRepository Users, IPermissionService Permissions, TimeProvider Clock, IAuditLogger Audit) : IProfileDeps;

public sealed class InMemoryUsers : IUserRepository
{
    public ValueTask<User?> FindAsync(int id, CancellationToken cancellationToken) => new(id is 1 or 3 ? new User(id, $"user{id}") : null);
}

public sealed class Permissions : IPermissionService
{
    public ValueTask<bool> CanViewAsync(User user, CancellationToken cancellationToken) => new(user.Id == 1);
}

public sealed class ConsoleAudit : IAuditLogger
{
    public void Write(string message) => Console.WriteLine($"  audit: {message}");
}
