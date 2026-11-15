import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const project = join(root, 'tests/Moderato.Effects.Documentation');
const generated = join(project, 'obj/guide-examples');

function snippets(page, count) {
  const markdown = readFileSync(join(root, 'document/src/content/docs', page), 'utf8').replaceAll('\r\n', '\n');
  const blocks = [...markdown.matchAll(/^```csharp\n([\s\S]*?)^```[ \t]*$/gm)].map(match => match[1]);
  assert.equal(blocks.length, count, `Update the compilation context for ${page}`);
  return blocks;
}

const [program, storeEffects, sharedScope] = snippets('Agents.md', 3);
const [openConnection, usingResource, compose, transaction, defer, environment] = snippets('Resources.md', 6);
const [group, all, childResource, race, fork, parallelScope, inference] = snippets('Concurrency.md', 7);
const [sleep, timeout, retryBudget, fakeClock] = snippets('Time.md', 4);
const [outcome, suppressed, unobserved, diagnosticOptions, exitCase] = snippets('Failures.md', 5);
const [appError, matching, nestedErrors, overlap, removeCase, nullableCase] = snippets('Error-unions.md', 6);
const [findUser, appEnvironment, exposeClock, groupedDependencies, dependencyInjection] = snippets('Environments.md', 5);
const [gen, genForms, fail, deferOnFailure, catchFilter, adapter] = snippets('Gen-blocks.md', 6);
const [attempt, linq, binder] = snippets('Operators.md', 3);
const [quickstart, runQuickstart, composeQuickstart, tryQuickstart] = snippets('Getting-started.md', 4);
const [retry, readRetry, idempotentRetry, policy, repeat, manualRetry] = snippets('Retry-and-repeat.md', 6);
const [lazyExecution, resultHandling] = snippets('From-Results-to-Effects.md', 2);
mkdirSync(generated, { recursive: true });
writeFileSync(join(generated, 'Program.cs'), program);
writeFileSync(join(generated, 'StoreEffects.cs'), storeEffects);
writeFileSync(join(generated, 'SharedScope.cs'), `
using Moderato.Effects;
using System.IO;

internal static class SharedScope
{
    public static Effect<int, StoreError, EmptyEnvironment> Create(
        Resource<Stream, StoreError, EmptyEnvironment> first,
        Func<Stream, Resource<Stream, StoreError, EmptyEnvironment>> openSecond,
        Func<Stream, Stream, Effect<int, StoreError, EmptyEnvironment>> work)
    {
${sharedScope}
        return operation;
    }
}
`);
writeFileSync(join(generated, 'Resources.cs'), `
using Moderato.Effects;
using Moderato.Results;

internal static class Resources
{
${openConnection}
${transaction}

    public static Effect<int, DbError, R> Read<R>() where R : IHas<IDbFactory> =>
        Scope<DbError, R>.Gen(async scope =>
        {
${usingResource}
            _ = connection;
            return await scope.Run(rows);
        });

    public static Resource<(DbConnection, Transaction), DbError, R> Compose<R>() where R : IHas<IDbFactory>
    {
${compose}
        return both;
    }

    public static Effect<Unit, DbError, R> RegisterRelease<R>(DbConnection connection) where R : IHas<IDbFactory> =>
        Scope<DbError, R>.Gen(async scope =>
        {
${defer}
            return Unit.Value;
        });

    public static async Task RunWithEnvironment()
    {
${environment}
    }

    private static Effect<DbConnection, DbError, R> Connect<R>() => throw new NotImplementedException();
    private static Effect<Transaction, DbError, R> Begin<R>(DbConnection connection) => throw new NotImplementedException();
    private static Effect<int, DbError, R> Query<R>(DbConnection connection) => throw new NotImplementedException();
    private static Effect<Unit, Never, R> Close<R>(DbConnection connection) => throw new NotImplementedException();
    private static Effect<Unit, Never, R> Commit<R>(Transaction tx) => throw new NotImplementedException();
    private static Effect<Unit, Never, R> Rollback<R>(Transaction tx) => throw new NotImplementedException();
    private static Resource<AppEnv, AppError, EmptyEnvironment> OpenEnvironment() => throw new NotImplementedException();
    private static Effect<Report, AppError, R> BuildReport<R>() => throw new NotImplementedException();

    internal interface IDbFactory;
    internal sealed record DbConnection;
    internal sealed record Transaction;
    internal sealed record DbError;
    private sealed record AppEnv;
    private sealed record AppError;
    private sealed record Report;
}
`);
writeFileSync(join(generated, 'Concurrency.cs'), `
using Moderato.Effects;

internal static class ConcurrencyExamples
{
    public static void Group(Effect<int, string, EmptyEnvironment>[] effects)
    {
${group}
    }

    public static void All<R>(int id, int[] ids)
    {
${all}
    }

    public static Effect<(User, Settings), AppError, R> WithChildResource<R>(
        int id, Resource<SettingsStore, AppError, R> settingsStore)
    {
${childResource}
        return profileData;
    }

    public static Effect<int, AppError, R> Race<R>(int key)
    {
${race}
        return fastest;
    }

    public static Effect<int, AppError, R> Fork<R>() => Scope<AppError, R>.Gen(async scope =>
    {
${fork}
        return value;
    });

    public static Effect<(User, Settings), AppError, R> ParallelScope<R>(int id) =>
        Scope<AppError, R>.Gen(async scope =>
        {
${parallelScope}
            return (user, settings);
        });

    public static Effect<int[], string, EmptyEnvironment> Inference()
    {
${inference}
        return grouped;
    }

    private static Effect<User, AppError, R> FindUser<R>(int id) => throw new NotImplementedException();
    private static Effect<Settings, AppError, R> LoadSettings<R>(int id) => throw new NotImplementedException();
    private static Effect<Settings, AppError, R> ReadSettings<R>(SettingsStore store, int id) => throw new NotImplementedException();
    private static Effect<int, AppError, R> FromPrimary<R>(int key) => throw new NotImplementedException();
    private static Effect<int, AppError, R> FromReplica<R>(int key) => throw new NotImplementedException();
    private static Effect<int, AppError, R> Compute<R>() => throw new NotImplementedException();

    internal sealed record AppError;
    internal sealed record User;
    internal sealed record Settings;
    internal sealed record SettingsStore;
}
`);

