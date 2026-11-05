# Moderato.Effects Database 連携の要件草案

2026-11-05 の設計検討。検討 Issue: [#409](https://github.com/SingUp009/Moderato.Effects/issues/409)。製品実装、専用パッケージの採用、名前空間、公開 API は決定しない。既存の Effect・Resource と、ADO.NET・EF Core・Dapper の通常の使い方を比較する。preview.4 の公開準備とは別の設計作業とする。

## 1. 専用パッケージが必要か

既存の `Resource.Make`・`Use`・`Scope` で、取得、解放の逆順、子と解放の完了待ち、失敗の集約を表せる。寿命管理だけを理由に別の実行器やパッケージを追加する必要性は、現時点では確認できていない。これは既存公開 API と [D9・D18](../design.md) からの設計上の推論であり、全プロバイダーでの実行確認ではない。

| 候補 | 提供するもの | 判断が必要なこと |
|---|---|---|
| A. ガイドとアプリ側の小さなヘルパー | 既存の Resource と標準 DB API を組み合わせる例 | 所有権・commit の配置・不明な結果をアプリが決める。まず比較の基準にする候補 |
| B. 薄い ADO.NET 連携パッケージ | 複数アプリで共通すると確認できた取得・解放の記法 | A では残る具体的な重複と、その共通化が約束する範囲が未確認 |
| C. EF Core / Dapper ごとの連携 | 各ライブラリ固有の利用例や必要な防御 | 既存の DI、factory、execution strategy と重複しない責務が未確認 |

A を先に評価する案を置く。B・C の必要性は、利用例で残る問題と検証結果から判断する。`CommitOutcomeUnknown` のようなアプリ固有の E、例外、操作記録で不明な状態を表すことは既存 API でも可能であり、共通の新しいエラー型や第5の Exit を直ちに追加する理由にはしない。

## 2. 一次資料で確認した契約

資料は 2026-11-05 に確認した。.NET 11 RC3 の参照 XML、公開概念文書、固定コミットの実装はそれぞれ別の証拠として扱う。GA や任意のプロバイダーの保証へ広げない。

| 層 | 確認したこと | 設計への制約 |
|---|---|---|
| ADO.NET | `DbCommand.ExecuteNonQueryAsync` の token はプロバイダーが尊重しない場合がある。完了前にその command の他の操作を呼ばない契約がある | 実行完了を待つ。command の制約を connection 全般の禁止事項へ読み替えない。[API 文書][ado-command] |
| ADO.NET | 基底の `CommitAsync` / `RollbackAsync` は同期メソッドへ委譲する既定実装を持つ。基底の `Dispose(bool)` は空の仮想メソッド | Async という名前だけで非同期 I/O を約束せず、基底の Dispose に自動 rollback を期待しない。[固定ソース][runtime-transaction] |
| プロバイダー | Npgsql は pending な transaction の Dispose / DisposeAsync で rollback すると文書化している | この約束を ADO.NET の全実装へ広げない。[Npgsql 文書][npgsql-transaction] |
| EF Core | provider が transaction をサポートする場合、既定の単一 SaveChanges は transaction を利用する。手動 transaction と retrying execution strategy は調整が必要 | 単一 SaveChanges のためだけに別の transaction を必須にしない。明示 transaction 内の SaveChanges が外側を commit するとはしない。[Transactions][ef-transactions] |
| EF Core | 同じ DbContext 上の並行操作はサポートされない。factory で作った context の解放はアプリが所有する | 同じ context を共有する Effect.All / Fork を安全だと案内しない。DI 所有と factory で取得した値を分ける。[DbContext lifetime][ef-context] |
| EF Core | SaveChanges 等の token は provider へ渡され、実効性は provider による | execution strategy の再試行用 token の注記を、全 EF 操作の動作として一般化しない。[Async][ef-async] |
| Dapper | CommandDefinition が transaction と cancellationToken を保持する。対象の async 実装は token を DbCommand へ渡し、最初に閉じていた connection を開いて最後に閉じる | 接続状態の管理と Dispose の所有権を分け、既存 transaction を明示的に渡す。[CommandDefinition][dapper-command]・[async 実装][dapper-async] |

.NET 11 RC3 の BCL 既定実装は、DB に接続しない stub provider の対照でも確認した。`CommitAsync` は呼出しスレッド上で同期 Commit を呼び、開始前のキャンセルでは呼ばないが、同期 Commit の途中で token がキャンセルされても正常完了し得た。既定 `DisposeAsync` は Dispose を呼び、独立して Rollback を呼ばなかった。これは基底実装の観測であり、実 DB や独自 override を持つ provider の検証ではない。

Npgsql の pooling では Dispose / Close が物理接続の切断ではなく pool への返却となる。解放を物理切断と同一視しない。[Npgsql basic usage][npgsql-basic]

## 3. 所有権

| 入力・値 | 所有者 | この処理で行うこと |
|---|---|---|
| DI から借りた DbDataSource / factory | DI / アプリ | 借りて接続を取得する。transaction ごとに factory 自体を Dispose しない |
| この処理が作った connection | アプリの Resource | 値を取得した直後に解放を登録し、その後で OpenAsync。open 失敗でも取得済み object を解放する |
| 呼び出し元から借りた connection / transaction | 呼び出し元 | 自動で閉じる・Dispose・commit・rollback しない。権限を別の契約として渡す |
| この処理が開始した transaction | transaction の外側を所有する処理 | commit の位置を決め、未確定時の rollback 方針と Dispose を管理する |
| DI 所有の DbContext | DI のスコープ | Effect は借用する。内部ヘルパーが独自に Dispose しない |
| factory で作った DbContext | 作成した処理 | Resource 等で解放を管理する |
| command / reader / 非 buffered な結果 | 取得した処理 | 利用完了まで接続・transaction を維持する。reader の寿命を越える列挙を外へ返さない |

取得関数の途中で object を作ってから OpenAsync が失敗すると、Resource がまだ値を受け取っていない場合がある。接続 object の取得・解放登録と open を分ける案を、以下の例で使う。接続文字列、SQL の値、認証情報を診断のために保持・公開する仕組みは追加しない。

## 4. commit の配置と失敗の意味

### 本体で明示する案

内側の業務 Effect と、その Scope・子・reader 等の解放を待ち、成功した場合に `AndThen` で commit を呼ぶ。rollback と Dispose は解放側へ置く。この案では commit に実行 token を渡すことや、アプリが確認した想定内の commit エラーを E に写すことを選べる。例では例外を自動分類せず、通常の Core 規則へ渡す。

業務 body が返す値は materialize 済みの値とする。body 内で transaction を独自に完了させず、借りた資源を外へ逃がさず、未完了の DB 操作を切り離さない。子の Err を成功判定へ含めるなら Join / All 等で観測する。Scope が終了を待つことだけでは、未 Join の子の Err が親の Err になるという保証にはならない。

### 解放で Succeeded を見て commit する案との比較

`Resource.Make` の解放型は `Effect<Unit, Never, R>` で、想定内の commit 失敗を Err として返せない。また D9 の ExitCase は全解放前に固定されるため、先行した別の解放が失敗していても後の callback は元の Succeeded を見る。解放には実行 token を渡さないので、commit の token 方針も変わる。[Resource.Make][core-resource]・[Releases][core-releases]

このため、release 内 commit を標準形として採用する根拠にはしない。本体で明示する案を比較の基準候補とし、既存の D9 を DB のために変更しない。rollback と Dispose は別の解放操作として扱い、両方を試みて Core の主失敗・付属情報へ渡す。raw finally 内で既に上書きされた例外を Core が復元する保証はない。

### Exit と ExitCase のケースを取り違えない

| 型 | 成功 | 想定内失敗 | その他 |
|---|---|---|---|
| `Exit<T, E>` | `Ok<T>` | `Err<E>` | `Die` / `Interrupted` |
| `ExitCase` | `Succeeded` | `Failed` | `Die` / `Interrupted` |

release が受け取るのは後者である。`exit.Value is Ok<Unit>` は object に対する判定としてコンパイルできるが、成功した release のケースは Succeeded なので一致しない。

この誤った判定で commit / rollback を選ぶアプリコードを、独立したメモリ内 SQLite の対照で確認した。INSERT の Effect は両方で `Ok(1)`、release は `Succeeded` だったが、誤った判定では rollback 後に0行、`Succeeded` 判定では commit 後に1行だった。これはそのアプリ例の未確定 INSERT が rollback された証拠であり、既存データの消失や Core の不具合を示さない。また、正しいケース判定だけでは前節の release 内 commit の制限は解消しない。

使用したのは Microsoft.Data.Sqlite 10.0.9 と、scratch 内だけで明示参照した SQLitePCLRaw.lib.e_sqlite3 3.53.3。外部 DB には接続していない。型付きのケース取得や不可能な pattern の扱いは [#411](https://github.com/SingUp009/Moderato.Effects/issues/411)（公開面）・[#408](https://github.com/SingUp009/Moderato.Effects/issues/408)（解析器）で扱う別の検討事項であり、この草案では変更を決めない。

### ローカル Exit と物理 DB の状態

Effect の終了分類と、DB がどこまで確定したかは別に記録・判断する。

| 局面 | ローカルの結果の例 | DB について分かること・分からないこと |
|---|---|---|
| DB 操作前に業務 Err | Err | この実行の DB 書込みはまだ行っていない |
| transaction 内の業務処理で Err / Die / Interrupted | 元の分類。解放失敗があれば Die 等へ集約 | rollback を試みる方針を置ける。最終 Exit だけから rollback 成功を断定しない |
| commit を呼び、正常に応答を得た | 後処理も成功すれば Ok | provider の commit 成功として扱う。後の Effect の失敗で元へ戻るものではない |
| commit 中の切断・キャンセル等 | Die / Interrupted、またはアプリの明示的な Err | commit 済みかどうか不明な場合がある。例外だけから未 commit と決めず、原因・provider・業務記録で判断する |
| commit 成功後に Dispose が失敗 | Die になり得る | 既に成立した commit は取り消されない |
| rollback / Dispose が失敗 | Die と保持された失敗情報 | 解放失敗を通知する。DB の最終状態を自動で確定したことにはしない |

EF Core も commit 中の通信断では結果が不明になり、retry が重複書込み等を招き得ることと、確認用の verifySucceeded 等を説明している。DB の原子性と、クライアントが確定結果を知れるかという問題を分ける。[Connection resiliency][ef-resiliency]

## 5. キャンセル・再実行・並行性

- 通常処理へ実行 token を渡し、コマンドの Task / ValueTask と子・解放の終了を待つ。WaitAsync 等で待機だけを切り上げて、未完了の DB 操作を放置する形は基準にしない。
- Core は解放へ実行 token を渡さない。これは rollback の完了、時間制限内の終了、provider 内部の timeout や失敗がないことを意味しない。中断扱いで終わった解放は Core の規則で Die になる。
- Interrupted はアプリ実行の分類であり、DB サーバー側の停止・rollback・外部副作用の取消を証明しない。メール、HTTP、別接続での commit 等は対象 transaction の外にある。
- Effect の再実行や Retry は DB 処理も再実行し得る。commit 結果不明時に自動 retry を足さない。業務キー・重複排除・確認処理と、provider / EF execution strategy の責務を決める。transaction で囲んだという理由だけで再実行可能とはしない。
- 同じ DbContext の並行使用を Core の型は防がない。必要なら別 context を factory で取得するが、それぞれの transaction は自動で一つの原子的処理にはならない。暗黙のロックや専用 scheduler で隠す案は採用しない。
- Scope の入れ子を SQL の savepoint や ambient / distributed transaction へ自動変換しない。対応の有無、同一接続の同時操作、isolation、timeout は対象 provider の契約とアプリの要求を確認する。

## 6. 利用者コードの比較

次はアプリ内の説明用ヘルパーであり、新しいパッケージ API の提案・確定ではない。既存 Core の公開 API だけで寿命を組み立て、ADO.NET と Dapper は同じ所有 transaction の中へ SQL 処理を差し込む。EF Core の Save は DI 所有 context の通常の単一 SaveChanges を借用する例であり、所有 transaction ヘルパーへそのまま重ねない。

| 書き方 | 使う既存機能 | 呼び出しの形 |
|---|---|---|
| ADO.NET | Resource で connection / transaction / command の解放を管理し、実行 token を渡す | `OwnedTransaction((c, tx) => AdoWrite(c, tx, id, state))` |
| Dapper | 同じ所有権のヘルパー内で CommandDefinition に値・transaction・token を渡す | `OwnedTransaction((c, tx) => DapperWrite(c, tx, id, state))` |
| EF Core | 借用 context を AccessAsync から利用。既定 transaction / execution strategy は EF の契約を保つ | `Save().RunExitAsync(env, token)` |

E は紙面を絞るため string とした。アプリの union へ置き換えられる。すべての DbException を一律に業務 Err とする方針ではなく、既知の期待内エラーの分類と commit 不明時の語彙はアプリ側の検討事項とする。SQL と parameter 記法も対象 provider 向けの例であり、異なる SQL 方言で同じ動作を保証しない。

以下は scratch で .NET SDK 11.0.100-rc.3.26522.148、EF Core Relational 10.0.9、Dapper 2.1.89 と Core の Analyzer を参照してコンパイル確認済み（警告・エラーとも0）。この利用者コード自体の DB 接続・実行、物理状態の検証、Native AOT、性能は未確認。前節のメモリ内 SQLite 対照は、別の小さな誤用例と修正版だけの確認である。これらの依存を製品へ追加せず、製品の依存版として推奨・固定もしない。

```csharp
using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Moderato.Effects;
using Moderato.Results;

public sealed record DbEnv(DbDataSource Source);
public sealed record EfEnv(DbContext Context);

public static class ExistingApiExamples
{
    public static Resource<DbConnection, string, DbEnv> Connection()
    {
        Effect<DbConnection, string, DbEnv> create = Effect<DbEnv>.Access(env => env.Source.CreateConnection());
        return Resource.Make(create, (connection, _) => Effect<DbEnv>.Async<Unit>(async _ =>
        {
            await connection.DisposeAsync();
            return Unit.Value;
        }));
    }

    public static Resource<DbTransaction, string, DbEnv> Transaction(DbConnection connection)
    {
        Effect<DbTransaction, string, DbEnv> begin = Effect<DbEnv>.Async(connection.BeginTransactionAsync);
        return Resource.Make(begin, (transaction, exit) => Scope<Never, DbEnv>.Gen(async cleanup =>
        {
            cleanup.Defer(_ => Effect<DbEnv>.Async<Unit>(async _ =>
            {
                await transaction.DisposeAsync();
                return Unit.Value;
            }));
            if (exit.Value is not Succeeded)
            {
                await cleanup.Run(Effect<DbEnv>.Async<Unit>(async token =>
                {
                    await transaction.RollbackAsync(token);
                    return Unit.Value;
                }));
            }
            return Unit.Value;
        }));
    }

    public static Effect<T, string, DbEnv> OwnedTransaction<T>(Func<DbConnection, DbTransaction, Effect<T, string, DbEnv>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Connection().Use(connection =>
        {
            Effect<Unit, string, DbEnv> open = Effect<DbEnv>.Async<Unit>(async token =>
            {
                await connection.OpenAsync(token);
                return Unit.Value;
            });
            return open.AndThen(_ => Transaction(connection).Use(transaction =>
                body(connection, transaction).AndThen(value =>
                {
                    Effect<Unit, string, DbEnv> commit = Effect<DbEnv>.Async<Unit>(async token =>
                    {
                        await transaction.CommitAsync(token);
                        return Unit.Value;
                    });
                    return commit.Map(_ => value);
                })));
        });
    }

    public static Effect<int, string, DbEnv> AdoWrite(DbConnection connection, DbTransaction transaction, int id, string state)
    {
        Effect<DbCommand, string, DbEnv> create = Effect<DbEnv>.Sync(connection.CreateCommand);
        return Resource.Make(create, (command, _) => Effect<DbEnv>.Async<Unit>(async _ =>
        {
            await command.DisposeAsync();
            return Unit.Value;
        })).Use(command => Effect<DbEnv>.Async<int>(async token =>
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE orders SET state = @State WHERE id = @Id";
            var idParameter = command.CreateParameter();
            idParameter.ParameterName = "Id";
            idParameter.Value = id;
            command.Parameters.Add(idParameter);
            var stateParameter = command.CreateParameter();
            stateParameter.ParameterName = "State";
            stateParameter.Value = state;
            command.Parameters.Add(stateParameter);
            return await command.ExecuteNonQueryAsync(token);
        }));
    }

    public static Effect<int, string, DbEnv> DapperWrite(DbConnection connection, DbTransaction transaction, int id, string state)
        => Effect<DbEnv>.Async<int>(async token => await connection.ExecuteAsync(
            new CommandDefinition("UPDATE orders SET state = @State WHERE id = @Id", new { Id = id, State = state },
                transaction: transaction, cancellationToken: token)));

    public static Effect<int, string, EfEnv> Save()
        => Effect<EfEnv>.AccessAsync<int>(async (env, token) => await env.Context.SaveChangesAsync(token));
}
```

このヘルパーの保証範囲は、自分で取得した接続・transaction と、その完了を待つ body に限る。commit 中の不明な結果に対する rollback は試行であり、その成功や二重 rollback の可否は provider による。まだ結果を知らない commit、既に完了した transaction、借用資源をどう扱うかは、共通 API を決める前に検証する。

## 7. 未決事項

1. ガイドだけで足りるか。複数アプリで残る記述の重複は何か。薄い helper が必要なら ADO.NET 共通と ORM 固有の境界をどこへ置くか。
2. commit の権限・場所と token 方針。commit 失敗を Err とする根拠、結果不明の語彙、操作 ID / 記録 / 確認を誰が所有するか。
3. rollback 試行と provider の Dispose 契約をどう組み合わせるか。通信断・既完了・二重呼出しでの結果、rollback と Dispose の両失敗をどう検証するか。
4. 借用資源と所有資源の入力をどう区別するか。アプリ helper の前提を文書化するだけで十分か。
5. DbContext / connection / transaction の並行制約をどこまで防御するか。文書・既存 factory・解析器等の実効性と必要性を比較し、追加機能を先に決めない。
6. retry / idempotency と EF execution strategy、savepoint、ambient / distributed transaction、非 buffered 結果の範囲。未対応・provider 依存を曖昧にしない。

## 8. 今後の受け入れ確認候補

- 所有 / 借用の両方で、取得失敗、open 失敗、begin 失敗、取得直後のキャンセルを試し、取得済み資源の解放と借用資源の非破棄を確認する。
- Ok・Err・Die・Interrupted と、実 DB 内の行・transaction 状態を別々に観測する。実行 token を provider が尊重する場合としない場合を分ける。
- 内側の reader・子・解放の失敗時に commit へ進まないことを確認する。未 Join の子の Err を無視する Core の通常動作も混同しない。
- commit の前・最中・成功応答後の通信断やキャンセル、rollback / Dispose の個別失敗と同時失敗を試す。元の分類、Relation、順序、省略件数を Core の規則で保持する。
- EF の単一 SaveChanges、明示 transaction、execution strategy、同一 context の並行誤用を別々に確認する。Dapper は開 / 閉接続、transaction / token 指定、buffered / 非 buffered を分ける。
- 対象 provider と版、SQL、isolation、環境、実 DB を明示する。scratch のコンパイル成功を DB 契約、provider 間の互換性、GA / AOT / 性能の検証完了に読み替えない。

検討 Issue にこれらの未決事項と受け入れ候補を残す。現段階では新しい製品実装・公開 API を確定しない。

## 9. 出典と検証範囲

- Core は main 4eb95b7 の [Resource.Make][core-resource]、[Releases][core-releases]、D9・D18を確認した。資料の主張は、他の担当者の読解とも独立に照合した。
- ADO.NET は SDK RC3 の `System.Data.Common.xml` を参照し、公開 API 文書は確認時に到達できた net10.0 の説明を併記した。runtime の補助実装参照は release/11.0 の固定コミット 8f0cccb。これは GA の検証記録ではない。
- Dapper の実装読解は固定コミット eb47546。コンパイルで使用した配布物 2.1.89 と実装スナップショットを同じものとして断定しない。
- EF Core の API 参照、概念資料、provider の文書を区別した。接続先 DB や product package に変更は加えていない。

[ado-command]: https://learn.microsoft.com/en-us/dotnet/api/system.data.common.dbcommand.executenonqueryasync?view=net-10.0
[runtime-transaction]: https://github.com/dotnet/runtime/blob/8f0cccb3acace572b8faf40470d58c9d699979e9/src/libraries/System.Data.Common/src/System/Data/Common/DbTransaction.cs
[npgsql-transaction]: https://www.npgsql.org/doc/api/Npgsql.NpgsqlTransaction.html
[npgsql-basic]: https://www.npgsql.org/doc/basic-usage.html
[ef-transactions]: https://learn.microsoft.com/en-us/ef/core/saving/transactions
[ef-context]: https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/
[ef-async]: https://learn.microsoft.com/en-us/ef/core/miscellaneous/async
[ef-resiliency]: https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency#transaction-commit-failure-and-the-idempotency-issue
[dapper-command]: https://github.com/DapperLib/Dapper/blob/eb47546a408bbf6c178bda4f04dc205bc51ffbfe/Dapper/CommandDefinition.cs
[dapper-async]: https://github.com/DapperLib/Dapper/blob/eb47546a408bbf6c178bda4f04dc205bc51ffbfe/Dapper/SqlMapper.Async.cs
[core-resource]: https://github.com/SingUp009/Moderato.Effects/blob/4eb95b771577d969a8f5eace09fe06770c6cb550/src/Moderato.Effects/Resource.cs
[core-releases]: https://github.com/SingUp009/Moderato.Effects/blob/4eb95b771577d969a8f5eace09fe06770c6cb550/src/Moderato.Effects/Internal/Releases.cs
