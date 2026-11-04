# Moderato.Effects.Hosting の要件（草案）

Moderato.Effects を .NET Generic Host の `BackgroundService` として動かすための要件を整理する。三者での議論とレビューのための草案であり、公開 API・実装・リリース時期を決定するものではない。「必要な契約」と「未決事項の推奨案」を区別する。

本体の基準は main の `bea534380d5854b0460e1ed11d14822ccc5df0da`。背景は [設計メモ](../design.md) の D6〜D10・D17・D18 と、[合成則と観測の範囲](../mathematical-contracts.md) を参照する。

## 1. 依存の向き

- `Moderato.Effects.Hosting` から `Moderato.Effects` と Microsoft の Hosting・DI・Logging の標準抽象へ依存する。本体は Hosting に依存しない。
- 同じリポジトリの別 NuGet パッケージとする案を第一候補とする。構成・対象バージョンは未決。
- 利用者が構築した型付きの環境 `R` を実行入口へ渡す。`IServiceProvider` から任意のサービスを引く万能な環境は作らない（D5・D17）。
- ログの境界は `ILogger` とし、ZLogger など特定のプロバイダーを必須依存にしない。

## 2. 解決したい課題と範囲

長時間動く Effect を、Host の起動・停止と DI の寿命に合わせて実行できるようにする。利用者が毎回書く、停止トークンの接続、終了結果の扱い、診断ログの接続を整理する。

初版の範囲は実行入口とその終了処理に限る。ジョブの永続化、キュー、配送保証、分散ロック、独自スケジューラー、自動再起動は含めない。HTTP リクエストの子 Fiber を切り離す機能も含めない。リクエストより長い寿命の処理は、必要なデータを渡し、Host が所有する別の実行として開始する。

## 3. 求める振る舞い

### H1. 遅延実行と実行の所有者

- DI 登録やサービスの構築だけでは Effect 本体を実行しない。実行の開始は `BackgroundService` のライフサイクルに従う。
- 1つのサービス実行につき、ルートの Effect を1回実行し、その完了を追跡する。起動直後に待機せず完了する場合も同じ契約にする。
- 登録補助で複数の Effect サービスを登録できる形にする場合、E・R の型が同じという理由だけで別の登録を除外しない。各登録の実行と環境の寿命を独立に扱う。
- 終了結果を受け取った後、暗黙に再実行しない。`Ok` で終了しても Host 全体を独自に停止しない。
- Effect 内の繰り返し・再試行は利用者が記述する。Hosting は副作用の冪等性や、外部処理の取り消しを保証しない。

### H2. 停止要求と終了結果

- **2026-11-04 合意**：実行入口は `RunExitAsync` を使い、Die と Interrupted の分類を保つ。Die は Host が異常として観測できる例外で伝え、元の例外・保持する付属情報・省略件数を保つ。OCE の Die をそのまま投げ直して正常停止へ紛れ込ませない。具体的な例外型は QH1 で定める。
- **2026-11-04 合意**：ルートの Err には型付きのエラーハンドラーを必須とする。Hosting が暗黙に成功扱いしたり、再試行・再起動したりしない。ハンドラーの具体的な署名と終了規則は QH2 で定める。
- **2026-11-04 合意**：Interrupted は、結果を受け取った時点で `stoppingToken.IsCancellationRequested` を観測し、キャンセル済みなら正常停止、未キャンセルなら Host へ異常として伝える。その判定を後から状態が変わったことで覆さず、物理的な中断原因の証明とはしない。本体の Interrupted を Die に分類し直すことでもない。
- Die を異常として伝えた後の動作は利用者の `HostOptions.BackgroundServiceExceptionBehavior` に従う。確認した既定の `StopHost` では Host 全体の停止要求につながり、同居するサービスも停止対象になる。`Ignore` を選んでも終了したサービスは再起動されない。Hosting がこの設定を勝手に変更しない。
- `ExecuteAsync` の `stoppingToken` を Effect の実行入口へ渡す。Effect 内で観測されるトークンとの同一性は要求しない（D7）。
- 停止要求と結果の確定を区別する。Host の停止要求後も、確定済みの `Ok`・`Err`・`Die` を一律に `Interrupted` へ置き換えない。
- `Interrupted` だけから物理的なキャンセル原因を推定しない。中断済み Fiber の `Join` など、ルートのトークンが未キャンセルでも生じる経路がある。
- 結果の観測時に `stoppingToken.IsCancellationRequested` を併せて確認しても、分かるのはその時点の停止要求の有無である。独立した中断と Host の停止が競合した場合の原因は復元できない。観測する時点と、その状態に対する境界の方針を定義する。
- `Die` に入った OCE を例外型だけで正常停止へ変換しない。後始末の失敗など、Host 停止中でも診断すべき Die がある。
- 無効な `default` Effect は入力検証で拒否する。環境構築や結果ハンドラーの失敗も握りつぶさず、H4・D7・D18 の規則で分類する。

