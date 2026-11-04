# Moderato.Effects 実装計画

設計は `docs/design.md` にまとめてある。この計画は、その設計をどの順番で実装するかを記録する。

各段階の結果にあるテスト数・測定値・未公開の記述は、その段階が終わった時点の記録。現在の契約と制約は設計メモ、未出荷の変更は CHANGELOG、利用方法は GitHub wiki を参照する。

## 進め方

- **段階ごとに一度止まって確認する**。段階が1つ終わるたびに「何を作ったか」「設計から変えた点（あれば）」を報告し、確認してもらってから次へ進む。
- **設計と違う形になったら、必ず相談する**。作ってみて設計を変える必要が出ても、勝手には変えない。変えたら設計メモも更新する。
- **コミット**：段階ごとに作業用のコミットをしてプッシュする。最後に、意味のある単位にまとめ直す。
- **最初のプレビュー版を出す時期**：未定。P1〜P4（コア、安全な実行系、構造化並行性）だけでも Effect System として一通り使えて、P5〜P7 は後から足しても既存の API を壊さない。P4 が終わった時点で、出すかどうかを相談する。

## 0. 実装前に確かめること（完了）

| | 確かめたこと | 結果 |
|---|---|---|
| S1 | `Effect` / `Effect<R>` / `Effect<T, E, R>` / `Scope<E, R>.Gen` / `Effect.Gen(...)` の共存と型推論。Never の2つの暗黙変換と LINQ の4通りの `SelectMany`。作る API、`scope.Run` / `Get`（case 版を含む）、`throw scope.Fail`、`Attempt`、`Widen`、`Sleep`、`Timeout`（union / Never）、`All`、`EmptyEnvironment` の `RunAsync(ct)` | **33ケースすべて期待どおり**（成功すべき28ケースが通り、失敗すべき5ケースがコンパイルエラーになる）。ただし設計を1点修正：`CatchCase` は拡張メソッドにし、`CatchCase((NotFound nf) => ...)` の形にする。ハンドラが Never の Effect を返す版のオーバーロードも必要（D13） |
| S2 | netstandard2.0 の Source Generator が `union` を検出し、`partial union` にコードを足せるか | **できる**。Roslyn 4.14 で作った Generator が SDK の Roslyn 5.12 の上で動く。ソースの union には `[Union]` 属性が見えない。現在は `union` 宣言のキーワードまたは `[Union]` 属性で検出する（D13）。`IUnion` の実装だけでは不十分なことを Issue #277 で確認した |

S1 の33ケースは、回帰テストとしてテストプロジェクトに移す（型推論や変換の結果が、SDK の更新で変わっていないことを確かめるため）。
- 成功すべきケースは、使う API ができた段階で、その段階のテストとして書く。P1 で、LINQ の4通り、`AndThen` の推論、`Widen`、`CatchCase`、`Result` からの変換、`EmptyEnvironment` の `RunAsync(ct)` を移した。P2 で Gen のケース（`Scope<E, R>.Gen` / `Effect.Gen` の推論、`Run` に渡せる Effect の形、`Get` の case 版、`throw scope.Fail` による絞り込み、Gen の中の `Attempt` と `scope.Env.Resolve`）を移した。P3 で `Sleep` と `Timeout`（union / Never）を移した。P4 で `All` を移した。
- コンパイルエラーになるべきケース（5件）は、実行するテストでは確かめられないので、Roslyn を使える `Moderato.Effects.SourceGenerator.Tests`（P6）で、コンパイルして診断を確かめるテストとして書く。P6 で移した（`CompileErrorTests`）。

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

**P8 の結果**

- 省略可能な引数を持つオーバーロード（`All` の `concurrency`、Retry / Repeat の `when`、`RunAsync` の `cancellationToken`）は、Moderato.Results と同じく RS0026 / RS0027 をメンバーごとに `[SuppressMessage]` で抑止し、理由を `Justification` に書いた。
- サンプルを書いて、使い勝手の問題が2つ見つかった。どちらも README の注意点に書いた。
  - 失敗しない Resource の `Use` から失敗しうる処理を返せない（G8 の `AndThen` と同じ問題）。変数の型を書いて広げる。
  - コンパイラの CS8655 の不具合に、サンプルの `foreach` の中の `RunExitAsync(env)` がそのまま当たった。`CancellationToken.None` を渡して避けた。
