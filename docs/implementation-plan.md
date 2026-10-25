# Moderato.Effects 実装計画

設計は `docs/design.md` にまとめてある。この計画は、その設計をどの順番で実装するかを記録する。

## 進め方

- **段階ごとに一度止まって確認する**。段階が1つ終わるたびに「何を作ったか」「設計から変えた点（あれば）」を報告し、確認してもらってから次へ進む。
- **設計と違う形になったら、必ず相談する**。作ってみて設計を変える必要が出ても、勝手には変えない。変えたら設計メモも更新する。
- **コミット**：段階ごとに作業用のコミットをしてプッシュする。最後に、意味のある単位にまとめ直す。
- **最初のプレビュー版を出す時期**：未定。P1〜P4（コア、安全な実行系、構造化並行性）だけでも Effect System として一通り使えて、P5〜P7 は後から足しても既存の API を壊さない。P4 が終わった時点で、出すかどうかを相談する。

## 0. 実装前に確かめること（完了）

| | 確かめたこと | 結果 |
|---|---|---|
| S1 | `Effect` / `Effect<R>` / `Effect<T, E, R>` / `Scope<E, R>.Gen` / `Effect.Gen(...)` の共存と型推論。Never の2つの暗黙変換と LINQ の4通りの `SelectMany`。作る API、`scope.Run` / `Get`（case 版を含む）、`throw scope.Fail`、`Attempt`、`Widen`、`Sleep`、`Timeout`（union / Never）、`All`、`EmptyEnvironment` の `RunAsync(ct)` | **33ケースすべて期待どおり**（成功すべき28ケースが通り、失敗すべき5ケースがコンパイルエラーになる）。ただし設計を1点修正：`CatchCase` は拡張メソッドにし、`CatchCase((NotFound nf) => ...)` の形にする。ハンドラが Never の Effect を返す版のオーバーロードも必要（D13） |
| S2 | netstandard2.0 の Source Generator が `union` を検出し、`partial union` にコードを足せるか | **できる**。Roslyn 4.14 で作った Generator が SDK の Roslyn 5.12 の上で動く。ただし、ソースの union には `[Union]` 属性が見えないので、検出は `IUnion` の実装で行う（D13） |

S1 の33ケースは、回帰テストとしてテストプロジェクトに移す（型推論や変換の結果が、SDK の更新で変わっていないことを確かめるため）。
- 成功すべきケースは、使う API ができた段階で、その段階のテストとして書く。P1 で、LINQ の4通り、`AndThen` の推論、`Widen`、`CatchCase`、`Result` からの変換、`EmptyEnvironment` の `RunAsync(ct)` を移した。P2 で Gen のケース（`Scope<E, R>.Gen` / `Effect.Gen` の推論、`Run` に渡せる Effect の形、`Get` の case 版、`throw scope.Fail` による絞り込み、Gen の中の `Attempt` と `scope.Env.Resolve`）を移した。`Sleep` / `Timeout`（P3）、`All`（P4）のケースは、それぞれの段階で移す。
- コンパイルエラーになるべきケース（5件）は、実行するテストでは確かめられないので、Roslyn を使える `Moderato.Effects.SourceGenerator.Tests`（P6）で、コンパイルして診断を確かめるテストとして書く。

それ以前に、設計の見直しの中で RC2 を使って確認したこと：

- `partial union`、union へのインターフェースの実装、`IUnionCase` / `IUnionRest` を使った汎用のコードでの型推論
- `[Error]` / `[Environment]` という属性名
- class のプライマリコンストラクタの引数を、別の partial 宣言から参照できること
- `Schedule<TNeed>` による、型パラメータの制約を使った能力の追跡
- `Effect<R>.Try(() => ..., (TException ex) => ...)` の型推論
- 引数名 `when`
- C# 14 の拡張ブロックによる、制約付きの拡張プロパティ（手法として確認済み）
- DI が `[Environment]` の環境を組み立てられること、寿命の誤りを検出できること