const [timeoutDeclaration, timeoutUse] = timeout.trimEnd().split('\n\n');
const [clockDeclaration, clockUse] = fakeClock.trimEnd().split('\n\n');
assert.ok(timeoutUse && clockUse, 'Expected declarations and usage in the time examples');
writeFileSync(join(generated, 'Time.cs'), `
using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;

namespace TimeGuide;

${timeoutDeclaration}
${clockDeclaration}

public sealed record Profile(string Name);
public sealed record NotFound;
public sealed record Forbidden;

internal static class TimeExamples
{
    public static Effect<Unit, Never, R> Wait<R>() where R : IHas<TimeProvider> =>
        Scope<Never, R>.Gen(async scope =>
        {
${sleep}
            return Unit.Value;
        });

    public static Effect<Profile, AppError, R> Limit<R>(int id) where R : IHas<TimeProvider>
    {
${timeoutUse}
        return limited;
    }

    public static void Budget<R>(Effect<Profile, AppError, R> effect, Schedule schedule) where R : IHas<TimeProvider>
    {
${retryBudget}
    }

    public static async Task AdvanceClock()
    {
${clockUse}
    }

    private static Effect<Profile, AppError, R> GetUserProfile<R>(int id) => throw new NotImplementedException();
}
`);
writeFileSync(join(generated, 'Failures.cs'), `
using Moderato.Effects;
using Moderato.Results;
using TimeGuide;

internal static class FailureExamples
{
    public static async Task<string> Describe(int id, AppEnv env, CancellationToken ct)
    {
${outcome}
        return message;
    }

    public static void PrintSuppressed(Exit<Profile, AppError> exit)
    {
${suppressed}
    }

    public static void Configure(Logger logger)
    {
${unobserved}
    }

    public static async Task<Exit<Profile, AppError>> Observe<R>(
        Effect<Profile, AppError, R> effect, R environment, CancellationToken cancellationToken,
        Action<ExitCase> reporter)
    {
${diagnosticOptions}
        return exit;
    }

    public static void Inspect(ExitCase failure)
    {
${exitCase}
    }

    internal abstract class Logger
    {
        public abstract void LogError(string template, object? argument);
    }

    internal sealed record AppEnv;
    private static Effect<Profile, AppError, R> GetUserProfile<R>(int id) => throw new NotImplementedException();
}
`);