- ベンチマーク（`--job short`、参考値）：素の `Result` の連鎖が約 9 ns に対して、Effect の連鎖は約 350 ns / 272 B、Gen ブロックは約 550 ns / 736 B。Gen ブロックの Err は例外で巻き戻るため約 7 µs。`Effect.All` は8個の小さな非同期処理で `Task.WhenAll` の約6倍。安全性を優先した設計どおりで、README には「Gen の Err は例外のコストがかかる」とだけ書いた。最適化はリリース後に、ベンチマークを見ながら検討する。

- P1〜P5 では、`IUnionCase` をテストの中で手書きして使う。Source Generator がなくても型安全に動くように設計してあるので（D13）、生成器を後に回して問題ない。手書きの実装が「生成器が出力すべきコードの正解」になり、P6 で答え合わせができる。
- `Timeout` は P3 の専用の実装とし、P4 の `Race` / `RaceFirst` とはキャンセルの単位と主となる失敗の選び方を共有する。ゼロ時間などの固有の契約は設計の D8 に従い、一般の `RaceFirst` 呼び出しへの置換とはしない。
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

**結果（P2 後）:** `interface IProfileDeps : IHas<...>` を4つ束ね、`static class Profiles<R> where R : IProfileDeps` に置けば、各関数の型は `Effect<T, E, R>` のままで増えない。R の表し方は変えない。見つかった点は2つ。

1. 失敗しない `Effect<User?, Never, R>` の後に失敗しうる処理を `AndThen` でつなげない（E が Never から変わらない）。Gen で書けば自然に書ける（`return user ?? throw scope.Fail(new NotFound(id));`）。README ではこの書き方を勧める。
2. ループの中で、`CancellationToken` に `default`（省略した既定値を含む）を渡す呼び出しの結果の union を switch すると、全ケースを書いても CS8655（null が網羅されていない）が出る。ライブラリと無関係に再現する RC2 のコンパイラの不具合。変数や `CancellationToken.None` を渡せば出ない。最小の再現コードで上流に報告する。

