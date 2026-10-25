# Moderato.Effects 設計メモ

議論しながら更新している設計メモ。決定事項（D）と未決事項（Q）を分けて記録する。設計レビュー（F1〜F26、G1〜G9）の指摘を反映済み。反映で変わった点は、末尾の「設計レビューで変えた点」にまとめた。

## 位置づけ

- [Moderato.Results](https://github.com/SingUp009/Moderato.Results) が neverthrow の C# 版であるように、Moderato.Effects は [Effect-TS](https://effect.website/) の C# 版を目指す。
- 前提：.NET 11 / C# 15 の `union`。
- `Result<T, E>`、`Ok<T>`、`Err<E>`、`Unit` は Moderato.Results のものを使う（依存する）。
  - 依存は深い：Effect の中身と `RunAsync` の戻り値（Result）、Exit の case（Ok / Err）、`Unit`、Result を受け取る API（`scope.Get`、`TimeoutResult`、`Attempt`）、命名規約と方針。
  - Effects は「実行すると Result を返す、遅延された計算」。Results が土台の層。
  - Results のプレビュー中の破壊的変更はそのまま波及するので、特定のバージョンに固定して追従する。

### 原則

1. **安全性を優先する**。動機は速さではない。迷ったら、型で保証できる側、`finally` / `using` が確実に実行される側を選ぶ。
2. **簡単なことは簡単に書けるべき**。安全性のために型引数や定型の記述が増えるときは、推論や暗黙変換で消せないかを先に検討する。
3. **コードを読めば、何が起きるか予測できる**。生成されるコード、選ばれるオーバーロード、変換が、書いたコードから分かるようにする。

## 理論的な整理

`Effect<T, E, R>` は「effect」と「coeffect」の組み合わせとして整理する。

| | 種類 | 意味 | 合成 | C# での表現 |
|---|---|---|---|---|
| `E` | effect（出力側） | 計算が起こしうる失敗 | 和集合 `E1 ∪ E2` | `union`（名前で宣言する）＋ Source Generator |
| `R` | coeffect（入力側、flat） | 計算が文脈に要求する能力 | 要求の積 `R1 ∧ R2` | ジェネリック制約 `where R : IHas<A>, IHas<B>` |

- C# は要求の積（インターフェース制約の積）を言語として持っているので、R は素直に表せる。
- 和集合を型として持たないので、E は難しい。この非対称性が、設計の多くを説明する。
- ログや乱数などの副作用は、E ではなく R に能力として要求する（capability passing）。E は失敗だけを表す。
- CancellationToken も概念上は入力側の文脈だが、実行の途中で差し替わる特殊な文脈なので、R には入れず、ライブラリが実行時に渡す（D6）。

参考：Petricek, Orchard, Mycroft, *Coeffects: A calculus of context-dependent computation* (ICFP 2014)、ゆきくらげ「Effect の双対、Coeffect」。

## 決定事項

### D1. 型

- `Effect<T, E, R>`。型パラメータの並びは Effect-TS / Results と同じ。
- 遅延実行される不変の記述で、何度でも実行し直せる。
- 中身は `Func<R, RunContext, ValueTask<Outcome<T, E>>>`（どちらも内部の型。D18）。概念上は「R と CancellationToken を受け取り、Result を返す非同期の関数」。
- `default` は不正な状態（Results と同じ方針）。
- **Never を使った2つの暗黙変換**を持つ。どちらも型パラメータの数が同じなので、型推論も変換も通る（RC2 で確認済み）。
  - `Effect<T, Never, R>` → `Effect<T, E, R>`：失敗しない Effect は、どのエラー型の Effect としても使える。
  - `Effect<Never, E, R>` → `Effect<T, E, R>`：失敗しかしない Effect（`Effect<R>.Fail`）は、どの値の型の Effect としても使える。
  - 両側とも Never 同士のときは変換が働かず、そのまま通る。ラムダの戻り値も変換される。
- `Result<T, E>` から `Effect<T, E, R>` への暗黙変換も持つ（代入先の型が決まっているとき用。型推論が必要な場面では `Effect<R>.FromResult` を使う）。
- **型パラメータが2つの `Effect<T, E>` は作らない**。ユーザー定義の暗黙変換は連鎖せず、型推論は変換を考慮しないため、2引数から3引数への変換は代入先の型が決まっているときしか働かない（F3）。作る API は、すべて最初から3引数の Effect を返す（D16）。

### D2. 書き方は LINQ と Gen の2本立て

```csharp
// LINQ
from user in FindUser<R>(id)
from now  in Effect<R>.Access(env => env.Resolve<TimeProvider>().GetUtcNow())
select new Profile(user.Name, now);

// Gen（Effect-TS の Effect.gen に相当。詳細は D11）
Scope<AppError, R>.Gen(async scope =>
{
    using var conn = Open();
    var user = await scope.Run(FindUser<R>(id));
    if (!user.IsActive) throw scope.Fail(new Forbidden("inactive"));
    return new Profile(user.Name, ...);
});
```

- ドキュメントでは **Gen を主な書き方**として案内する。LINQ は短い処理をつなぐとき向け（R3 の「クエリ構文は SQL 以外では悪い記法」という指摘のとおり、`if` やループが入ると LINQ は読みにくい）。
- **独自の async method builder は採用しない**。`await` は awaiter から呼び出し元メソッドの戻り値の型が見えないので、E と R の不一致をコンパイル時に検出できず、Analyzer 頼みになる。
- `scope` の型は `Scope<E, R>`。`scope.Run` のシグネチャで、E と R の不一致がコンパイルエラーになる。
- `Effect<T, E, R>` は `GetAwaiter` を持たない（scope を通さずに直接 `await` できない）。

### D3. E（失敗）

- union を前提にする。
- **Never** は、private コンストラクタを持つ sealed class にする（struct にすると `default` が値として存在してしまうため）。`Result<T, Never>` は Results 単体でも役に立つので、Never を Results 側に置くことも提案する（D12）。
- union の case から union へ広げる処理は、ライブラリが `IUnionCase<TSelf, TCase>` を使う汎用のコードとして持つ。Source Generator がするのは、`[Error]` を付けた union にそのインターフェースを実装することだけ（D13）。
- LINQ の `SelectMany` は、Never の Effect と組み合わせられるよう4通りにオーバーロードする（ソースと選択子のそれぞれが E か Never か）。型推論の段階でエラー型がぶつかるため、D1 の暗黙変換だけでは足りない。

### D4. Err で処理を止める仕組み

- Gen の中で Err が出たら、内部用の例外 `ScopeFailure` で巻き戻す。`finally` / `using` が確実に実行される。
- ユーザーが try/catch を書く必要はない（D12）。
- 想定内のエラー＝E、バグ＝例外（Die）、呼び出し側のキャンセル＝`OperationCanceledException`（Interrupted）。Results と同じ方針（細かい判定は D7）。

### D5. R（依存）

- 汎用の `IHas<TService>` 制約の積で表す。`IHas<T>` は**不変**（D13）。

  ```csharp
  public interface IHas<TService> { TService Service { get; } }
  where R : IHas<IUserRepository>, IHas<ILogger>
  ```

- サービスの取り出しは `env.Resolve<T>()`（`IHas<T>` への拡張メソッド。型引数が1つだけなので、型推論の制約にかからない）。
- 依存を部分的に提供して R から消す、という操作はできない。要求はすべて実行の入口（`RunAsync(env)`）で満たす。
- `Provide<R2>(Func<R2, R>)`：文脈を変換する（Reader の local）。Effect-TS の `provide` に相当する。
- `WidenEnv<R2>() where R2 : R`：弱化。struct は変性を持てないため、明示的に変換する。

**制約の伝播を軽くする推奨パターン**（F8）

末端の Effect に依存が1つ増えると、そこへ至る呼び出し元すべての `where` を書き換えることになる（Effect-TS では推論で自動的に伝わる）。そこで、次の2つをドキュメントで推奨する。

```csharp
// 1. 依存をまとめるインターフェース
public interface IUserDeps : IHas<IUserRepository>, IHas<ILogger>;

[Environment]
public sealed partial record AppEnv(IUserRepository Users, ILogger Logger, TimeProvider Clock) : IUserDeps;
// [Environment] が IHas<> を実装するので、条件を満たしていれば IUserDeps も自動で満たされる。
// 足りなければコンパイルエラーになる。Source Generator に新しい仕組みは要らない。

// 2. 関連する処理をジェネリッククラスにまとめる（呼び出しのたびに <R> を書かずに済む）
public static class Users<R> where R : IUserDeps
{
    public static Effect<User, AppError, R> Find(UserId id) => ...;
}
```

### D6. CancellationToken

- **ライブラリが実行時の文脈として渡す。型（R）には出さない**。
- 受け取り方：
  - 実行の入口：`RunAsync(env, ct)` / `RunExitAsync(env, ct)`
  - 作る API：`Effect<R>.Async(ct => ...)`、`Effect<R>.AccessAsync((env, ct) => ...)` など（D16）
  - Gen の中：`scope.CancellationToken`
- R に入れない理由：
  - トークンは実行の途中で差し替わる（`Timeout` / `Race` が内側にだけ新しいトークンを渡す）。
  - 寿命が実行ごとで、アプリ全体で使うサービスと違う。
  - 環境に値を入れると、環境を差し替えるための実装（`IWith`）や、実行ごとの環境の作り直しが必要になる。
- **「キャンセルを見る」ことを型に出す目印（`ICancellable`）は作らない**（F4）。
  - 意味を持たせるには、ct を得るすべての経路を目印で塞ぐ必要がある。static class の側では R に制約を付けられないため、`Effect<R>.Async(ct => ...)` などが抜け道になる。
  - 塞ぐには ct を受け取る API を別の static class に分ける必要があり、API が二重になる。
  - D10 の「型で検証できない自己申告の目印は作らない」という判断とも食い違う。
  - その代わり、「キャンセルに応じない処理には Timeout が効かない」ことをドキュメントで強調する（D8）。

### D7. Exit（フラットな表現から始める）

まずはフラットな union で表す。ライブラリの成長に合わせて、Effect-TS の `Cause` のような木構造へ広げる。

```csharp
public union Exit<T, E>(Ok<T>, Err<E>, Die, Interrupted);
public sealed record Die(Exception Exception, IReadOnlyList<Suppressed> Suppressed);   // 付属情報（D18）
public sealed record Interrupted;
```

- `Ok<T>` と `Err<E>` は Results のものを使う。Result と同じ感覚でパターンマッチできる。
- 実装では、Results の `Result<T, E>` と同じく、`[Union]` 属性を付けた独自の struct にする。コンパイラが生成する union は値型の case をボックス化するため。
- **内部の表現は `Outcome<T, E>`**（主となる結果と付属情報の組。D18）。当初は `ValueTask<Result<T, E>>` のままにする予定だったが、それでは複数の失敗、キャンセルの理由、発生した順番を運べなかった（G1 / G3）。公開する Exit はフラットなまま。
- 実行 API（D16）：
  - `RunAsync` は `Result<T, E>` を返す。Die と Interrupted は例外として投げる（普段はこちら）。
  - `RunExitAsync` は `Exit<T, E>` を返す。例外を投げずに、すべての結果を値で見たいとき用。
- **`RunAsync` で Die を投げるとき**は、元の例外を `ExceptionDispatchInfo` でそのまま投げ直す（F14）。例外の型を保ち、ミドルウェアの `catch` に影響させないため。付属情報は `Exception.Data` に入れる。キーは名前空間付きの1つだけを使い、追加ではなく上書きする（F24。使い回された例外や、再実行で同じ例外が投げられたときに情報がたまらないように）。すべての情報が必要なときは `RunExitAsync` を使う。
- 失敗が複数のとき、どれを主とし、残りをどう付属情報にするかは **D18** で決める。Err を全部集めたいときは `AllErrors`（`E[]`）か `AllSettled`（`Exit[]`）を使う（D10）。
- **`Interrupted` は、呼び出し側のトークンによるキャンセルだけ**を表す。判定の規則は「**呼び出し側の ct がキャンセル済み、かつ OCE**」（F12）。内部では連動したトークン（linked token）を使うので、OCE の `CancellationToken` プロパティでは判定しない。よそのトークンによるキャンセル（`HttpClient` のタイムアウトなど）と呼び出し側のキャンセルがほぼ同時に起きた場合も、呼び出し側のキャンセルを優先して Interrupted とする。
- 呼び出し側がキャンセルしたとき、API によっては OCE ではなく `ObjectDisposedException` や `IOException` を投げる。これは**仕様として Die** とする（F26）。アプリの停止時にエラーログが出ることがあると、ドキュメントに書く。

  | 中断の出どころ | 扱い |
  |---|---|
  | 呼び出し側のトークン（アプリの停止を連動したものも含む） | `Interrupted` |
  | `Timeout` | `Err(TimeoutError)` |
  | `Race` の負けた側、`All` の兄弟、終了した親の子 | 外から見えない |
  | よそのトークンの OCE（`HttpClient` のタイムアウトなど） | `Die`（`Try` で包めば Err） |
  | Fork した子の Die で親がキャンセルされたとき | 親の結果は子の `Die`（D10） |

### D8. Timeout

- 時間切れは `Err(TimeoutError)`。ライブラリが `public sealed record TimeoutError(TimeSpan After)` を用意する。
- C# には名前のない union がないため、次の形を用意する。
  1. `Timeout(ts)`：E が `TimeoutError` を case に持つ union のとき（`where E : IUnionCase<E, TimeoutError>`、D13）に使える。E が `Never` または `TimeoutError` そのもののときは、別のオーバーロードで `Effect<T, TimeoutError, R>` になる。
  2. `Timeout(ts, onTimeout: t => error)`：自分のエラーに変換する。E は変わらない。
  3. `TimeoutResult(ts)`：`Effect<Result<T, TimeoutError>, E, R>`。値の側で表す（UniTask の `TimeoutWithoutException` に相当）。
- いずれも `where R : IHas<TimeProvider>` を要求する（D14）。
- 時間切れのときは、内側のキャンセルが**終わるまで待つ**（finalizer が終わってから戻る）。
- **Timeout は協調的なタイムアウト（cooperative timeout）**。キャンセルに応じない処理では、時間の上限として働かない（F9）。ドキュメントに次の表を載せる（G4）。

  | 性質 | 保証 |
  |---|---|
  | 設定した時間が経ったらキャンセルを要求する | 保証できる |
  | 処理と解放処理が終わるのを待つ | 保証できる（終わるなら） |
  | 設定した時間内に呼び出し元へ戻る | 保証できない |
  | キャンセルに応じない処理を強制終了する | 保証できない |

  安全性を優先し、時間切れの処理を放置して先に戻る形にはしない。解放処理（`CancellationToken.None` で動く）も無期限に待つ可能性があり、これは `finally` の確実性と、終わるまでの時間とのトレードオフ。

### D9. リソース管理：`Resource<T, E, R>` ＋ Gen の Scope

Effect-TS は Scope を R に入れ、`Effect.scoped` でその要求を満たして R から消す。C# には「型パラメータ R に能力を1つ足した型」（row extension）がないため、R に入れた要求を途中で消せない。そこで、リソースの寿命を別の型で表す（cats-effect の `Resource` / ZIO 1 の `ZManaged` と同じ方式）。Effect と API が二重になるのは、必要なコストとして受け入れる。

```csharp
static Resource<DbConnection, DbError, R> OpenConn<R>() where R : IHas<IDbFactory> =>
    Resource.Make(acquire: Connect<R>(), release: (conn, exit) => Close<R>(conn));

// Resource 同士も LINQ で合成できる。解放は逆順
from conn in OpenConn<R>() from tx in BeginTx<R>(conn) select (conn, tx);

// 中身を取り出す方法は Use だけ。Use を抜けたら必ず解放される
resource.Use(x => Query<R>(x.conn, x.tx));

// Gen のブロックはそれ自体がレキシカルな Scope。解放は Gen の終わりに逆順で行う
var conn = await scope.Acquire(OpenConn<R>());
```

- 暗黙の Scope（実行時の文脈にする案）は採用しない。閉じ忘れを型で防げず、長く動く処理でリソースがたまり続けるため。
- `IHas<Scope>` として環境に値を入れる案も採用しない（CancellationToken を環境に入れる案と同じ問題があるため）。
- `IDisposable` なものは `await using` で十分。終わり方を見てロールバックしたいときや、解放処理が R を使うときに `Acquire` / `Resource` を使う。
- `Use` の中からリソースを外に返すことは型では防げない（region types がないため）。
- 意味の規則：
  - 解放は、取得し終えた順の**逆順**で行う。並列に取得したものも同じ。
  - 解放処理には `CancellationToken.None` を渡す。キャンセル済みのトークンではロールバックできないため。
  - 取得が完了したら、トークンを確認する前に、同期的に解放を登録する（取得と登録のすき間でリソースが漏れないように）。
  - 解放処理の型は `Effect<Unit, Never, R>`。解放処理の失敗は Die。1つが失敗しても残りの解放は全部実行し、例外は `AggregateException` にまとめる。
  - 解放処理は Scope 全体の終わり方を、型パラメータのない `ExitCase` で受け取る。

    ```csharp
    public union ExitCase(Succeeded, Failed, Die, Interrupted);   // Die と Interrupted は Exit<T, E> と同じ型
    public sealed record Succeeded;
    public sealed record Failed(object Error);                     // Err の中身を、型を消して持つ（ログ用）
    ```

    解放処理の中でも `exit is Die(var ex)` のようにバグの中身を取り出せる。型パラメータが必要な場面が見つかったら見直す。
    E が union のときは、`Failed` には union ではなく、union が持っている case を入れる（P3）。`exit is Failed(NotFound nf)` のように case で一致させるため。
  - **ExitCase は、利用本体が終わった時点で確定する**（G5）。解放処理の途中で別の解放処理が失敗しても、後の解放処理が受け取る ExitCase は変わらない。解放処理の失敗は別に集め、すべての解放が終わってから最終的な Exit を決める。トランザクションのように、別のリソースより先に成否を確定させたいものは、Resource を組み立てる順番で表す。
  - Gen の中では、解放処理だけを登録する `scope.Defer(exit => ...)` も使える（D11）。
- **Layer**：独自には作らない。DI があるアプリでは DI が、それ以外では `Resource<AppEnv, E, RIn>` と `Provide` が、その役割を果たす（D17）。

### D10. 並行処理

.NET は JavaScript と違い、本当に複数のスレッドで同時に動く。この違いを前提に決める。

**`All`**

```csharp
Effect.All(effects);                                         // 既定は順番に実行（Sequential）
Effect.All(effects, Concurrency.Parallel);                   // 並列に実行（上限なし）
Effect.All(effects, Concurrency.Parallel.WithMaxDegree(4));  // 上限を指定したいときだけ
```

- 引数の形は Results の `Combine` と同じく、2〜8個の Effect を取ってタプルを返す版と、シーケンスを取って配列を返す版の両方を用意する。
- `Concurrency` は struct にし、`default` が `Sequential` を意味するようにする（オプション引数の既定値に使うため）。
- 既定を順番に実行にする理由：環境 R の中のスレッドセーフでないサービス（DbContext など）での事故を防ぐ。Effect-TS も既定は順番に実行。
- Effect は遅延実行される記述なので、上限を指定したときも「n 個ずつ起動する」のが自然に書ける。
- `Parallel` のときは、子をスレッドプールで起動する（`Parallel.ForEachAsync` と同じ）。名前どおり、本当に同時に動く。`Sequential` のときはスレッドプールを使わない。単独の Effect をスレッドプールで動かしたいときは `.OnThreadPool()` を使う。
  - G7 で「I/O ならスレッドプールは不要。CPU を使う処理は `.OnThreadPool()` に任せるほうが役割が明確」という指摘があったが、今の形を維持する。子が最初の `await` の前に重い同期処理をすると兄弟が起動すらできなくなることを防ぐためで、コストは子1つにつき作業項目1つと小さい。
- fail-fast：1つが失敗したら、残りにキャンセルを送り、**それらが終わる（解放処理を含む）まで待ってから**戻る。
- 主となる失敗は、**キャンセルのきっかけになった失敗**（時間順で最初のもの）。キャンセルより前に独立して起きた失敗の中では Die を優先する。キャンセルより後に兄弟が起こした失敗は、キャンセルされたせいで起きたものなので、付属情報（`Consequence`）にするだけ。規則の全体は D18。
- fail-fast しない版：
  - `AllSettled`：`Effect<Exit<T, E>[], Never, R>`。全部の結果を Exit で受け取る。
  - `AllErrors`：`Effect<T[], E[], R>`。エラーを全部集める（入力値の検証向け。Results の `CombineAll` と同じ考え方）。
- 名前を Results の `Combine` / `CombineAll` と分けるのは、すでに計算し終えた Result をまとめる操作とは違い、並列数など実行のしかたを伴うため。

**`Race`**

- `Race`：先に**成功**したほうを採る。片方が Err になっても、もう片方の結果を待つ。両方とも Err なら、先に失敗したほうを主とし、もう一方は付属情報にする。
- 片方が **Die** になったら、その時点で Race 全体を Die で終える（もう片方はキャンセルする）。バグを黙って捨てないため。Err として扱いたいときは `Try` で包む。
- `RaceFirst`：成功・失敗に関係なく、先に**終わった**ほうを採る。
- 負けた側にはキャンセルを送り、終了を待つ。**負けた側の終了処理（解放処理を含む）が Die になったら、勝った側の結果より Die を優先する**（G3）。重大な失敗を見落とさないため。`Race` と `RaceFirst` で同じ規則にする。待たずに戻る仕組み（Effect-TS の `disconnect`）は、必要になったら明示的に選ぶ形で検討する。
- `Timeout` は `RaceFirst(effect, Sleep(t) → Err(TimeoutError))` として定義する（D8）。

**`Fork`**

- 構造化された並行性：子は親の Scope より長生きしない。
- 実行中の子（Fiber）はリソースとして扱う。

  ```csharp
  // 取得 = 子を起動する、解放 = 子にキャンセルを送り、終了を待つ
  Resource<Fiber<T, E>, Never, R> Fork<T, E, R>(this Effect<T, E, R> effect);

  var fiber = await scope.Fork(Compute<R>());   // Gen の中では、Gen のブロックが Scope になる
  var value = await scope.Run(fiber.Join());
  ```

- Join しなかった子が失敗したとき：
  - Die は親の Scope に伝わる。兄弟と親をキャンセルし、親は Die で終わる。このとき親の中で投げられる OCE は、呼び出し側のトークンによるものではないので Interrupted にはならない。**親の結果は子の Die** とする。
  - Err は Join したときにだけ現れる。子の E が親の E に含まれるとは限らず、型の上で伝えようがないため。
  - Join しないまま Scope が閉じたら、その Err は**全体共通の診断用ハンドラー**に報告する（既定ではログに出す。R3 の `UnhandledExceptionHandler` や UniTask の `UnobservedTaskException` と同じ位置づけ）。黙って消すことはしない。このハンドラーは処理の結果を左右しない、診断のためだけの出口なので、「依存は R に出す」原則の例外として許容する。
- 親から切り離して実行する仕組み（Effect-TS の `forkDaemon`）は、最初は用意しない。

**環境 R のスレッド安全性**

- ドキュメントで指針を示す：R に入れるのはスレッドセーフなもの、またはファクトリ（`IDbContextFactory` など）にする。
- 既定を順番に実行にすることでも防ぐ。
- 「並列に使っても安全」という型の目印は作らない（型では検証できず、自己申告になるため）。
- ライブラリ自身が環境のサービスを使うときは、スレッドセーフでなくても壊れないように使う（例：D15 の乱数）。

**時計**：`Sleep` と `Timeout` は `where R : IHas<TimeProvider>` を要求する（D14）。

### D11. Gen の詳細

**形**：E と R から T を推論できるよう、次の2通りを用意する（C# ではメソッドの型引数を一部だけ書けないため、`Effect<R>.Gen<E>` の形は取れない）。

```csharp
Scope<AppError, R>.Gen(async scope => { ...; return profile; });             // Scope の型から E と R を決める
Effect.Gen(async (Scope<AppError, R> scope) => { ...; return profile; });    // ラムダの引数に型を書く
```

以前の案 `Effect<AppError, R>.Gen` は採用しない（「AppError を返す Effect」と読めてしまうため）。

**`scope.Run`**：Effect を実行して値を取り出す。Err ならそこで巻き戻る。

- E と R が一致する Effect を受け取る。D1 の暗黙変換により、`Effect<T, Never, R>` / `Effect<Never, E, R>` もそのまま渡せる。
- union の case の Effect も受け取れる（`Run<T, E, E2, R>(this Scope<E, R>, Effect<T, E2, R>) where E : IUnionCase<E, E2>`）。

**失敗のさせ方**：`throw scope.Fail(error)`

- `Fail` は**それ自体が例外を投げる**（`[DoesNotReturn]`）。戻り値の型は `Exception` で、`throw` と組み合わせて書くためにある。
  - `throw` を書き忘れて `scope.Fail(e);` だけにしても、正しく失敗する。
  - `throw` を付ければ、コンパイラが「この後は実行されない」と理解するので、null の絞り込みや戻り値の到達性の検査が効く。
- 引数の型は `E`。union の case は C# の暗黙の変換でそのまま渡せる（`scope.Fail(new NotFound(id))`）。
- 先行例：Kotlin Arrow の Raise DSL（`raise(e)` は内部で例外を使って処理を止める）、Scala 3 の `boundary` / `break`（`break` は内部で例外を使う）。

**Result の取り出し**：`scope.Get(result)`

- 同期的に値を取り出す。Err ならそこで巻き戻る。`await` は不要。
- Results の `Unwrap`（Err なら例外を投げる、安全でない操作）と混同しないよう、別の名前にする。
- `scope.Run` と同じく、union の case の Result も受け取れる（`Get<T, E, E2, R>(this Scope<E, R>, Result<T, E2>) where E : IUnionCase<E, E2>`）。

**Err が出たら即座に巻き戻る。後始末は `Defer` で書く**（F1 → F20）

- scope は最初の Err を覚えておき、Gen の結果は必ずその Err になる。ラムダが普通に `return` しても、Ok にはならない。
- **その後の `Run` / `Get` / `Acquire` / `Fork` は、同じ Err で即座に巻き戻る**。`catch (Exception)` で `ScopeFailure` を握りつぶしても、その後に書いた `scope.Run(Commit<R>(tx))` などは実行されない。結果の安全性（Ok に化けない）に加えて、制御フローの安全性（失敗の後に副作用が起きない）も保つ（G6）。
- そのため、`finally` の中に書いた `scope.Run(Rollback<R>(tx))` も実行されない。**後始末は `scope.Defer` か `Acquire` の解放処理で書く**。どちらも Gen の終わりに逆順で、ExitCase を受け取って確実に実行される（D9）。

  ```csharp
  var tx = await scope.Run(BeginTx<R>());
  scope.Defer(exit => exit is Succeeded ? Commit<R>(tx) : Rollback<R>(tx));   // 必ず実行される
  ```

- Analyzer は、`finally` / `catch` の中の `scope.Run` を既定で Warning にし、`scope.Defer` を提案する（コードの見た目だけで確実に検出できる）。
- 経緯：F1 で一度「その後の `Run` は普通に実行する」に変えたが、握りつぶしたときに副作用が起きる問題（F20）があり、戻した。握りつぶしは呼び出した先のメソッドの中でも起きうるので Analyzer では検出しきれないが、`finally` の中の `scope.Run` は確実に検出でき、正しい書き方（`Defer` / `Acquire`）も常に用意されているため。
- `catch` の中で別の例外を投げ直した場合は、Die を優先し、最初の Err は付属情報にする（D7）。

**`ScopeFailure`**

- 公開し、コンストラクタは internal にする。利用者が自分で作って投げることはできないが、`catch (Exception ex) when (ex is not ScopeFailure)` のように除外できる。
- **どの scope が投げたかを持つ**（F2）。
- **入れ子の Gen の中で、外側の scope を使うことは禁止する**（`Run` だけでなく `Fail` / `Get` / `Defer` なども。F21）。使ったら `InvalidOperationException`（外から見ると Die）。許すと、Retry / Timeout / All / Attempt などのすべてのコンビネータに「外側の scope への脱出」という特別な通り道が必要になり、失敗モデルが複雑になるため。外側の Err にしたいときは、内側の Gen の結果を外側で `scope.Run` すれば足りる。
- `Effect<R>.Try` は、どの scope のものであっても `ScopeFailure` を素通しする。

**scope の使い方の制限**

- Gen が終わった後に scope を使ったら、`InvalidOperationException`（外から見ると Die）。
- 同時に2か所から使ったら、検出して例外にする。scope は1つの論理的な流れの中でだけ使える。並列に動かしたいときは `scope.Run(Effect.All(..., Concurrency.Parallel))` を使う（D10 の fail-fast の規則が効くように）。

**Effect 以外の await と例外**

- Gen のラムダは普通の async ラムダなので、どんな C# のコードも書ける。Gen の中では、Effect を作らずに普通の C# を書くのが基本。
- 普通の `await` で投げられた例外は Die。想定内のエラーとして扱いたいときは、`scope.Get(Result.Try(..., (JsonException ex) => ...))` や `scope.Run(Effect<R>.TryAsync(...))` で包む（D12）。
  - **`Result.Try` に渡すラムダの中では scope を使わない**。Results の `Try` は `ScopeFailure` を知らないので、すべての例外を捕まえる形で使うと、`ScopeFailure` が普通の Err に変わってしまう（G2）。Analyzer が検出する。
- `scope.CancellationToken` で、実行時のトークンを取り出せる（D6）。

**再実行、入れ子、ループ**

- **保証の範囲をドキュメントで明記する**（G6）：結果の安全性（Ok に化けない）は実行時に保証する。制御フローの安全性（失敗の後に副作用が起きない）は、即座に巻き戻る規則で守るが、`catch` で `ScopeFailure` を握りつぶして普通の C# のコードを続けた場合までは防げない。そこは Analyzer で補う。
- 実行するたびにラムダが呼ばれ直し、新しい scope が作られる。ただし、ラムダの外で宣言した変数（クロージャで捕まえた変数）は、実行をまたいで共有される。ドキュメントで注意を促す。
- Gen の中に Gen を書けば、それが入れ子の Scope になる。
- ループの中で外側の scope に `Acquire` / `Defer` すると、リソースが Gen の終わりまでたまり続ける（`using var` はループの1回ごとに解放されるので、そこが違う）。入れ子の Gen か `resource.Use` を使うよう案内する。

**Analyzer（補助）**：Gen の中で次のものを警告する。

- すべての例外を捕まえる `catch`（`catch (Exception)`、型を書かない `catch`）。`when (ex is not ScopeFailure)` を付けるよう提案する
- `finally` / `catch` の中の `scope.Run`（既定で Warning。`scope.Defer` を提案する。F20）
- `Result.Try` などに渡すラムダの中での scope の使用（G2）
- ラムダより長生きしそうな場所（フィールドや `Task.Run` など）への scope の持ち出し
- 入れ子の Gen の中での、外側の scope の使用（F21。実行時にも例外になる）
- ループの中での `scope.Acquire`
- `.Result` / `.Wait()` のような、処理を止めて待つ呼び出し

### D12. エラー処理の道具

**Effect や Gen を try/catch で包む必要はない**

- 想定内のエラーは `RunAsync` の戻り値（`Result<T, E>`）として値で返る。
- `RunAsync` が例外を投げるのは Die と Interrupted のときだけで、それらはアプリの一番外側（ミドルウェアなど）で扱う。
- 例外を一切投げずに受け取りたいときは `RunExitAsync` を使う。

**Gen の中は自由に書いてよい**（try/catch を含む。D11 の安全網と Analyzer はそのまま）

- 型を指定した catch（`catch (JsonException)` など）は安全。Analyzer も警告しない。
- すべての例外を捕まえる catch だけ、Analyzer が `when (ex is not ScopeFailure)` を付けるよう提案する。

**`Effect<R>.Try`：型付きの try/catch**

```csharp
// 指定した型の例外だけを E に変換する。それ以外は Die として通す。
// ラムダの引数に例外の型を書くと、TException、T、E がすべて推論される（RC2 で確認済み）。catch (JsonException ex) と同じ見た目になる
Effect<R>.Try(() => JsonSerializer.Deserialize<Dto>(json)!, (JsonException ex) => new ParseError(ex.Message));

// Results と同じ、すべての例外を変換する形も用意する
Effect<R>.Try(() => ..., ex => new LegacyError(ex));
```

- どの例外がどのエラー型に変わるかがシグネチャに出るので、予測しやすい。
- 外部の API（JSON、HTTP、昔からあるライブラリなど）との境界で、関数として一度だけ定義するのが基本の使い方。必須ではなく、選択肢の1つ。
- `Try` は `ScopeFailure` を必ず素通しする（`TException` が `Exception` の場合も同じ）。
  - 実装してみると、`Effect<R>.Try` の本体から scope を使う道はほぼない：`Try` の Effect は `scope.Run` の実行中に動くので、その中で scope を使うと「同時に使った」として検出され、`InvalidOperationException` になる（D11）。素通しは、その検出をすり抜けた場合に備える防御として残す（P2）。そのため、**`Effect<R>.Try` は Results の `Result.Try` を使わず、専用に実装する**（`catch (ScopeFailure) { throw; }` を先に置く。G2）。Results 側に `ScopeFailure` を認識させる設計にはしない（依存の向きを守る）。
- `OperationCanceledException` の扱いは Results の `Result.Try` / `TryAsync` と同じ。

**`Attempt()`：失敗を値として受け取る**（Effect-TS の `Effect.either`、cats-effect の `attempt` に相当）

```csharp
// Effect<T, E, R> → Effect<Result<T, E>, Never, R>
var found = await scope.Run(FindUser<R>(id).Attempt());
var user = found switch
{
    Ok<User>(var u)            => u,
    Err<AppError>(NotFound)    => User.Guest,
    Err<AppError>(Forbidden f) => throw scope.Fail(f),
};
```

- union に対する `switch` なので、case の網羅性をコンパイラが検査する。
- `ToResult()` という名前は「その場で実行して Result にする」と読めてしまう（.NET の `ToList()` などと同じ印象になる）ため採用しない。`Attempt()` は遅延したまま。

**Moderato.Results への提案**

- 型付きの try/catch：`Result.Try(func, (TException ex) => error)` と `TryAsync`（ラムダの引数の型から `TException` を推論する形）。Gen の中では `scope.Get(Result.Try(...))` として使える（ラムダの中で scope を使わない限り。D11）。`Effect<R>.Try` は、`ScopeFailure` を素通しする必要があるため、これとは別に実装する。
- `Never`（D3）。

### D13. Source Generator

**エラー型を広げる処理**

```csharp
public interface IUnionCase<TSelf, TCase> where TSelf : IUnionCase<TSelf, TCase>
{
    static abstract TSelf From(TCase value);                 // 広げる：case → union
    static abstract bool TryGet(TSelf self, out TCase value); // 狭める：union → case
}

[Error]
public partial union AppError(NotFound, Forbidden, TimeoutError);
// → Source Generator が IUnionCase<AppError, NotFound> などを実装する
```

- Source Generator がするのは、**属性を付けた `partial union` にインターフェースを実装することだけ**。属性を付けていない union には何も生成しない（原則3）。
- ライブラリ側は、union の種類に関係なく汎用のコードで書く。Source Generator がなくても、インターフェースを手で実装すれば同じように動く。
  - `scope.Run` / `scope.Get` の case 版（D11）
  - `Timeout(...) where E : IUnionCase<E, TimeoutError>`（D8）
  - `Effect<T, E, R>.Widen<F>() where F : IUnionCase<F, E>`：LINQ やコンビネータで明示的に広げる。LINQ では、合成した結果をどの union にするかを C# が決められないので、自動では広げられない。
  - `CatchCase((TCase c) => ...)`：`TryGet` を使って1つの case だけ処理する（E は変わらない）。インスタンスメソッドではクラスの型パラメータ E に制約を付けられない（CS0699）ため、拡張メソッドにし、TCase はラムダの引数の型から推論させる（S1 で確認）。拡張メソッドでは E もメソッドの型パラメータになり、ハンドラが `Effect<T, Never, R>` を返すと推論がぶつかるので、ハンドラが Never の Effect を返す版のオーバーロードも用意する。
- .NET 11 RC2 で確認済み：`partial union` を書ける。union に static abstract メンバーを持つインターフェースを実装できる。汎用の拡張メソッドで型推論が効く。間違った case を渡すとコンパイルエラーになる。
- union は Roslyn のプレビュー API ではなく、**`System.Runtime.CompilerServices.IUnion` を実装していること**（または `[Union]` 属性が付いていること）と、引数1つの public コンストラクタ＝case であることで検出する。S2 で確認した事実：
  - コンパイル中のソースの union には、`[Union]` 属性が**見えない**（コンパイラが出力時に付ける）。`IUnion` の実装と、case ごとのコンストラクタ、`Value` プロパティは見える。
  - 参照しているアセンブリ（メタデータ）の union には、`[Union]` 属性と `IUnion` の両方が見える。
  - 手で書いた union（`[Union]` 属性を付けた struct）は、ソースでも属性が見える。
  - Roslyn 4.14 で作った Generator が、SDK の Roslyn 5.12 の上で動く。union の宣言は `UnionDeclarationSyntax`（`TypeDeclarationSyntax` の派生）として `ForAttributeWithMetadataName` で拾え、`partial` も構文から判定できる。生成した `partial union` のコードも正しく足される。
- 最初は、ジェネリックではない union だけを対象にする。ジェネリックな union には診断を出す。

**入れ子の union**

- 推移的な case も生成する。たとえば `union AppError(DbError, NotFound)` で `DbError(ConnFailed, QueryFailed)` なら、`IUnionCase<AppError, ConnFailed>` も実装する。
- 同じ case に届く道が2本あるとき（DbError と NetError の両方に Timeout があるときなど）は、推移的な実装を生成せず、警告を出す。

**`IHas<T>` は不変にする**

- `IHas<out T>` だと、たとえば `IHas<SqlRepo>` と `IHas<MemRepo>` の両方を実装した環境が、`IHas<IRepo>` を要求する Effect に**警告なしで**渡せてしまう。そのとき、どちらの実装が返るかは CLR が勝手に決める（RC2 で確認済み）。

**環境への `IHas<>` 実装の自動生成**：`[Environment]`

```csharp
[Environment]
public sealed partial record AppEnv(IUserRepository Users, ILogger Logger, TimeProvider Clock);
// → IHas<IUserRepository>、IHas<ILogger>、IHas<TimeProvider> を実装する
```

- 対象は**プライマリコンストラクタの引数だけ**。record でも class でも使える（class のプライマリコンストラクタの引数を、別の partial 宣言から参照できることを確認済み）。
- 同じ型の引数が2つあるときは、Source Generator がエラーを出す。
- 依存をまとめるインターフェース（D5 の `IUserDeps`）を環境の宣言に書けば、生成された `IHas<>` によって自動で満たされる。
- どの型として公開するかは `[Expose<T>]` で引数ごとに指定できる（D14）。

**属性名**：`[Error]`（union 用）、`[Environment]`（環境用）、`[RestOf<E>]`（下記）

- `[Error]` は、利用者が自分で `record Error` を定義していても衝突しない。C# は属性名を解決するとき、属性クラスではない型は候補から外すため（RC2 で確認済み）。

**catchTag（union から case を1つ取り除く）**

C# の union は名前を付けて宣言しないと存在できないので、「取り除いた残りの型」は自動では作れない（和集合が作れないなら、差集合も作れない）。

| | API | 型の変化 |
|---|---|---|
| (a) | `CatchCase((TCase c) => ...)` | E のまま（Effect-TS の `catchSome`） |
| (b) | `Attempt()` ＋ `switch` | E → `Never`（すべての case を処理する。D12） |
| (c) | `OrElse<F>(e => e switch { ... })` | E → 利用者が書いた F |
| (d) | `Catch<TCase, F>(handler)` | E → F。F は利用者が宣言した「残り」の union（Effect-TS の `catchTag` に相当） |

(d) の仕組み：

```csharp
[Error] public partial union AppError(NotFound, Forbidden, TimeoutError);
[Error, RestOf<AppError>] public partial union AccessError(Forbidden, TimeoutError);   // AppError から NotFound を除いたもの

Effect<User, AccessError, R> e = FindUser<R>(id).Catch<NotFound, AccessError>(nf => Effect<R>.Succeed(User.Guest));

// ライブラリ：インターフェースは「残り」の union（F）の側に実装する。
// 制約がメソッド自身の型パラメータ F に付くので、Effect のインスタンスメソッドとして書け、
// 型引数は TCase と F の2つだけ書けばよい（T、E、R はインスタンスの型から決まる）
public interface IUnionRest<TSelf, TSource, TCase> where TSelf : IUnionRest<TSelf, TSource, TCase>
{
    static abstract bool TrySplit(TSource source, out TCase caught, out TSelf rest);
}
public Effect<T, F, R> Catch<TCase, F>(Func<TCase, Effect<T, F, R>> handler) where F : IUnionRest<F, E, TCase>;
```

Source Generator の処理：

1. **F の側で `[RestOf<E>]` を付けたものだけを対象にする**（F17）。公開 API にインターフェースが勝手に増えず、何が生成されるかが宣言から分かる（原則3）。
2. 取り除く case X は、「E の case のうち F に含まれないもの」として決まる。ちょうど1つでなければ診断を出す（`Catch` は1つの case を取り除く操作のため）。E の残りの case は、すべて F に含まれている必要がある（推移的に含まれる場合も数える）。
3. **X に指定できるのは E の直接の case だけ**とする（F16）。入れ子の union の内側の case（`DbError` の中の `ConnFailed` など）は、最初は対象にしない。
4. `TrySplit` は、E に対する `switch` として生成する。X なら `caught` に、それ以外の case なら F に広げて `rest` に入れる。
5. 条件を満たさない F を指定すると、コンパイルエラー（CS0315）になる。そのままではメッセージが分かりにくいので、Analyzer が「AccessError には AppError の case のうち Forbidden が足りない」のように言い換える。
6. 実装は Incremental Generator で行う。`ForAttributeWithMetadataName` で属性を付けた union を集め、`Collect` してから組み合わせを求める。キャッシュが効くよう、中間のモデルは等価比較できる値（型名の文字列など）にする。

RC2 での確認：`Catch<NotFound, AccessError>` で NotFound が型から消える。NotFound 以外の case（Forbidden）は AccessError に入って残る。足りない case がある union を指定すると CS0315 になる。

複数の case を一度に取り除く `Catch<A, B, F>` は、必要になったら追加する。それまでは `Catch` を2回つなげて書ける。

### D14. 標準サービス

**原則**：ライブラリ自身の操作が必要とするものだけを、標準サービスとする。

- それ以外（ログ、設定、コンソールなど）は、利用者が `IHas<T>` で自由に環境に入れる。ライブラリはそれらについて何も知らない。
- Effects 本体の依存は、.NET の基本クラスライブラリと Moderato.Results だけに保つ。`ILogger` などとの連携が必要になったら、別パッケージにする。

| サービス | ライブラリ自身の操作が必要とするか | 扱い |
|---|---|---|
| 時計（`TimeProvider`） | ✅ `Sleep`、`Timeout`、`Retry` / `Schedule` | 標準にする。R に入れる（`IHas<TimeProvider>`） |
| 乱数（`System.Random`） | ✅ `Schedule` の待ち時間のばらつき（jitter） | 標準にする。R に入れる（`IHas<Random>`、D15） |
| ログ、設定、コンソール | ❌ | 標準にしない |
| トレース（`ActivitySource`） | △ | `WithSpan` のような機能を作るときに検討する |

**時計は R に入れる**

- Effect-TS は時計を R に入れず、実行時の仕組みが自動で用意する。しかしこのライブラリでは、D6 の線引き（実行中に変わるものは実行時の文脈、変わらないものは R）に従い、R に入れる。
- 時間に依存する処理はテストで一番困るところなので、それがシグネチャから見えることに価値がある。
- R3 の `ObservableSystem.DefaultTimeProvider` のような、全体共通の既定値は持たない。持つと、時間への依存が型から見えなくなるため。

```csharp
public static Effect<Unit, Never, R> Sleep<R>(TimeSpan duration) where R : IHas<TimeProvider>;   // static class Effect に置く
public static Effect<T, E, R> Timeout<T, E, R>(this Effect<T, E, R> effect, TimeSpan after)
    where R : IHas<TimeProvider>
    where E : IUnionCase<E, TimeoutError>;
```

- 実装には .NET 8 以降の `Task.Delay(TimeSpan, TimeProvider, CancellationToken)` や `new CancellationTokenSource(TimeSpan, TimeProvider)` を使う。
- テストでは `Microsoft.Extensions.TimeProvider.Testing` の `FakeTimeProvider` を環境に入れ、`Advance` で時間を進める。

**乱数は `System.Random` を共通の型にする**

- 実装は、利用者が `System.Random` の派生クラスとして環境に入れる。`Moderato.Mathematics.Random` は Effects の**依存ではなく**、そうした派生クラスを提供する別のライブラリの1つ（F19）。要件は `docs/requirements/moderato-mathematics-random.md` にまとめた。
- スレッドセーフでない `Random` を入れても壊れないよう、ライブラリ側で使い方を工夫する（D15）。

**`[Expose<T>]`：どの型として公開するかを、引数ごとに指定する**

```csharp
[Environment]
public sealed partial record TestEnv([Expose<TimeProvider>] FakeTimeProvider Clock);   // IHas<TimeProvider> を生成する
```

- `IHas<T>` は不変（D13）なので、`FakeTimeProvider` のまま宣言すると `IHas<FakeTimeProvider>` が生成され、`IHas<TimeProvider>` を要求する Effect に渡せない。
- テスト側では、`Advance` などを使うために、具体的な型のまま持っておける。

**Analyzer：時計を直接使っていたら警告する**

- 対象の範囲：Gen の中、`Effect<R>.Sync` / `Async` などに渡すラムダの中、`Effect<R>.Succeed` の引数（F11）。
- 検出する API：`DateTime.Now` / `UtcNow`、`DateTimeOffset.Now` / `UtcNow`、`TimeProvider.System`、`Task.Delay`、`Thread.Sleep`、`Stopwatch.GetTimestamp`、`Environment.TickCount` / `TickCount64`。
- `IHas<TimeProvider>` 経由で取得するか（`GetUtcNow` / `GetTimestamp`）、`Effect.Sleep` を使うよう提案する。
- 既定の重要度は Info（提案）にする。時間をテストで差し替えたいプロジェクトは、`.editorconfig` で Warning に上げられる。

### D15. Schedule、Retry、Repeat

R3 と UniTask はどちらも、方針付きの Retry を持っていない。R3 は「複雑な再試行や繰り返しは async/await のループで書いたほうが分かりやすい」としている。その考え方に従い、**よく使う形は小さく用意し、複雑なものは Effect や Gen で書く**、という2段構えにする。

**再試行の意味**

- 再試行するのは **Err（E）だけ**。Die（バグ）は再試行できないものとして扱う。Interrupted も再試行しない。一時的な例外を再試行したいときは、`Effect<R>.Try` で E に変換してから再試行する（D12）。
- 試行のたびに Effect を最初から実行し直す（D1）。試行の中で取得したリソースは、試行ごとに解放される（D9）。
- 再試行し尽くしたら、最後の Err を返す。代わりの処理に切り替えたいときは `OrElse` をつなげる。
- 1回あたりのタイムアウトか全体のタイムアウトかは、書く順番で決まる（`effect.Timeout(2s).Retry(s)` と `effect.Retry(s).Timeout(30s)`）。
- **Retry / Repeat の実行器はループで実装する**（F15）。再帰的な bind で実装すると、ValueTask の入れ子が深くなるため。

**段1：よく使う形だけの、小さな DSL**

```csharp
effect.Retry(Schedule.Exponential(TimeSpan.FromMilliseconds(100)).WithMaxDelay(TimeSpan.FromSeconds(5)).UpTo(3).Jittered(),
             when: e => e is TimeoutError);
```

| 種類 | API | 意味 |
|---|---|---|
| 基本形 | `Schedule.Spaced(d)` | 毎回 d だけ待つ |
| | `Schedule.Exponential(base, factor = 2)` | base, base×2, base×4, … と待ち時間を伸ばす |
| | `Schedule.Immediately` | 待たずに再試行する |
| 回数・時間の制限 | `.UpTo(n)` | **再試行**を最大 n 回まで行う（最初の試行を合わせて最大 n+1 回。Effect-TS の `recurs`、Polly の `MaxRetryAttempts` と同じ数え方）。Repeat では、繰り返しを最大 n 回 |
| | `.During(total)` | 最初の試行から total が経つまで再試行する |
| 待ち時間の調整 | `.WithMaxDelay(max)` | 待ち時間の上限 |
| | `.Jittered()` | 待ち時間にばらつきを加える |

- Schedule が表すのは「いつ、何回」だけで、エラーの中身は見ない。どの Err を再試行するかは `when:` で指定する。
- 条件を渡す引数の名前は、Retry と Repeat の両方で **`when:`** にする（`while` は C# の予約語なので使えない。`catch ... when` と同じ「この条件のとき」と読める）。
- **jitter と型**：`.Jittered()` を付けると、型が `Schedule` から `Schedule<IHas<Random>>` に変わる。Retry は、Schedule が必要とする能力（TNeed）を R に要求する。jitter を使わない人には、乱数の制約は一切出てこない（RC2 で確認済み）。

  ```csharp
  public static Effect<T, E, R> Retry<T, E, R>(this Effect<T, E, R> e, Schedule s, Func<E, bool>? when = null)
      where R : IHas<TimeProvider>;
  public static Effect<T, E, R> Retry<T, E, R, TNeed>(this Effect<T, E, R> e, Schedule<TNeed> s, Func<E, bool>? when = null)
      where R : IHas<TimeProvider>, TNeed;
  ```

- **`Schedule<TNeed>` の制限**（F18）：
  - 追加の能力を1つしか持てない。将来、乱数のほかに別の能力も必要になると、この形では表せない。
  - `Schedule` と `Schedule<TNeed>` は、あえて継承関係にしない（継承させると、jitter 付きの Schedule が乱数の要求をすり抜けてしまうため）。そのため、`.Jittered()` の後に `.UpTo()` などを続けられるよう、両方に同じメソッドが必要になる（API が二重になる。実装は内部で共有する）。
- **乱数の使い方**（F6）：Retry を1回実行するたびに、環境の `Random` から種を1つだけ取り出し、その Retry 専用の `Random` を作って使う。
  - 共有のインスタンスに触るのは、種を取り出す1回だけ。そこは lock で守る（`Random.Shared` を lock しても害はない）。
  - スレッドセーフでない `Random`（テストで入れる `new Random(42)` など）を、`Concurrency.Parallel` の子たちが同時に使っても壊れない。
  - 種を固定すると、順番に実行する限り結果を再現できる。
  - 利用者が入れた `Random`（`Moderato.Mathematics.Random` など）は**種を作るためだけ**に使い、Retry の中では .NET 標準のアルゴリズムを使う（F23）。利用者の乱数の性質が jitter に必要な場合は、将来「分岐できる乱数」を任意で実装できる形を検討する。
  - lock が守るのはライブラリ側の利用だけ。利用者のコードが同じインスタンスを lock なしで並列に使った場合の安全は、利用者の責任になる。

**段2：Effect で書く、自由な再試行の方針**（R3 の「待ち方を async 関数で渡す」に相当）

```csharp
effect.Retry((RetryContext<AppError> ctx) => ctx.Gen<R>(async scope =>
{
    if (ctx.Attempt >= 5 || ctx.Error is not (TimeoutError or RateLimited)) return false;   // 再試行しない
    var wait = ctx.Error is RateLimited r ? r.RetryAfter : TimeSpan.FromSeconds(ctx.Attempt);
    await scope.Run(Effect.Sleep<R>(wait));
    return true;                                                                             // 再試行する
}));
```

- 方針の型は `Effect<bool, Never, R>`。必要な能力（時計やログなど）は R の要求として自然に型に出る。
- **方針は失敗できない**ので、Gen で書くときは `Scope<Never, R>.Gen` になる（F5）。`Scope<AppError, R>.Gen` と書くと `Effect<bool, AppError, R>` になり、型が合わない（暗黙変換は Never から E の方向にしかないため）。書き間違えやすいので、`ctx.Gen<R>(...)`（`Scope<Never, R>.Gen` の省略形）を用意する。
- エラーに応じて待ち時間を決められる（HTTP 429 の Retry-After など）。そのため、以前検討した `delay:` 引数は作らない。
- 段1の DSL も、内部では段2の形に変換して実行する。仕組みを1つにそろえる。
- さらに複雑な場合は、Gen の中で普通のループとして書く。ドキュメントでは「DSL → Effect で書く方針 → Gen のループ」の順に紹介する。

**Repeat（成功したときに繰り返す）**

```csharp
PollStatus<R>(jobId).Repeat(Schedule.Spaced(TimeSpan.FromSeconds(1)).During(TimeSpan.FromMinutes(5)),
                            when: status => status is Running);
```

- 途中で Err になったら、そこで止まり、その Err を返す。
- 最後の成功値を返す。`During` で時間切れになっても、最後の値を返す（失敗にはしない）。時間切れを失敗にしたいときは `Timeout` と組み合わせる。
- 段2と同じく、Effect で書く方針も渡せる。

**API の形（R3 / UniTask との比較から）**

- 時計は R でだけ受け取る（D14）。
- 例外を投げない Timeout は `TimeoutResult`（D8）。`Attempt` と同じく、Result で返ることが名前で分かる。
- 並列の指定は `Concurrency.Sequential / Parallel.WithMaxDegree(n)`（D10）。R3 の `AwaitOperation` と `maxConcurrent` と同じ考え方。
- 時計の種類（UniTask の `DelayType` のような、ゲーム内の時間や実時間の区別）は、フレーム単位の時間と一緒に、リリース後に検討する。

### D16. 外部との相互運用と実行 API

**Effect を作る（外の世界 → Effect）**

R を推論する手がかりがないので、作る API はすべて **`Effect<R>` の static メソッド**にし、最初から3引数の Effect を返す（F3 / F7）。Effect を返すメソッドはもともと `<R>` を持っているので、普段は `Effect<R>.` と書くだけで済む。

| 元になるもの | API | 戻り値の型 |
|---|---|---|
| 値 | `Effect<R>.Succeed(v)` | `Effect<T, Never, R>` |
| エラー | `Effect<R>.Fail(e)` | `Effect<Never, E, R>` |
| `Result<T, E>` | `Effect<R>.FromResult(r)`（代入先が決まっていれば暗黙変換も可） | `Effect<T, E, R>` |
| 副作用のある同期処理 | `Effect<R>.Sync(() => ...)` | `Effect<T, Never, R>`（例外は Die） |
| 非同期処理（Task / ValueTask） | `Effect<R>.Async(ct => ...)` | `Effect<T, Never, R>`（例外は Die） |
| 例外を投げうる処理 | `Effect<R>.Try(() => ..., (TException ex) => ...)` / `TryAsync`（D12） | `Effect<T, E, R>` |
| `ValueTask<Result<T, E>>` を返す既存のコード | `Effect<R>.FromResultAsync(ct => ...)` | `Effect<T, E, R>` |
| 環境から読む | `Effect<R>.Access(env => ...)` / `AccessAsync((env, ct) => ...)` | `Effect<T, Never, R>` |
| 環境そのもの | `Effect<R>.Env` | `Effect<R, Never, R>` |
| 待つ | `Effect.Sleep<R>(d)`（`where R : IHas<TimeProvider>`、D14） | `Effect<Unit, Never, R>` |

- **どちらの書き方になるかの規則**（F22）：R に制約が要る API（`Sleep` など）は `Effect.X<R>(...)`、それ以外は `Effect<R>.X(...)`。C# では static class の型パラメータに、メソッドごとの制約を付けられないため。
  - `Effect<AppEnv>.Succeed(1)` も「AppEnv を返す Effect」と読めなくはない。それでも採用するのは、`Effect<R>` が static class で、必ず `.Succeed` などのメソッド呼び出しが続き、値の型として現れないため。Gen の入口に `Effect<AppError, R>.Gen` を採用しなかったのは、それが値の型（`Effect<T, E>`）と同じ形に読めてしまうからだった（D11）。
- 戻り値の Never は、D1 の暗黙変換で任意の E / T に変わる。`Effect<R>.Fail<AppError>(new NotFound(id))` は `Effect<Never, AppError, R>` になり、どの値の型の Effect としても使える。union の case は、引数の位置で C# の暗黙の変換により union に変わる。いずれも `scope.Run` やラムダの戻り値に、そのまま渡せる（RC2 で確認済み）。
- **`Fail` の型引数は省略しない**（F25）。`Effect<R>.Fail(new NotFound(id))` は `Effect<Never, NotFound, R>` になり、ラムダの戻り値として返すと「Never → T」と「NotFound → AppError」の2段の変換が必要になって通らない（`scope.Run` に渡す場合は case 版のオーバーロードで通る）。ドキュメントの例はすべて `Fail<AppError>(...)` と書く。
- **`Succeed` と `Sync` を区別する**。`Effect<R>.Succeed(DateTime.UtcNow)` は作った瞬間に値が決まり、何度実行しても同じ値になる。副作用のある処理は `Sync` でラムダに包み、実行するたびに評価させる（Effect-TS の `succeed` / `sync` と同じ）。D14 の Analyzer が、`Succeed` の引数に時計が使われていることを検出する。
- **`Async` と `FromResultAsync` は別の名前にする**。同じ名前のオーバーロードにすると、`ValueTask<Result<...>>` を返すラムダが両方に当てはまってしまう。C# はより限定的なほうを選ぶので一応動くが、どちらが選ばれたかがコードから見えない（原則3）。
- **環境から読む API の名前は `Access`**（F10）。リソースを使って解放する `resource.Use`（D9）と区別するため。ZIO の `environmentWith` に近い意味。
- **ラムダの中で Effect をその場で作るときの注意**：Results と同じく、`AndThen(x => cond ? Effect<R>.Succeed(x) : Effect<R>.Fail(e))` のように条件演算子の両側の型が違うと、型推論が効かないことがある（CS0411）。そのときは `AndThen<int>(...)` のように型引数を書く（RC2 で確認済み）。ドキュメントで案内する。

**Effect を実行する（Effect → 外の世界）**

- `RunAsync(env, ct)` → `ValueTask<Result<T, E>>`。Die と Interrupted は例外として投げる（D7）。
- `RunExitAsync(env, ct)` → `ValueTask<Exit<T, E>>`。すべての結果を値で返す（D7）。
- **同期で実行する API（`RunSync`）は作らない**。同期で待つとデッドロックの原因になるため（R3 の「同期 API は作らない」と同じ）。

**ライブラリが用意する環境**

- **`EmptyEnvironment`**：何のサービスも持たない環境。依存のない Effect は、`Parse<EmptyEnvironment>("42").RunAsync(ct)` のように、環境を渡さずに実行できる。唯一の値は `EmptyEnvironment.Value`（Results の `Unit.Value` に合わせる）。Effect-TS の `Context.empty`、ZIO の `ZEnvironment.empty` と同じく「空」という語を使い、`[Environment]` 属性とも名前をそろえる。`Environment` という名前は `System.Environment` と紛らわしいので避ける。
- **`DefaultEnvironment`**：D14 の標準サービスだけを持つ環境（`IHas<TimeProvider>` に `TimeProvider.System`、`IHas<Random>` に `Random.Shared`）。時計を使う Effect（`Sleep` / `Timeout` / `Retry`）を、小さなコンソールアプリなどで、自分の環境を作らずに実行できる。

**Results と同じ操作**

- `Map` / `MapErr` / `AndThen` / `AndThrough` / `OrElse` / `Tap` / `TapErr` を用意する。
- `Match(ok, err)` → `Effect<U, Never, R>`。実行はせず、両方の場合を1つの値にまとめる Effect を返す。
- `UnwrapOr` / `UnwrapOrElse` → `Effect<T, Never, R>`。
- 複数の Effect をまとめる操作は、`All` / `AllErrors` / `AllSettled`（D10）。
- state 引数を取るオーバーロード（クロージャを作らずに済む形）は、最初は用意しない。ベンチマークで必要性が分かってから追加する。

**対象外**

- ストリーム（複数の値が流れてくるもの）：`IAsyncEnumerable`、`Channel`、R3 に任せる。必要になったら、つなぎの API だけを後で検討する。
- 同期で実行する API。

**実行するスレッド**

- ライブラリの内部では `ConfigureAwait(false)` を使う（Results と同じ）。
- Gen の中で利用者が書いた `await` は、普通の C# と同じく元のコンテキストに戻る。
- `ConfigureAwait` を指定する引数は用意しない。Gen の中で利用者が自由に書けるため。

### D17. DI との連携

**環境は普通の DI サービスとして登録する**。DI 専用の API は本体に入れない。

```csharp
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AppDbContext>();
builder.Services.AddScoped<AppEnv>();          // 環境そのものを登録するだけ

app.MapGet("/users/{id}", async (int id, AppEnv env, CancellationToken ct) =>
    await GetProfile<AppEnv>(id).RunAsync(env, ct) switch { ... });
```

- `[Environment]` の環境はただの record / class なので、DI がプライマリコンストラクタを使ってそのまま組み立てられる。Keyed サービス（`[FromKeyedServices]`）も引数に使える（Microsoft.Extensions.DependencyInjection 10.0 で確認済み）。
- 環境は、中に入れるサービスのうち**一番寿命が短いもの**に合わせて登録する。Scoped な `DbContext` を入れるなら、環境も Scoped にする。間違えて Singleton にしても、`ValidateOnBuild` / `ValidateScopes` が起動時にエラーにする（確認済み。ASP.NET Core の開発環境では既定で有効）。

**`IServiceProvider` を包んだ環境は作らない**

- 作ると、必要な依存が型（R）に現れなくなり、足りないサービスが実行時にしか分からなくなる。D5 の設計の価値を根本から失う。
- 技術的にも、「あらゆる T について `IHas<T>` を実装する」ことは C# ではできない。

**環境の組み立て方の使い分け**

| 組み立て方 | 向いている場面 | リソースの寿命を管理するもの |
|---|---|---|
| DI | ASP.NET Core、Generic Host を使うアプリ | ホスト（DI のスコープ） |
| Resource ＋ `Provide`（D9） | コンソールアプリ、テスト、ホストを使わない場面 | Effect（`Use`） |
| 直接 `new` | テスト | テストコード |
| `EmptyEnvironment` / `DefaultEnvironment`（D16） | 依存のない Effect、標準サービスだけで足りる小さなアプリ | — |

DI があるアプリでは、DI が Layer の役割を果たす。

**ドキュメントに書く注意点**

- Scoped な `DbContext` はスレッドセーフではない。`Concurrency.Parallel`（D10）で使う処理には、`IDbContextFactory<T>` を環境に入れる。
- 同じ型のサービスを2つ入れると、`IHas<T>` が重複して Source Generator がエラーを出す（D13）。それぞれを別の型で包むか、`[Expose<T>]`（D14）で公開する型を分ける。
- リクエストより長く動かしたい処理は、Fork ではなく、Hosting パッケージなどで動かす（Fork した子はリクエストの Effect と一緒に終わる。D10）。

### D18. 失敗モデル

並行処理を持つ Effect System では、「成功か失敗か」だけでなく、「なぜ失敗したか」「どの失敗が先に起きたか」「その失敗で何が中断されたか」が重要になる（G1 / G3）。公開する Exit はフラットなまま（D7）にし、内部ではこれらの情報を失わないようにする。後から Effect-TS の `Cause` のような木構造に広げるときに、実行エンジンを作り直さずに済むようにするため。

**内部の実行結果：主となる結果と、付属情報**

```csharp
// 内部の型（公開しない）
internal readonly struct Outcome<T, E>
{
    Primary;                                   // Ok(T) / Err(E) / Die(Exception) / Interrupted のどれか1つ
    ImmutableArray<Suppressed> Suppressed;     // 付属情報。成功のときはふつう空なので、コストはほぼない
}

// 付属情報は公開する（Die から取り出せるようにするため）
public sealed record Suppressed(ExitCase Case, Relation Relation);   // ExitCase（D9）を使い回す。Succeeded は入らない
public enum Relation
{
    Sequential,    // 後から起きた（本体の後の解放処理など）
    Parallel,      // 同時に起きた（並列の兄弟）
    Consequence,   // キャンセルされた結果として起きた
}
```

- 中身のデリゲートは `Func<R, RunContext, ValueTask<Outcome<T, E>>>`。`RunContext` は実行時の文脈（内部の型）で、CancellationToken、キャンセルの理由、診断用ハンドラー、追跡（Q11）の差し込み口を持つ。
- 付属情報に `Relation` を付けておくのは、将来 `Cause` の木（Sequential / Parallel）に広げるため。

**キャンセルの理由**

```csharp
internal enum CancellationReason { Caller, Timeout, RaceLoser, SiblingFailure, ParentScopeClosed, ChildDefect }
```

- キャンセルを送る仕組み（Timeout、Race、All、Fork、Scope）は、それぞれ自分専用のキャンセルの単位（CancellationTokenSource と、その理由）を持つ。理由は、最初の1つだけを `Interlocked.CompareExchange` で確定する。
- 子が終わったときは、「終わった時点で、自分のトークンにキャンセルが要求されていたか」を記録する。これで、ある失敗が**独立して起きたもの**か、**キャンセルされた結果として起きたもの**かを区別する。

**起きたことの分類**

| 起きたこと | 分類 |
|---|---|
| 本体が、キャンセルを要求される**前に** Ok / Err / Die で終わった | 通常の結果（主となる結果の候補） |
| 本体が、自分のキャンセルによって **OCE** で終わった | 想定どおりの中断。**捨てる**（付属情報にもしない） |
| 本体が、キャンセルを要求された**後に** OCE 以外の例外で終わった | `Consequence` として付属情報にする。**例外**：キャンセルの理由が呼び出し側（Caller）で、ほかに主となる失敗がないときは Die（F26） |
| **解放処理**が例外を投げた（いつでも） | Die。主となる結果の候補になる（G3 / G5） |

**主となる結果の優先順位**

1. **Die**（独立して起きた本体の Die か、解放処理の Die）。複数あれば、時間順で最初のもの。残りは付属情報にする。
2. **Interrupted**：本体が呼び出し側のキャンセルによって終わったとき。本体がすでに終わってから呼び出し側がキャンセルした場合（解放処理の途中など）は、結果を変えない。
3. **キャンセルのきっかけが決めた結果**：Timeout なら `Err(TimeoutError)`、All なら最初に独立して起きた失敗、Race なら勝った側。
4. **通常の Ok / Err**。

この順位で、各場面の結果は次のようになる。

| 場面 | 結果 |
|---|---|
| Timeout の後、子が OCE で正常に終わる | `Err(TimeoutError)` |
| Timeout の後、子の解放処理が例外を投げる | Die（主）＋ Timeout（付属、Sequential） |
| Timeout の後、子の本体が `ObjectDisposedException` を投げる | `Err(TimeoutError)` ＋ Die（付属、Consequence） |
| All で A が Err、そのキャンセルで B が例外 | Err(A)（主）＋ Die(B)（付属、Consequence） |
| All で A が Err、B がキャンセルの前に独立して Die | Die(B)（主）＋ Err(A)（付属、Parallel） |
| Race の勝者が決まった後、負けた側の解放処理が例外 | Die（主）。勝者の Ok は捨てる |
| 呼び出し側のキャンセル中に、Timeout も同時に発火 | Interrupted（F12。呼び出し側を優先） |
| 呼び出し側のキャンセルで、子が `IOException` | Die（F26） |
| Fork した子が独立して Die、親の本体は OCE で終わる | 子の Die（主） |

**付属情報の Die を、黙って消さない**

- 主となる結果に載らなかった付属情報の **Die は、D10 の診断用ハンドラーに報告**する。たとえば次の場面。
  - 主となる結果が Err のまま `RunAsync` / `RunExitAsync` に届いたとき（Results の `Ok` / `Err` には付属情報を載せる場所がない）。
  - Err が `OrElse` / `Retry` / `CatchCase` / `Attempt` で処理されたとき（Err と一緒に付属情報も消えるため）。
- Err の付属情報（兄弟の Err など）は、報告せずに捨てる。

**付属情報が増え続けないようにする**

- Retry の各試行の付属情報は、次の試行に持ち越さない（Err は再試行で処理済みのため）。付属情報の中の Die だけを、上の規則で報告する。
- 1つの Outcome が持つ付属情報には上限を設ける（100件）。超えた分は件数だけを記録し、`Die.OmittedSuppressed` で公開する（P3）。

**実装メモ（P3）**

- 「キャンセルが要求された後に起きた Die か」は、例外を Outcome に変えた時点のトークンで判定し、Outcome に印として持たせる。Gen が内側の Die を投げ直したときは、内側での判定を引き継ぐ（外側のトークンが後からキャンセルされていても、解放処理の Die を「キャンセルの結果」と誤って扱わないため）。
- キャンセルを送る仕組みは、内部の `Cancellation`（親につながった CancellationTokenSource と理由）を共通の部品にする。「処理が先に終わった」と「ある理由でキャンセルした」のどちらか先に起きた方だけを確定する。
- Timeout で中断された Gen ブロックの解放処理は、ExitCase として `Interrupted` を受け取る（ブロックの内側から見るとキャンセルで終わったため）。外側の Timeout がそれを `Err(TimeoutError)` に変える。

## 未決事項

- **Q11. 追跡機能**：デバッグ用に、実行中の Fiber、Scope、リソースを一覧できる機能。機能の本体はリリース後に作る。
  - **実装の約束（今から守る）**：Fiber、Scope、Resource の生成と終了は、それぞれ1か所の内部処理に集める。追跡機能は、そこに記録を差し込むだけで作れるようにする。追跡が無効のときの負担は、フラグを1つ確認するだけにする。
  - **公開の仕方の第一候補（リリース後に決める）**：両方を用意する。
    - 数と区間は .NET 標準の `System.Diagnostics.Metrics` と `ActivitySource` に乗せる。`dotnet-counters` や OpenTelemetry で、本番環境でも見られる。
    - スタックトレース付きの詳細な一覧は、R3 の `ObservableTracker` のような全体共通のスイッチで、開発時だけ有効にする。


## リリース後の追加機能の候補

- **`Moderato.Effects.Hosting`**：長く動き続ける Effect を `BackgroundService` として動かす基底クラス。アプリの停止を CancellationToken に連動させ、Effect からは Interrupted に見えるようにする。Join しなかった子の Err を報告するハンドラー（D10）の既定を、`ILogger` に向ける。
- **`Moderato.Effects.AspNetCore`**：`Result` / `Exit` を `IResult` に変換する補助（Die を ProblemDetails にする、など）。
- **フレーム単位の時間**（R3 の `FrameProvider`、UniTask の `DelayFrame`）：今は想定していない。.NET 11 の union を前提にしているため、当面 Unity では使えない。必要になったら、`IHas<FrameProvider>` のような形で Schedule などを拡張する。

## 設計レビューで変えた点

外部の設計レビュー（F1〜F26、G1〜G9）を受けて、次の点を変えた。

| 指摘 | 変えた点 | 該当 |
|---|---|---|
| F3 / F7 | 型パラメータが2つの `Effect<T, E>` をやめた。作る API をすべて `Effect<R>` に移し、3引数の Effect を返すようにした。`Fail` は値の側を Never にした（`Effect<Never, E, R>`）。`Effect.Succeed` / `Fail` が Results の `Ok` / `Err` を返す形もやめた | D1, D16 |
| F1 | 「一度出た Err の後の `Run` は即座に巻き戻る」をやめ、「最初の Err が結果になる」だけにした。`finally` の中のロールバックが実行されるようにするため | D11 |
| F2 | `ScopeFailure` に、どの scope が投げたかを持たせた | D11, D12 |
| F4 | `ICancellable` をやめ、CancellationToken は型に出さない実行時の文脈にした | D6, D8, D13, D16 |
| F5 | 段2の方針は `Scope<Never, R>.Gen` になることを明記し、`ctx.Gen<R>` を追加した | D15 |
| F6 | Retry ごとに種を取り出して専用の `Random` を作り、スレッドセーフでない `Random` でも壊れないようにした | D14, D15 |
| F8 | 依存をまとめるインターフェースと、ジェネリッククラスにまとめるパターンを推奨にした | D5 |
| F9 | Timeout が時間の上限にならない場合があることを、ドキュメントで強調する | D8 |
| F10 | 環境から読む API を `Use` から `Access` に変えた | D16 |
| F11 | 時計の Analyzer の対象と検出する API を広げた | D14 |
| F12 | Interrupted の判定規則を「呼び出し側の ct がキャンセル済み、かつ OCE」と明文化した | D7 |
| F13 | Never は sealed class にし、Results 側に置くことも提案する | D3, D12 |
| F14 | `RunAsync` は元の例外を投げ直し、付属情報は `Exception.Data` に入れる | D7 |
| F15 | Retry / Repeat の実行器はループで実装する | D15 |
| F16 | `Catch` で取り除けるのは、最初は E の直接の case だけ | D13 |
| F17 | `IUnionRest` の生成は、F 側の `[RestOf<E>]` で選んだものだけにした | D13 |
| F18 | `Schedule<TNeed>` の制限と、API が二重になることを明記した | D15 |
| F19 | `Moderato.Mathematics.Random` は依存ではないことを明記した | D14 |
| F20 | F1 の修正を戻し、Err の後は即座に巻き戻る規則にした。後始末は `scope.Defer` / `Acquire` で書き、`finally` の中の `scope.Run` は Analyzer が警告する | D9, D11 |
| F21 | 入れ子の Gen の中で外側の scope を使うこと（`Fail` も含む）を禁止した | D11 |
| F22 | `Effect<R>.X` と `Effect.X<R>` の使い分けの規則と、`Effect<R>` を採用する理由を明記した | D16 |
| F23 | 利用者の `Random` は種を作るためだけに使うことと、lock の範囲を明記した | D15 |
| F24 | `Exception.Data` のキーは1つにし、上書きする | D7 |
| F25 | `Fail` の型引数は省略しない | D16 |
| F26 | 呼び出し側のキャンセル中でも、OCE 以外の例外は Die とする | D7 |
| G1 / G3 | 内部の失敗モデルを作った（`Outcome`、付属情報と `Relation`、キャンセルの理由、分類と優先順位の表、黙って消さない規則）。Race の負けた側の Die は勝者の結果より優先する | D7, D10, D18 |
| G2 | `Effect<R>.Try` は Results の `Try` を使わず専用に実装する。`Result.Try` のラムダの中での scope の使用を Analyzer が検出する | D11, D12 |
| G4 | Timeout が保証すること・しないことの表をドキュメントに載せる | D8 |
| G5 | 解放処理に渡す ExitCase は、利用本体が終わった時点で確定する | D9 |
| G6 | 結果の安全性と、制御フローの安全性を区別して明記した | D11 |
| G7 | `Parallel` で子をスレッドプールで起動する形は維持した（理由を明記） | D10 |
| G8 | R の合成のしやすさを、4つの依存を持つサンプルで検証する | 実装計画 |
| G9 | 実装の段階の順番を組み直し、タイミングを制御したテストを実装計画に入れた | 実装計画 |