const [restUnion, catchUse] = removeCase.trimEnd().split('\n\n');
assert.ok(catchUse, 'Expected a declaration and usage in the rest union example');
writeFileSync(join(generated, 'ErrorUnions.cs'), `
using Moderato.Effects;
using Moderato.Results;

namespace ErrorUnionGuide;

${appError}
${nestedErrors}
${restUnion}
${nullableCase}

internal static class ErrorUnionExamples
{
    public static string Describe(Result<Profile, AppError> result)
    {
${matching}
        return message;
    }

    public static Effect<Profile, AccessError, R> Recover<R>(int id)
    {
${catchUse}
        return orGuest;
    }

    internal sealed record Profile(string Name)
    {
        public static Profile Guest(int id) => new($"guest {id}");
    }

    private static Effect<Profile, AppError, R> GetUserProfile<R>(int id) => throw new NotImplementedException();
}
`);
writeFileSync(join(generated, 'DistinctErrorCases.cs'), `
using Moderato.Effects;

namespace DistinctErrorCasesGuide;

${overlap}
`);

assert.ok(groupedDependencies.includes('=> ...;'), 'Update the placeholder context for grouped environment dependencies');
assert.ok(dependencyInjection.includes('switch { ... }'), 'Update the placeholder context for the ASP.NET endpoint');
writeFileSync(join(generated, 'Environments.cs'), `
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;

namespace EnvironmentGuide;

${appEnvironment}
${exposeClock}

public interface IUserRepository
{
    ValueTask<User?> FindAsync(int id, CancellationToken cancellationToken);
}

public sealed record User(int Id);
public sealed record NotFound(int Id);
public sealed record AppDbContext;

internal static class EnvironmentExamples
{
${findUser}

    public static void Configure(WebApplicationBuilder builder, WebApplication app)
    {
${dependencyInjection.replace('switch { ... }', 'switch { Ok<User>(var user) => user, _ => null }')}
    }

    private static Effect<User, NotFound, R> GetUserProfile<R>(int id) where R : IHas<IUserRepository> => FindUser<R>(id);
}
`);
writeFileSync(join(generated, 'GroupedDependencies.cs'), `
using EnvironmentGuide;
using Microsoft.Extensions.Logging;
using Moderato.Effects;

namespace GroupedDependenciesGuide;

public sealed record AppError;

${groupedDependencies.replace('=> ...;', '=> throw new NotImplementedException();')}
`);

assert.equal(genForms.split('=> ...').length, 3, 'Update the placeholder context for the two Gen overloads');
assert.equal(catchFilter.split('{ ... }').length, 3, 'Update the placeholder context for the try/catch example');
writeFileSync(join(generated, 'Composition.cs'), `
using Moderato.Effects;
using Moderato.Results;

namespace CompositionGuide;

public sealed record NotFound(int Id);
public sealed record Forbidden(string Reason);

[Error]
public partial union AppError(NotFound, Forbidden);

internal static class CompositionExamples
{
${gen}

    public static void GenForms<R>(Effect<int, AppError, R> effect)
    {
${genForms.replaceAll('=> ...', '=> await scope.Run(effect)')}
    }

    public static Effect<User, AppError, EmptyEnvironment> Resolve(IUserRepository repository, int id) =>
        Scope<AppError, EmptyEnvironment>.Gen(async scope =>
        {
${fail}
        });

    public static Effect<Unit, AppError, R> Release<R>() => Scope<AppError, R>.Gen(async scope =>
    {
${deferOnFailure}
        return Unit.Value;
    });

    public static void CatchFilter(Action act)
    {
${catchFilter.replace('try { ... }', 'try { act(); }').replace('{ ... }', '{ throw; }')}
    }

    public static Effect<int, AppError, R> Adapter<R>(Effect<int, AppError, R> effect) =>
        Scope<AppError, R>.Gen(async scope =>
        {
${adapter}
            return value;
        });

    public static Effect<User, AppError, R> Attempt<R>(int id) where R : IHas<IUserRepository> =>
        Scope<AppError, R>.Gen(async scope =>
        {
${attempt}
            return user;
        });

    public static Effect<Profile, AppError, R> Linq<R>(int id) where R : IHas<IUserRepository>, IHas<TimeProvider>
    {
${linq}
        return profile;
    }

    public static void Bind<R>(Effect<int, AppError, R> attempt)
    {
${binder}
    }

    private static Effect<User, AppError, R> FindUser<R>(int id) where R : IHas<IUserRepository> =>
        throw new NotImplementedException();
    private static Effect<Transaction, AppError, R> BeginTransaction<R>() => throw new NotImplementedException();
    private static Effect<Unit, Never, R> Commit<R>(Transaction tx) => throw new NotImplementedException();
    private static Effect<Unit, Never, R> Rollback<R>(Transaction tx) => throw new NotImplementedException();

    internal interface IUserRepository
    {
        ValueTask<User?> FindAsync(int id, CancellationToken cancellationToken);
    }

    internal sealed record User(string Name, bool IsActive)
    {
        public static User Guest => new("Guest", true);
    }

    internal sealed record Profile(string Name, DateTimeOffset Timestamp);
    private sealed record Transaction;
}
`);