```csharp
foreach (var id in new[] { 1, 2 })
{
    var result = Get(default);   // Get(CancellationToken ct) => Result<Profile, AppError>
    _ = result switch { Ok<Profile> => "a", Err<AppError>(NotFound) => "b", Err<AppError>(Forbidden) => "c" }; // CS8655
}
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

### 実行器の移行（Issue #267、2026-11-03 承認）

P0〜P8 後の改修。D1 の反復実行器への移行も、冒頭の段階レビューに従う。詳細は [実行器の移行](executor-refactor.md) に記録する。

| 段階 | 完了条件 | 状態 |
|---|---|---|
| E0 契約と再現 | 通常の同期・非同期実行で守る観測をテストに残す。深い合成のプロセス終了を、テストホストから隔離して再現する。候補方式の失敗条件を記録する | 完了、[PR #308](https://github.com/SingUp009/Moderato.Effects/pull/308) でマージ済み |
| E1 Effect の実行器 | 型を保った命令と実行ごとの継続で Map・AndThen・環境変換・Never 変換・LINQ を実行する。動的に返された Effect も反復実行に戻す。E0 の契約と既存テストを保つ | 完了、[PR #308](https://github.com/SingUp009/Moderato.Effects/pull/308) でマージ済み |
| E2 Resource と境界 | Resource の取得・合成・解放、および Gen・時間操作・並行操作との境界を検証する。取得済みリソースは失敗・中断時も逆順に一度ずつ解放する | 完了、[PR #309](https://github.com/SingUp009/Moderato.Effects/pull/309) でマージ済み |
| E3 出荷前の検証 | 深い合成の隔離テストを CI で必須にし、再実行・同時実行・大きな値型・割り当て・性能を確認する。パッケージ、サンプル、NativeAOT、wiki を確認する | 完了、[PR #311](https://github.com/SingUp009/Moderato.Effects/pull/311) でマージ済み |

E0 の終了だけでは #267 を修正済みにしない。各段階の終了時に、実装と設計の差、通った検証、残る制約を報告する。

**E0 の結果**：コンテキスト境界の回帰テスト18件を追加し、Release の全1,708件と空白・スタイル検証が成功した。隔離プローブは5種類の合成を同期・中断ありの両方で実行し、深さ100では全10ケースが成功、10万では全10ケースでスタックオーバーフローを再現した。ライブラリ本体と公開 API は変更していない。反復実行器の実装と出荷前の性能・パッケージ検証は後続段階に残る。

**E1 の結果**：Effect の直接実行を深さ予算で制限し、深い合成と動的な子を型付きフレームで反復実行する。Release の全1,752件が成功し、E0 の契約は浅い経路と反復経路の両方で確認した。Effect の10万段の全14経路は JIT・NativeAOT の両方で成功した。BenchmarkDotNet Dry の全246ケースも完走した。短い AndThen の実行割り当ては0バイトを維持したが、同じマシンの ShortRun で約29%遅くなったため、性能の改善は Issue #307 で E3 に引き継ぐ。Resource の10万段は依然としてプロセスを終了させるので、#267 は E2 以降も継続する。公開 API は変更していない。

**E2 の結果**：Resource の取得・Map・AndThen・LINQ・Never 変換を E1 の実行器へ接続した。無条件の async 隔離と子の失敗の捕捉を維持し、取得成功時の登録と Releases の解放・集約規則は変更していない。Release の全1,781件が成功した。同期／中断ありの10万段の全26経路は JIT・NativeAOT で成功し、全246ベンチマークの Dry 実行も完走した。取得途中の失敗・キャンセル・Timeout、複数の解放の失敗、Gen・All・Race・Fork とその組み合わせを検証した。深いプローブの CI 必須化、Issue #307 の速度低下と Resource の構築・保持コストの評価は E3 に残す。任意深さの制御ラッパー自体の入れ子や、Q13・Q14 の未決事項を解決したとはしない。公開 API は変更していない。

**E3 の結果**：10万段の28ケースと制御ラッパー2,048段の4ケースを、JIT・パッケージ経由の NativeAOT で確認し、CI の必須チェックに追加した。全1,781件、空白・スタイル、サンプル、パッケージ consumer と全270ベンチマークの Dry が成功した。記述を生かしたまま実行後の環境が回収されることを検証した。AndThen の直接経路を軽くし、最新の比較では導入前との差が平均約5.5%だったが、同速とは断定せず #307 を残す。構築・実行の割り当てを分けて測定し、深い Resource の実行には10万段あたり約97 MBが必要だった。#308 の Retry の退行は回復したが、Timeout の耐性低下は #310 に残る。wiki は適用前のパッチを用意し、未公開。公開 API と Q13・Q14 は変更していない。

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
- `PublicAPI.*.txt`（公開 API の変更を検出する Analyzer）は、API が固まる P8 で入れる。P8 で入れた。最初のプレビュー版までは、すべて `PublicAPI.Unshipped.txt` にある。リリースの手順は `RELEASING.md`。
- コードのコメントと README は英語（Moderato.Results と同じ）。設計メモと実装計画は日本語。

## 3. Hosting の実装（2026-11-04）

[Hosting 要件](requirements/moderato-effects-hosting.md)に基づき、次の順で進める。要件は `7c9fa20` で保存済み。各段階をテストして Claude Code の独立レビューを受け、指摘を解消してから次へ進む。設計からの逸脱が必要なら先にユーザーへ相談する。

| 段階 | 対象 | 受け入れ確認 |
|---|---|---|
| H1 | 本体の実行単位の診断先。既存の実行 API と全体共通ハンドラーを維持し、指定した診断先を子・回復・解放・キャンセルの報告まで引き継ぐ | 実行間の分離、既存の既定動作、遅れたコールバック、深い合成、再実行。引数省略や `default` を含む既存呼び出しを曖昧にしない |
| H2 | Hosting プロジェクト、診断の Host 寿命、サービス登録、型付き環境と Err ハンドラー、DI scope の解放と失敗集約 | 実際の Host で要件の節6を確認。同期・非同期のハンドラー失敗、元 Err の保持、同型の複数登録、停止猶予超過と接続終了後を含む |
| H3 | パッケージ・CI・サンプル・公開 API 記録・利用ガイド・リリース手順 | 個別の未公開バージョンで pack し、利用側・Analyzer・NativeAOT と必要な性能確認を実行。公開済みの同名バージョンをローカルキャッシュへ上書きしない |

開発中の Hosting は最新の本体への ProjectReference を使う。公開済み preview.2 に最新の契約がすべて入っているとは扱わない。バージョン確定・タグ・公開はリリース手順の別の段階とする。

H1 は PR #373、H2 は PR #374 でレビュー・マージ済み。H3 は実行物・依存範囲・シンボル・SourceLink・利用側の生成器／Analyzer・Native AOT と wiki を対象とし、確認結果は Hosting 要件の節10に記録する。NuGet 公開・タグ作成・リリース版の確定は含めない。