## 1. 段階

設計レビュー G9 を受けて、「逐次実行のコア → 安全な実行系 → 構造化並行性 → 開発体験」の順に組み直した。どの段階も、その段階だけでテストが通り、使える状態で終わる。

**失敗モデル（D18）は P1 の最初から組み込む**。内部の実行結果の型（中身のデリゲートが返す `Outcome<T, E>`）は、すべての操作の土台になるため。

| 段階 | 中身 | 主な決定 |
|---|---|---|
| P0 | 骨組み：`slnx`、`Directory.Build.props`、`global.json`、CI、プロジェクト構成 | — |
| **逐次実行のコア** | | |
| P1 | **Effect 本体**：`Effect<T, E, R>`、内部の実行結果（`Outcome` / `RunContext`、D18）、`Never`、Never の2つの暗黙変換、`IHas<T>` / `Resolve`、`EmptyEnvironment` / `DefaultEnvironment`、作る API（`Effect<R>` の `Succeed` / `Fail` / `Sync` / `Async` / `FromResult(Async)` / `Try(Async)`（専用実装）/ `Access(Async)` / `Env`）、`RunAsync` / `RunExitAsync`、`Exit` / `ExitCase`、Results と同じ操作、`Attempt`、LINQ（Never のオーバーロードを含む）、`Provide` / `WidenEnv`、`IUnionCase` と `Widen<F>` / `CatchCase` | D1, D3, D5〜D7, D12, D16, D18 |
| P2 | **Gen**：`Scope<E, R>`、`Scope<E, R>.Gen` / `Effect.Gen`、`Run` / `Get` / `Fail` / `Defer`、`ScopeFailure`（どの scope が投げたかを持つ）、Err の後に即座に巻き戻る仕組み、閉じた scope・同時に使った scope・入れ子の Gen での外側の scope の検出、`scope.CancellationToken` | D2, D4, D11 |
| **安全な実行系** | | |
| P3 | **リソースと失敗の扱い**：付属情報の上限（D18。付属情報を作る操作が P3 から出てくるため）、`Resource<T, E, R>`、`Make` / `Use` / LINQ、`scope.Acquire`、解放の規則（ExitCase の確定時点、解放処理の失敗の集約）、キャンセルの理由と、失敗の分類・優先順位（D18）、付属情報の Die の報告、`Sleep`、`Timeout` / `TimeoutResult` | D7〜D9, D14, D18 |
| **構造化並行性** | | |
| P4 | **並行処理**：`Concurrency`、`All` / `AllSettled` / `AllErrors`、`Race` / `RaceFirst`、`Fork` / `Fiber` / `Join`、`OnThreadPool`、診断用ハンドラー | D10 |
| **開発体験** | | |
| P5 | **Schedule**：DSL、`Schedule<TNeed>`、`Retry` / `Repeat`（段1と段2、`ctx.Gen<R>`）、Retry ごとの `Random` の派生、ループによる実行器 | D15 |
| P6 | **Source Generator**：`[Error]`（まず直接の case だけ → 推移的な case）、`[Environment]` / `[Expose<T>]`、`[RestOf<E>]`（`IUnionRest` による `Catch`。最後でよい）、診断 | D13, D14 |
| P7 | **Analyzer**：Gen の中のすべての例外を捕まえる `catch`、`finally` / `catch` の中の `scope.Run`、`Result.Try` などのラムダの中での scope の使用、scope の持ち出し、入れ子の Gen での外側の scope の使用、ループの中での `Acquire` / `Defer`、処理を止めて待つ呼び出し、時計の直接使用（`Sync` / `Async` のラムダ、`Succeed` の引数を含む）、CS0315 の言い換え | D11, D13, D14, D16 |
| P8 | **仕上げ**：README、サンプル、ベンチマーク、`PublicAPI.*.txt`、CHANGELOG、パッケージング | — |