Generic Host が OCE を正常停止として扱うのは、`ApplicationStopping` が要求済み、実行 Task がキャンセル状態、例外が OCE、という条件を満たす場合である。「OCE なら常に正常停止」とはしない。[Host の実装](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/Microsoft.Extensions.Hosting/src/Internal/Host.cs)

### H3. 子 Fiber・解放・停止猶予

- **2026-11-04 合意**：初版は Host 標準の停止猶予を尊重し、予算内に後始末が終わる構成を利用者へ求める。Hosting が停止猶予を自動延長したり、後始末完了まで Host の破棄を独自に保留する仕組みは初版に含めない。
- ルートの実行 Task は、Effects が管理する子と解放処理の完了を含む。本体の結果だけを受け取り、後始末を切り離してサービス完了を報告しない。
- Effects の解放はキャンセル不能であり、失敗しても残りの解放を試みる。Hosting が停止トークンを解放へ差し込んだり、独自に失敗の優先順位を変えたりしない。
- Host の停止猶予と、Effect が実際に完了する時刻を区別する。停止待機が猶予で打ち切られても、解放の完了を報告しない。
- `Effect.Timeout` を Host の停止時間の上限として使わない。キャンセルに応じない処理や解放を強制終了する機能は提供しない。
- 利用者は、本体が停止に応じ、解放が有限時間で終わるようにし、Host と実行基盤の停止猶予を設定する。プロセスの強制終了後の解放は保証できない。
- Generic Host の `HostOptions.ShutdownTimeout` はサービスごとの専用予算ではない。確認した .NET 11 の既定は30秒で、利用者の設定を Hosting が変更しない。[HostOptions の実装](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/Microsoft.Extensions.Hosting/src/HostOptions.cs)

### H4. DI の寿命

