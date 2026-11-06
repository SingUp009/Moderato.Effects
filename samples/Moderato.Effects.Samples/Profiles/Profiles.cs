using Moderato.Effects;
using Moderato.Results;

// ---- Effects ----
public static class Profiles<R> where R : IProfileDeps
{
    static Effect<User, NotFound, R> FindUser(int id) => Scope<NotFound, R>.Gen(async scope =>
    {
        var user = await scope.Env.Resolve<IUserRepository>().FindAsync(id, scope.CancellationToken);
        return user ?? throw scope.Fail(new NotFound(id));
    });

    static Effect<Unit, Forbidden, R> CheckAccess(User user) => Scope<Forbidden, R>.Gen(async scope =>
    {
        var allowed = await scope.Env.Resolve<IPermissionService>().CanViewAsync(user, scope.CancellationToken);
        return allowed ? Unit.Value : throw scope.Fail(new Forbidden($"user {user.Id} is private"));
    });

    static Effect<Unit, Never, R> Audit(string message) => Effect<R>.Access(env =>
    {
        env.Resolve<IAuditLogger>().Write(message);
        return Unit.Value;
    });

    public static Effect<Profile, AppError, R> GetUserProfile(int id) => Scope<AppError, R>.Gen(async scope =>
    {
        var user = await scope.Run(FindUser(id));
        await scope.Run(CheckAccess(user));
        await scope.Run(Audit($"viewed {user.Id} at {scope.Env.Resolve<TimeProvider>().GetUtcNow():HH:mm:ss}"));
        return new Profile(user.Name);
    }).Timeout(TimeSpan.FromSeconds(5));
}