- P1〜P5 では、`IUnionCase` をテストの中で手書きして使う。Source Generator がなくても型安全に動くように設計してあるので（D13）、生成器を後に回して問題ない。手書きの実装が「生成器が出力すべきコードの正解」になり、P6 で答え合わせができる。
- `Timeout` は、`RaceFirst` と同じ内部の仕組み（2つの処理を競わせ、負けた側をキャンセルして終了を待つ）で作る。その仕組みは P3 で作り、P4 の `Race` / `RaceFirst` で公開する。
- Q11（追跡機能）の実装の約束（Fiber、Scope、Resource の生成と終了を1か所に集める）は、P1〜P4 の実装で守る。
- RC2 で確かめた型推論や変換の結果（「確認済み」の一覧）は、回帰テストとしてテストプロジェクトに残す。

### 使い勝手の検証（G8）

P2 が終わった時点で、次の構成のサンプルを書き、`GetUserProfile` に必要な型引数や制約がどれだけ増えるかを評価する。複雑になりすぎるなら、R の表し方を見直すかを相談する。

```
GetUserProfile
 ├── FindUser       → IUserRepository
 ├── CheckAccess    → IPermissionService
 ├── GetCurrentTime → TimeProvider
 └── WriteAuditLog  → IAuditLogger
```

### タイミングを制御したテスト（G9）

並行処理と Scope では、成功する場合よりも、失敗同士が競合する場合のテストが重要。`TaskCompletionSource` と `FakeTimeProvider` でタイミングを制御する。`Thread.Sleep` に頼るテストは、マシンの負荷で結果が変わるので書かない。

| テスト | 確かめること | 段階 |
|---|---|---|
| Gen の中で `ScopeFailure` を catch する | 最初の Err が失われない。その後の `Run` が実行されない | P2 |
| `finally` の中で `scope.Run` を呼ぶ／`scope.Defer` を使う | 前者は実行されず、後者は ExitCase を受け取って実行される | P2 |
| 入れ子の Gen で外側の scope を使う | `InvalidOperationException` になる | P2 |
| `Effect<R>.Try` が `ScopeFailure` を受け取る | Err に変換せず、そのまま伝える | P1 / P2 |
| Timeout の後に子が OCE を投げる | `Err(TimeoutError)` になる | P3 |
| Timeout の後に解放処理が例外を投げる | Die を主とし、Timeout を付属情報に持つ | P3 |
| 解放処理の途中で別の解放処理が失敗する | 後の解放処理が受け取る ExitCase が変わらない | P3 |
| `Acquire` が完了した直後にキャンセルされる | 取得したリソースが漏れない | P3 |
| `All` で Err と Die が競合する | 規則どおりに主となる失敗を選ぶ | P4 |
| `Race` で負けた側の解放処理が失敗する | Die を黙って消さない | P4 |
| Fork した子が Join の前に Die になる | 親に失敗が伝わり、親の結果は子の Die になる | P4 |
| Retry で各試行が Err になる | 各試行のリソースが解放される | P5 |

## 2. プロジェクトの構成

Moderato.Results の構成に合わせる。

```
src/Moderato.Effects/                            本体（net11.0）
src/Moderato.Effects.SourceGenerator/            Source Generator と Analyzer（netstandard2.0。本体のパッケージの analyzers/ に同梱）
tests/Moderato.Effects.Tests/                    xunit v3
tests/Moderato.Effects.SourceGenerator.Tests/    生成コードのスナップショットテストと、診断のテスト
samples/Moderato.Effects.Samples/                設計メモに出てきた例を、動く形にしたもの
bench/Moderato.Effects.Benchmarks/               BenchmarkDotNet（P8）
```

- CI は Moderato.Results と同じく `dotnet test -c Release` を基本にする。
- `PublicAPI.*.txt`（公開 API の変更を検出する Analyzer）は、API が固まる P8 で入れる。
- コードのコメントと README は英語（Moderato.Results と同じ）。設計メモと実装計画は日本語。