- **QH5 の設計案**：Hosting がサービス実行ごとに非同期 DI scope を作成・所有し、その scope の `IServiceProvider` を環境の生成関数へ渡す。利用者はその関数で型付きの `R` を構築する。`IServiceProvider` を業務 Effect の万能な環境として渡さない。
- ルートの Err ハンドラーも、その実行の環境を使える間に処理する。ハンドラーが正常に完了した Err は処理済みとして扱い、その Err だけを理由にサービスを再起動しない。ハンドラーの例外・キャンセルの分類は D7 に従い、失敗時の元の Err の保持は下記の Hosting 境界の契約に従う。
- `BackgroundService` は Singleton として登録されるため、Scoped サービスをルートから直接捕捉しない。必要な範囲で明示的な非同期 DI scope を使う。[Microsoft のガイド](https://learn.microsoft.com/en-us/dotnet/core/extensions/scoped-service)
- 環境が使う DI scope は、その環境を使う子と Effects の解放が完了するまで破棄しない。停止待機の期限と、リソースを安全に破棄できる時点を同一視しない。
- scope を保持しても、Host が所有する Singleton などの依存先の寿命は延ばせない。停止猶予超過後に Host 自体が破棄されれば、解放中の処理が破棄済みの依存先を参照しうる。Hosting だけで Host 破棄後の安全性を保証しない。
- DI が所有するリソースと `Resource` が所有するリソースを区別し、同じものを二重に破棄しない。
- 長時間のルート全体で1つの scope を保持するか、処理単位で scope を作るかは用途による。`DbContext` などを長時間保持したり並列共有したりする構成を既定の例にしない。
- **QH5 の設計案**：DI scope の破棄失敗も、実行の最終結果を決める前に本体の解放規則へ統合する。本体の Die と破棄の Die が両方あれば、D18 に従って主を選び、残りを Sequential の付属情報へ保持する。成功・中断の後に破棄が失敗した場合も、破棄の Die を隠さない。
- 本体の結果を公開 Exit へ投影した後、素朴な `await using` の破棄例外だけを Host へ流す形にはしない。環境構築・ルート処理・Err の処理・scope 破棄を同じ実行境界で扱い、既存の `Resource`・D18 の集約を再利用する。正常に処理した Err は消費し、以後の scope 破棄失敗に独自に追加し直さない。
- 環境の生成関数が DI 解決などで失敗した場合、本体と Err ハンドラーは実行しない。確保済みの scope は解放し、生成時の例外・キャンセルと破棄失敗を同じ実行境界で分類・集約する。通常の例外は Die、実行トークンがキャンセル済みの OCE は Interrupted という D7 の規則を使う。
- **2026-11-04 合意**：`onError` が Die で失敗した場合、その失敗を主とし、処理しようとしていた元の Err を Sequential の付属情報として保持する。ハンドラーが同期的に投げても、返した ValueTask が待機後に失敗しても同じ規則にする。元の Err の付属情報も D18 の上限・順序で扱い、scope の解放を続ける。
- Err ハンドラーは解放処理ではない。通常の例外は Die、実行トークンがキャンセル済みの OCE は Interrupted という D7 の分類を使う。成功する前に中断した場合もエラー観測中の失敗として元の文脈を扱うが、公開 Interrupted が付属情報を運ばない制約は変えない。元の Err はこの公開結果から取得できず、付属 Die の診断報告の対象でもない。後からの scope 破棄失敗は D18 で集約する。
- この保持は Hosting の境界ハンドラーの契約であり、一般の `OrElse` の消費規則を変更しない。エラー観測中の失敗として既存の `TapErr` に対応する規則を確認し、処理の成功後に Err を消費する構成を具体化する。単純に `OrElse` の回復先へ非同期処理を渡すだけで保持できるとはしない。
- DI scope 内部で各サービスがどう破棄されるかは DI の契約に従う。scope 1つを解放処理として扱うことから、DI 内部に独自の全件継続保証が加わるとは主張しない。

### H5. 診断ハンドラーの所有権

- **2026-11-04 合意（登録スタイル）**：使いたい診断実装を、Hosting の登録口へ渡して構成する。参考は [Hono SmartRouter の登録例](https://hono.dev/docs/concepts/routers#smartrouter)の実装を渡す形であり、候補の自動選択は要求しない。型名・メソッド名・具体的な DI の受け渡しは未決。
- **2026-11-04 合意（適用範囲と寿命）**：診断の接続先と所有者を Host に揃える。その Host の ILogger を使い、その Hosting が開始した Effect と子へ診断先を引き継ぐ。通常の終了では後始末の診断まで受けてから接続を解除する。別の Host やプロセス全体の設定に影響させない。標準の `ILogger` 経由で ZLogger などを選べる形にする。
- 通常の Hosting 登録・起動・停止では、`EffectDiagnostics.UnobservedFailureHandler` を暗黙に変更しない。これは Host 単位ではなくプロセス全体の設定である。
- 診断実装の登録はアプリが明示的に選ぶ。複数 Host、登録の重複、生成・解除・破棄の所有者、並行呼び出しを定義し、配線と解除を利用者に毎回手書きさせない。
- 明示的な DI 登録だけでは Host ごとの診断分離を保証できない。現 API は実行・Host の識別子をハンドラーへ渡さないため、全診断を登録先1つへ送ることと、発生元 Host へ振り分けることを区別する。
- Host は通常の ASP.NET Core アプリや Generic Host 全体の単位であり、HTTP リクエストや BackgroundService ごとの単位ではない。対象は、まずその Hosting が開始した Effect と子であり、任意の HTTP リクエストの直接実行を自動的に含めない。この分離は未実装で、現在の static ハンドラーを接続するだけでは同じプロセスの別の Effect からも診断が届く。
- 破棄済みの Host のロガーをハンドラーに保持させない。現在の static ハンドラーだけを使った部分的な分離で済ませず、実行ごとの診断先、キャンセル経由の遅れた診断、接続終了時の扱いまで設計する。停止猶予超過後の依存先の寿命は H4・QH7 の制約に従う。
- 停止猶予超過時は、接続解除後に後始末の診断が届く可能性がある。その通知先や、接続終了を観測できる範囲は QH4 で定める。破棄済みの Host の ILogger を使って解決しない。
- 標準の `ILogger<T>` と `ILoggerFactory` は Singleton 登録である。ログの scope と DI の Scoped 寿命は別物であり、Singleton でも Host 破棄後まで有効とは限らない。[Logging の登録実装](https://source.dot.net/Microsoft.Extensions.Logging/LoggingServiceCollectionExtensions.cs.html)

### H6. 構造化ログと ZLogger

- 標準の診断実装は Microsoft の `ILogger` 経路を使う。ZLogger は利用者がログプロバイダーとして選ぶ。初版は ZLogger 専用の登録口・API・状態形式を用意せず、診断実装の注入と標準 ILogger 経由で接続する。
- `ILogger` の構造化ログとして、終了の種類、元の例外、保持されている付属情報の件数・順序・`Relation`、分かる場合の省略件数を扱えるようにする。フィールド名・EventId・レベルは未決。
- フィールド名は実装の式やローカル変数名から自動生成せず、明示的な名前を使う。`Relation` などの値をプロバイダーへ渡す形と、JSON・テキストでの最終的な表現を区別する。
- メッセージの文字列補間、任意のエラー値の `ToString()`、付属情報の JSON 化をログ呼び出し前に強制しない。ログ無効時は不要な整形・列挙を避ける。エラー値を無条件に全文出力しない。
- 標準経路では `IsEnabled` の確認を不要な引数構築や列挙より前に行う。型付きのログ呼び出しを使い、出力側が何もしない対照条件で、Hosting のログ呼び出し自体が不要な引数配列・ボックス化・文字列を作らないようにする。
- ZLogger 専用の呼び出しと標準 `ILogger` の呼び出しは、無効時の評価や状態の寿命が同じとは限らない。特定の独自プロバイダーでの失敗から、すべてのプロバイダーとの互換性を断定しない。[ZLogger の公式説明](https://github.com/Cysharp/ZLogger)
- ログ全体のゼロアロケーションや配送完了は保証しない。Hosting、プロバイダー、出力先、エラー値の形式で割り当てを分けて測る。
- 診断経路は Effect の結果を変更しない（D10）。ログ側の例外で、本来の主失敗を置き換えない。Host 自身の失敗ログと Hosting 独自ログの責務を分け、同じ主失敗の重複出力を避ける。

## 4. Effects 側が保証すること・しないこと

| 項目 | 現 main の公開契約と Hosting への制約 |
|---|---|
| キャンセル | 実行トークンへの要求を伝える。副作用の未実行・ロールバック・期限内終了は保証しない |
| `RunAsync` | `Result<T, E>` を返し、Die と Interrupted は例外として伝える。例外型だけでは両者を区別できない場合がある |
| `RunExitAsync` | 有効な実行の終了結果を `Exit<T, E>` として返す。`default` の引数検証などまで例外がないという契約ではない |
| 停止と成功 | Interrupted は外部処理が未完了だった証明ではない。Ok は外部処理の配送保証ではない |
| 解放 | 子の終了と登録済み解放を待ち、D9・D18 の規則で集約する。無限に待つ可能性は残る |
| 再実行 | 記述は再利用できるが、捕捉した可変状態・環境は複製しない。副作用を一度だけ実行する保証ではない |
| 主が Die の付属情報 | `Die.Suppressed` と `Die.OmittedSuppressed` で保持範囲を取得できる。ログで全件あるように見せない |
| 主が Ok・Err・Interrupted の付属情報 | 公開 Exit は付属情報を保持しない。公開 Exit への変換時に、保持していた付属 Die を本体の診断ハンドラーへ報告する（D10・D18、下記のコード確認）。Hosting が公開結果から復元することはできない |
| 診断通知 | Join しなかった子の Err などを全体共通ハンドラーへ通知する。Host ごとの識別・分離、外部ログの配送・永続化は保証しない |

この表の付属 Die の報告は、測定例の一般化ではなく D10・D18 の既存契約である。現 main の [`Exits.From`](../../src/Moderato.Effects/Internal/Exits.cs) は主が Ok・Err・Interrupted の各分岐で `ReportDefects` を呼び、[`UnobservedFailures`](../../src/Moderato.Effects/Internal/UnobservedFailures.cs) は保持列の Die を診断へ報告する。ログの配送完了や、保持上限で省略した情報の復元まで保証するものではない。

Join しなかった子の Err は診断へ通知される。一方、独立した子の Die は D18 の優先順位で親の主結果に伝わるため、Hosting は診断通知と公開結果の両方を扱う。あらゆる子の失敗が診断通知だけから取得できるとはしない。

Hosting は失敗の分類・順序・保持上限を再実装せず、本体の公開結果を境界で解釈する。数学的な同値性を主張するときも、戻り値だけでなく停止・解放・外部ログなど何を観測するかを明示する。Host へ接続しただけで、C# の任意の副作用に合成則やトランザクション性が成立するとは主張しない。

## 5. 未決事項

実行境界の扱いを次に整理する。Die の異常伝達、Err の型付き必須ハンドラー、Interrupted の観測時の状態に基づく方針は H2 のとおり合意済みである。API の具体的な形は以下の未決事項で決める。

| Effect の公開結果 | Host 境界での扱い・推奨案 |
|---|---|
| `Ok` | サービスの正常終了。勝手に再起動しない |
| `Err` | 合意済み：型付きの必須ハンドラーへ渡す。無言で成功扱いしない |
| `Interrupted` の観測時に `stoppingToken` がキャンセル済み | 合意済み：正常停止として扱う。Host が原因だったとの断定はしない。解放失敗の Die まで無視しない |
| `Interrupted` の観測時に `stoppingToken` が未キャンセル | 合意済み：Host へ異常として伝える。具体的な例外型は未決 |
| `Die` | 合意済み：元の情報を保持し、異常として Host へ伝える。Host の例外処理方針に従い、Hosting が再起動しない |

| 論点 | 推奨案・確認すること |
|---|---|
| QH1. 実行 API と Host への伝達 | 方針は合意済み：`RunExitAsync` で分類し、Die を元情報付きで Host へ異常として伝える。OCE の Die が正常停止へ紛れない例外型、主例外・付属情報・省略件数の公開方法、重複ログの回避を具体化する |
| QH2. ルートの Err | 型付き必須ハンドラーを採用する方針は合意済み。引数・戻り値・非同期対応・キャンセル・ハンドラー自身が失敗した場合の終了規則を決める。入口を Never に限定する方式は採用しない |
| QH3. 独立した Interrupted | 観測時に未キャンセルなら Host の異常、キャンセル済みなら正常停止とする方針は合意済み。観測点を1つに固定する実装と、Interrupted の分類を保持したまま Host へ伝える例外型を具体化する |
| QH4. ログ接続の所有権 | 実装を渡す登録スタイルと、接続先・所有者・寿命を Host に揃える方針は合意済み。実行ごとの診断先の保持・子への伝播・キャンセル経路の報告、生成・解除・破棄の責任、本体 API の拡張、接続終了後の遅れた診断の扱いを具体化する |
| QH5. 公開 API と DI scope | H4・節8の案をレビューする。Hosting が実行ごとの DI scope を所有し、環境生成と Err 処理をその寿命内で行う。scope 破棄も同じ実行境界で集約する。診断は Host の DI から生成・管理できる factory 登録とし、借用した ILoggerFactory は診断実装が独自に破棄しない。具体的な名前・署名と所有権は未決 |
| QH6. ログの形式・性能 | H6 の標準 ILogger・明示フィールド名・無効時の不要な構築回避を実装へ落とす。型付きログの No-op 対照と無効時を測り、Host 境界やプロバイダー・出力先を含む全体の割り当てと分ける。具体的なフィールド名・EventId・レベルと、実装時の測定構成は未決 |
| QH7. 停止猶予超過 | Host 標準の停止猶予を尊重し、予算内に解放が完了する構成を利用者へ求める方針は合意済み。初版は猶予の自動延長や Host 破棄を保留する仕組みを提供しない。未完了を観測できる範囲と QH4 の遅延診断の扱いを具体化する。scope を保持しても root 所有の依存先は保護できず、実時間上限と確実な後始末の両方は約束しない |

QH1 では、Effect の終了分類と Host の終了状態を分ける。停止中の `Die(OperationCanceledException)` を `RunAsync` で投げ直すだけでは、Host の正常停止条件に入る可能性がある。`RunExitAsync` による分類と、Host への異常伝達の両方を採用する。以下は合意した方針と、他の伝達方法で不足する点である。

| Die の伝達方法 | 合意した方針・不足する点 |
|---|---|
| 元の例外をそのまま投げ直す | 非 OCE では標準の経路を使えるが、OCE は停止中に正常停止へ紛れる。例外型の保持だけでは要件を満たさない |
| 非 OCE の例外に元の Die を保持して投げる | OCE の Die を正常停止にしないために採用する方針。主例外・付属情報・省略件数を保持し、Host が異常として観測することを実装時に確認する。具体的な例外型は未決 |
| ログに残して正常に戻る | ログは残るが実行 Task は失敗にならず、Host の例外処理方針に委ねる契約にはならない |
| `StopApplication()` だけを呼ぶ | 停止要求であり、失敗の伝達ではない。すでに停止中の場合の診断も別途必要 |

異常を Host へ伝える場合も、プロセスの終了コード、監視基盤の再起動、ログの永続化まで同一の保証にはしない。Host が何を観測したかと、プロセスがどう終了したかを別々に確認する。

## 6. 受け入れ確認（実装時）

本体の公開 API と実際の Generic Host で確認する。TaskCompletionSource などで順序を制御し、固定の Sleep だけを根拠にしない。測定値は条件付きの証拠として記録し、普遍的な上限にしない。

| 確認 | 観測すること |
|---|---|
| 登録のみ／起動／同期完了／待機あり | 登録だけで動かない。起動で1回実行し、終了後に勝手に再起動しない |
| 停止前からのキャンセル／実行中の停止 | 本体の既定規則を守り、正常停止と独立した Interrupted を区別する |
| Ok・Err の確定後、解放中の停止 | 確定済みの結果を後から Interrupted へ書き換えない |
| Die と停止の競合 | OCE の Die、通常の例外、解放の失敗、子の Die を正常停止として消さない |
| 主失敗と付属情報 | 保持順・Relation・省略件数を区別し、公開結果にない情報を捏造しない |
| onError が同期で投げる／await 後に失敗する | Die と元の Err の Sequential な付属情報が一致する。処理が正常に完了した Err を後から復活させない |
| environment の生成失敗 | 本体と onError を呼ばず、確保済み scope を解放する。生成と破棄が両方失敗しても元の失敗を隠さない |
| 同じ E・R のサービスを2つ登録 | 両方の Effect が実行され、環境はそれぞれの scope に属する |
| 解放が停止猶予を超える | 停止待機の終了と解放の完了を区別し、DI scope を使用中に破棄しない |
| 複数サービス／複数 Host／既存診断設定 | 暗黙の global 上書きなし。明示接続を提供するなら重複・解除・並行変更も確認する |
| ロガーが無効／失敗／Host 破棄 | 不要な整形なし、主失敗の置換なし、破棄済みロガーの保持なし |
| 標準ログ／ZLogger | 構造化データの取得を確認し、ログ経路の割り当てを条件ごとに測る |

要件の議論を経て Issue #372・H1/H2 の実装へ進んだ。具体化した境界の契約は節9に記録する。

## 7. 草案の検証記録

2026-11-04、Claude Code が用意した利用側ハーネスを読み、起草側でも停止中の解放失敗のケースを独立に実行した。SDK は `11.0.100-rc.2.26504.105`、Hosting は `11.0.0-rc.3.26522.148`、Effects は基準 main を pack したローカル検証用の `0.1.0-hosting-bea5343`。パッケージの repository commit が基準 main と一致することも確認した。NuGet の正式リリースや、将来の版の保証ではない。

実際の Host から停止し、本体がキャンセルに応じた後、解放処理が `OperationCanceledException` を投げる。開始の合図は TaskCompletionSource、確認対象は `ExecuteTask` と記録用 ILogger の Error/Critical 出力である。

| 境界のコード | 観測された状態 |
|---|---|
| `RunAsync` を await し、そのまま例外を伝える | `ExecuteTask` は Canceled。Host の Error/Critical ログなし。実行 Task を直接 await すれば、解放処理の元の OCE とメッセージは取得できた |
| `RunExitAsync` を await し、値を受け取って戻る | `Die(OperationCanceledException)` を取得。`ExecuteTask` は RanToCompletion。Host の Error/Critical ログなし |

両者とも、このケースでは `Host.StopAsync` は例外なしで終了した。この戻り方だけでは、実行 Task の分類や失敗の診断を判断できない。前者では Die と Interrupted の区別が例外境界で失われ、後者では区別を取得できるが Host へ異常を伝える責務が残る。QH1 はこの2段階を分けて決める。

同じ版で、開始通知を TaskCompletionSource にした次のケースも双方で実行して確認した。

| ケース | 確認できた範囲 |
|---|---|
| 本体を実行中にキャンセル | `RunAsync` は OCE を投げ、`RunExitAsync` は Interrupted を返す |
| すでに中断した Fiber を保持し、Host 停止要求を待ってから未キャンセルのトークンで Join | `stoppingToken` はキャンセル済みで、Join の結果も Interrupted。Host の停止状態はその中断の原因を証明しない |
| 解放を TaskCompletionSource で保留し、停止猶予を200msに設定 | `StopAsync` が復帰しても実行 Task は未完了。Host の破棄後、解放を再開すると Scoped のテスト用サービスは未破棄だが、それが参照する root 所有 Singleton は破棄済み。その後に解放が完了し、最後に scope が破棄された |
| 停止中の解放の OCE を `RunExitAsync` で受け取り、非 OCE の検証用例外に包んで伝える | 実行 Task は Faulted。既定の StopHost で Host の Error/Critical ログを観測し、この構成では `StopAsync` にも例外が伝わった。例外型は検証用であり、Hosting の公開 API として採用したものではない |

200ms は再現のために設定した値で、終了時間の測定結果や推奨値ではない。DI の寿命のケースは、独自のテスト用サービスの破棄フラグで順序を確認したもの。特定のデータベースやログプロバイダーの破棄後の動作まで確認したものではない。Host のログ出力を観測したことも、外部監視基盤への配送や永続化の保証にはしない。

ログの予備検証は ZLogger `2.6.0` を追加した利用側の隔離プロジェクトで行った。標準 `ILogger.Log<TState>` の独自捕捉プロバイダーで、件数が Int32、先頭 Relation が enum の状態で渡ることを確認した。標準経路を使った ZLogger の JSON 出力でも名前付きの件数フィールドを確認したが、付属情報全体の保存を確認したものではない。

起草側の独立した比較では、同じ失敗データを事前に作り、200回の準備後に同期で2000回呼び、呼出側スレッドの割り当てを測った。外側の `IsEnabled` ガードによる無効時と、`LoggerMessage.Define<int, int, Relation>` の無効時・No-op プロバイダー有効時で、それぞれ追加割り当ては0 B/回だった。例外・Exit の構築、実際のプロバイダー・背景 writer・出力先はこの結果の保証範囲に含めない。プロバイダーが同じ呼出側スレッドで処理する場合、その処理の割り当ても別に生じうる。

有効・無効の対照をプロバイダー0個で比べた初期測定と、破棄済み MemoryStream を別ケースへ再利用した初期の割り当て比較は、結論の根拠に採用しない。

## 8. 利用者コードの形（QH5 の案）

以下の利用例をもとに H2 の API を実装する。現在は公開パッケージには含まれず、H2/H3 のレビュー・利用側検証を経て確定する。登録時は構成を渡すだけで、Effect 本体は起動時に実行する。

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddZLoggerConsole();
builder.Services.AddScoped<WorkerEnvironment>();

builder.Services.AddEffectsHosting(options =>
{
    options.Diagnostics = services => new LoggerDiagnostics(
        services.GetRequiredService<ILoggerFactory>());
});

builder.Services.AddEffectHostedService(
    effect: Worker.Run(),
    environment: scopedServices =>
        scopedServices.GetRequiredService<WorkerEnvironment>(),
    onError: static (error, environment, cancellationToken) =>
        environment.Errors.ReportAsync(error, cancellationToken));

await builder.Build().RunAsync();
```

- `Worker.Run()` は `Effect<Unit, WorkerError, WorkerEnvironment>` を構築する。登録時にその本体を実行しない。
- `environment` は Hosting が作った実行用 scope 内で呼ぶ。class だけに限定せず、関数内で値型の環境を直接作れる形も保つ。生成が失敗しても確保済みの scope は解放し、本体や `onError` を呼ばず、H4 の規則で結果を決める。
- `onError` は型付きの `WorkerError` と、その実行の環境、キャンセル用トークンを受け取る必須の非同期ハンドラーの案である。例では `ReportAsync` が `ValueTask` を返す。環境を破棄する前に完了を待つ。正常完了なら Err は処理済み、Die で失敗したら元の Err を Sequential の付属情報として保持する。キャンセルとその後の scope 解放も H4 の規則に従う。
- 診断実装を作る factory は Host のサービスを使う。factory の生成物を Host の DI として所有・破棄できる登録形にし、配線・解除を利用者に毎回書かせない。診断実装が借りた `ILoggerFactory` や他の DI サービスを重ねて破棄しない。既存インスタンスの借用登録まで初版で用意するかは未決とする。
- 診断の factory は Host の寿命、環境の factory はサービス実行用 scope の寿命である。両方の引数が `IServiceProvider` でも解決できるサービスの寿命は同じとは限らず、診断の factory で Scoped サービスを捕捉しない。
- 診断実装の登録と、サービスの登録を分ける。診断実装の生成時に新しいサービス登録を追加する形にはしない。

この形で、登録コードの量・型推論・DI の寿命・エラー処理を確認してから API を確定する。手動で DI scope の破棄例外を合成するコードや、static ハンドラーの差し替えと復元を利用者へ要求しない。

## 9. H2 の具体化（2026-11-04、レビュー対象）

節5の検討を次の契約で具体化する。公開済みパッケージへの追加やリリースは H3 以降であり、H2 はプロジェクト参照で検証する。

- `AddEffectHostedService<E,R>` は `Effect<Unit,E,R>`、`Func<IServiceProvider,R>`、必須の `Func<E,R,CancellationToken,ValueTask>` を受け取る。同型の登録もそれぞれ独立したサービスになる。登録時に default Effect と null delegate を拒否し、本体や環境 factory は実行しない。
- Hosting は Core の公開 API のみを使う。`TapErr<U>(Func<E,Effect<U,Never,R>>)` で Err を観測し、成功後の `UnwrapOr(Unit.Value)` で消費する。定数の回復 Effect を新たに開始しないので、ハンドラー完了後のキャンセル確認だけで処理済み Err を Interrupted に書き換えない。`OrElse` の既存の消費規則は変えない。
- `Resource.Make` で `AsyncServiceScope` を取得・解放し、その `Use` の内側で環境を構築してルートを実行する。scope 破棄を含めて `RunExitAsync` の最終結果を得る。Host の停止時間を独自に延ばさない。
- `EffectHostedServiceException.Exit` は元の `Die` または停止要求なしで観測した `Interrupted` を保持する。Die の元例外は `InnerException` でも取得できる。元の付属情報・Relation・省略件数は Die に残る。正常な結果を独自にログ出力せず、主の異常ログは Host の標準経路に任せる。
- `AddEffectsHosting(options => options.Diagnostics = factory)` は `IEffectDiagnostics` を Host の Singleton として生成・所有する。省略時は `LoggerDiagnostics` を使う。複数回設定した場合は最後の設定が有効。factory の返却値が null の場合は起動時のサービス解決で拒否する。
- factory が新規生成した診断実装の `IDisposable` / `IAsyncDisposable` は Host の DI が破棄する。既存インスタンスを借用する専用 overload は初版に用意しない。診断実装自身は借りた `ILoggerFactory` を破棄しない。非同期破棄だけを持つ実装を使う場合、Host も非同期に破棄する。
- 接続ゲートは診断実装の呼び出しと解除を直列化する。Host 破棄時に参照を外し、実行が保持する callback が破棄済み診断実装や ILogger を呼ばないようにする。遅れて届いた診断はその時点の `EffectDiagnostics.UnobservedFailureHandler`、未設定なら `Console.Error` へ送る。このフォールバックは Host 分離の範囲外であり、Host 破棄後の外部配送や永続化を保証しない。
- 通常の接続中も global の設定は変更しない。診断の適用範囲はこの登録が開始したルートと子であり、任意の直接実行や HTTP リクエストは含まない。
- 標準 logger のカテゴリーは `Moderato.Effects.Hosting`。EventId 1001 `UnobservedDefect`（Error）は `FailureKind`・`SuppressedCount`・`OmittedSuppressed` と元例外、1002 `UnjoinedError`（Warning）は `FailureKind`・`ErrorType` を持つ。1003 `SuppressedFailure`（Error）は保持順に `SuppressedIndex`（0始まり）・`Relation`・`FailureKind`・`ErrorType` と分かる場合の例外を持つ。`ErrorType` は Failed の `Error?.GetType().Name` であり、値が null または Failed 以外なら null。値の書式化や union の再投影を行わず、型付きエラー値を自動出力しない。無効時は型名の取得や付属情報の列挙をしない（Issue #382）。
- ZLogger を使うアプリは `builder.Logging.AddZLoggerConsole()` などでプロバイダーを選ぶ。初版に専用の Hosting 登録口を追加しない。既知の非互換は呼び出し後に state を読む独自 provider との測定1例であり、すべての provider に一般化しない。

## 10. H3 の出荷物検証（2026-11-04）

- Hosting を別 nupkg / snupkg にし、README・XML docs・SourceLink と repository commit を `tests/verify-packages.py` で検証する。生成器を重複して同梱せず、Core の package から推移的に受け取る。
- Core と Microsoft.Extensions.Logging への参照は NuGet の最小版指定であり、完全一致ではない。上位版を拒否する上限を付けない（[NuGet の範囲規則](https://learn.microsoft.com/en-us/nuget/concepts/package-versioning#version-ranges)）。Hosting.Abstractions は net11.0 の参照フレームワークに含まれるため、冗長な PackageReference を置かない。AddLogging のための Logging 実装 package は維持する。
- ソース上の開発版はリリースの確定ではない。非タグ CI は固有の `-ci.<run>.<attempt>`、ローカル検証も固有の未公開版を両パッケージへ渡す。リリース PR で両方を更新し、タグとの一致を検査してから既存の承認付き公開へ進む。
- SDK は `11.0.100-rc.3.26522.148`。RC2 付属の Native AOT compiler に対する NU1903 を実測したため、[修正済み RC3](https://github.com/advisories/GHSA-p3jp-9v83-76w9)へ更新した。NuGet の監査や IL 警告を抑制しない。
- Hosting のみの PackageReference を持つ利用側で、型付きエラー union の生成と Widen、onError の待機後の失敗と元 Err、成功／失敗と scope 破棄成功／失敗の4組、同型2登録と診断の所有権を検証する。誤用を加えた build が MEFF301 で失敗することも CI で確認する。同じ consumer を linux-x64 Native AOT で発行・実行する。
- `LoggingAllocationTests` は、事前構築した Die（付属情報1件）と Failed を標準 ILoggerFactory の No-op provider に渡す。各1024回の準備後に1024回計測し、有効／無効の4条件すべてで呼び出しスレッドの追加割り当ては0 B。データ構築、実プロバイダー、外部出力の費用は含めない。
- H3時点（ErrorType 追加前）の BenchmarkDotNet 0.15.8、RC3、inProcess の Short job でも同じ4条件を測った。無効時は Die 約3.57 ns／Failed 約4.58 ns、有効時は Die 約56.60 ns／Failed 約23.58 ns、追加割り当ては検出されなかった。実行条件付きの短期測定であり、他のマシンや実プロバイダーの上限ではない。
- 既存を含む全274 benchmark ケースを inProcess の Dry job で実行した。これは実行のスモーク確認であり、速度改善や性能上限の根拠にはしない。全体の性能最適化は Hosting 後の別作業である。