const bodyStart = quickstart.indexOf('foreach');
const greetStart = quickstart.indexOf('static Effect');
const errorSection = quickstart.indexOf('#region Error');
assert.ok(bodyStart >= 0 && bodyStart < greetStart && greetStart < errorSection, 'Expected the quickstart body, method, and error declarations');
writeFileSync(join(generated, 'Quickstart.cs'), `
using System.Text.Json;

${quickstart.slice(0, bodyStart)}
namespace QuickstartGuide;

${quickstart.slice(errorSection)}

internal static class QuickstartExamples
{
    public static async Task Run()
    {
${quickstart.slice(bodyStart, greetStart)}
    }

    public static async Task<string> Read()
    {
${runQuickstart}
        return message;
    }

    public static async Task<Result<string, QuickstartTimeoutError>> Retry(AppEnv env, int id, CancellationToken cancellationToken)
    {
${composeQuickstart}
        return result;
    }

    public static void CatchJson<R>(string json)
    {
${tryQuickstart}
    }

${quickstart.slice(greetStart, errorSection)}

    private static Effect<string, QuickstartTimeoutError, R> GetUserProfile<R>(int id) =>
        throw new NotImplementedException();

    private sealed record Dto;
    private sealed record ParseError(string Message);
}

[Environment]
internal sealed partial record AppEnv(TimeProvider Clock);

[Error]
public partial union QuickstartTimeoutError(NotFound, TimeoutError);
`);

writeFileSync(join(generated, 'Retry.cs'), `
using Moderato.Effects;
using Moderato.Results;

namespace RetryGuide;

public sealed record RateLimited(TimeSpan RetryAfter);

[Error]
public partial union AppError(TimeoutError, RateLimited);

internal static class RetryExamples
{
    public static async Task<Result<int, AppError>> Run<R>(R env, CancellationToken ct)
        where R : IHas<TimeProvider>, IHas<Random>
    {
${retry}
        return result;
    }

    public static void RetryRead<R>(int order, Schedule schedule) where R : IHas<TimeProvider>
    {
${readRetry}
    }

    public static Effect<int, AppError, R> RetryWithKey<R>(int order, Schedule schedule) where R : IHas<TimeProvider>
    {
${idempotentRetry}
        return charge;
    }

    public static void RetryWithPolicy<R>(Effect<int, AppError, R> effect) where R : IHas<TimeProvider>
    {
${policy}
    }

    public static Effect<Status, AppError, R> Repeat<R>(int jobId) where R : IHas<TimeProvider>
    {
${repeat}
        return final;
    }

    public static void RetryLoop<R>() where R : IHas<TimeProvider>
    {
${manualRetry}
    }

    private static Effect<int, AppError, R> FetchPrices<R>() => throw new NotImplementedException();
    private static Effect<int, AppError, R> FetchQuote<R>(int order) => throw new NotImplementedException();
    private static Effect<int, AppError, R> Charge<R>(int order, int quote) => throw new NotImplementedException();
    private static Effect<int, AppError, R> Charge<R>(int order, Guid idempotencyKey) => throw new NotImplementedException();
    private static Effect<Status, AppError, R> PollStatus<R>(int jobId) => throw new NotImplementedException();
    private static Effect<int, AppError, R> Fetch<R>() => throw new NotImplementedException();

    internal abstract record Status;
    private sealed record Running : Status;
}
`);

writeFileSync(join(generated, 'FromResults.cs'), `
using Moderato.Effects;
using Moderato.Results;

internal static class FromResultsExamples
{
    public static async Task LazyExecution()
    {
${lazyExecution.replace(/^using [^\n]+;\n/gm, '')}
    }

    public static async Task HandleResult()
    {
${resultHandling.replace(/^using [^\n]+;\n/gm, '')}
    }
}
`);

const build = spawnSync('dotnet', ['build', project, '-c', 'Release', ...process.argv.slice(2)], { cwd: root, stdio: 'inherit' });
if (build.error) throw build.error;
process.exitCode = build.status ?? 1;
