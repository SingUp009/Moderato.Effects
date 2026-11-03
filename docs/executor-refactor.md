# 実行器の移行（Issue #267）

2026-11-03、D1 のデリゲート連鎖から反復実行器へ移行する方針が承認された。この文書は、置換前の観測と受け入れ条件、および E1〜E3 の実装を記録する。Effect と Resource の合成を共通の実行器で進める。

## 範囲

公開 API、定数の直接保持、遅延実行、再実行、同時実行を保つ。中間の値・エラー・環境はジェネリックな型のまま扱い、各段の Outcome を object に詰めない。命令は共有できる不変の記述、実行中の継続は実行ごとの状態に分ける。

変更は `Effect.Run` だけでは終わらない。`Then`、動的な binder、LINQ、環境変換、Never 変換、Resource の取得と合成が相互に実行を呼び出す。Gen・時間操作・並行操作がこれらを呼び出す境界も確認する。公開 API の追加や、新しいスケジューラーを利用者に要求する変更は含めない。

解放の Interrupted（Q13）、生成可能な union の範囲（Q14）は別の決定であり、この移行では変更しない。

## 守る観測

| 観測 | 受け入れ条件 |
|---|---|
| 値・失敗 | Result / Exit、主例外の同一性、付属情報の順序・上限・省略件数を保つ。既存の失敗を型変換で新しい失敗として採番しない |
| 評価 | 遅延評価、callback の順序と回数、失敗後の短絡、default の検証と callback の null 検証を保つ |
| 同期完了 | 同期で終わる計算を、人工的な pending やスレッド移動で非同期化しない |
| 実行コンテキスト | AsyncLocal の値と変更通知、ExecutionContext の flow suppression、SynchronizationContext の境界を確認する。通常の async/await と ConfigureAwait(false) の規則を保つ |
| 独立性 | 同じ記述の再実行・同時実行で、環境・結果・継続・キャンセル状態を共有しない |
| リソース | 取得順、逆順の解放、各解放が一度だけ実行されること、解放に渡る ExitCase を保つ |
| スタック | 深い Map・AndThen・Provide・Resource.AndThen と、binder が動的に返す深い合成を別プロセスで実行し、スタックオーバーフローしない |
| コスト | 浅い同期経路と深い合成を分け、速度・割り当て・保持メモリを測る。深い合成の継続を保存するための割り当てを、同期完了の維持と混同しない |

スタック安全性の対象は Map・AndThen・Provide・LINQ・回復・Never 変換と Resource の取得合成。利用者が callback 内で直接再帰する C# のコード自体や、Retry・Timeout・Gen などの制御ラッパー自体の任意深さの入れ子は対象に含まない。binder が次の Effect を返す形の再帰は対象に含む。

## 置換前のコンテキスト境界

`tests/Moderato.Effects.Tests/ExecutionContextTests.cs` に公開 API での観測を固定する。TaskCompletionSource で実際の中断を制御し、時間待ちに依存しない。

- 同期の Map・Tap・AndThen とその同期の子は、その呼び出し内で AsyncLocal の変更を引き継ぐ。
- 中断した入力を待つ各演算子は、それぞれが待機時に持っていた文脈で再開する。内側の mapper が変えた値を、すでに待機していた外側へ漏らさない。
- キャンセル可能な RunAsync は、同期完了でも現在の Isolated 境界を持つ。演算子同士の変更は伝わるが、呼び出し元の AsyncLocal は復元される。
- Resource.Use、Resource.AndThen、Gen には同期完了時にも async メソッドの境界がある。Then の同期経路と一律にまとめると意味が変わる。
- 同期の callback は現在の SynchronizationContext 上で実行される。ライブラリ内部の待機後に、入口の SynchronizationContext へ自動的に Post しない。利用者の async callback 自身が文脈を捕捉することは妨げない。

ExecutionContext をフレームごとに復元するだけでは、値が一致しても AsyncLocal の ThreadContextChanged 通知が増える。通知があるコードも、元の同期区間と async 境界を観測できるため、値だけの比較で互換と判定しない。

## 候補方式の検証

本体を変更せず、隔離した試作で比較した。以下は方式を選ぶための限定的な検証であり、製品実装の保証ではない。

| 方式 | 試作で確認できたこと | 判断 |
|---|---|---|
| 型付き継続フレーム | 値型を挟む30万段の Map、異なる型の Bind / Provide、待機した16実行の独立性、NativeAOT。プールを使った試作ではウォーム後の実行割り当ては0バイト | 実行状態を型付きで保持する基本方式とする。無制限の静的プールは採用しない。Resource と文脈の境界は未解決 |
| 浅い既存経路と深い反復実行の併用 | 10万段の Map、動的 Bind、異なる型の合成、複数実行。浅い同期経路の実行割り当ては0バイト | 性能上の候補。二つの実行経路で意味が一致することを先に証明する必要がある |
| 強制 pending のチケットとキュー | 数値の結果と同期完了が一致しても、外側の Map が読む AsyncLocal が変わった | 不採用。既存の async state machine が人工的な待機前の文脈を捕捉してしまう |
| フレーム単位の ExecutionContext.Run | 値・flow suppression・SynchronizationContext の限定的なモデルは再現できたが、ThreadContextChanged 通知回数が変わった | この実装のまま互換としない。同期区間と元の async 境界の扱いを詰める |

全演算子の例外・キャンセル競合、失敗の由来、Resource の完走、実製品での割り当てと速度は、この試作では検証していない。既存の回帰テストと後続段階で確認する。

## 別プロセスの再現

StackOverflowException は同じプロセスで回復できないため、`tests/Moderato.Effects.StackSafetyProbe` は通常のテストホストから分離する。ソリューションでビルドするが、通常のテストケースとしては実行しない。E1 では Effect の修正の検証と、Resource の既知の失敗の再現に使う。

```sh
dotnet build tests/Moderato.Effects.StackSafetyProbe -c Release
dotnet tests/Moderato.Effects.StackSafetyProbe/bin/Release/net11.0/Moderato.Effects.StackSafetyProbe.dll map 100 sync
dotnet tests/Moderato.Effects.StackSafetyProbe/bin/Release/net11.0/Moderato.Effects.StackSafetyProbe.dll map 100000 sync
```

引数は `<操作> <深さ> <sync|pending>`。操作は `map` / `bind` / `recursive-bind` / `provide` / `query` / `recover` / `conversions` / `resource` / `resource-map` / `resource-query` / `recursive-resource` / `resource-conversions` / `scope`。`map` は1段ごとに int → 値型 → int の2回の Map を行う。`recursive-bind` は binder が次の合成を返す右側の再帰、`recover` は OrElse による動的な回復、`query` は LINQ、`conversions` は遅延された Never 変換を調べる。Resource の各操作も対応する合成を調べ、`scope` は深い Resource を Gen で取得する。全ケースで異なる環境の2実行と再実行の結果・評価回数を確認し、Resource では実行ごとの逆順解放も確認する。pending はゲートを開く前に2実行が両方中断したことを確認する。

2026-11-03、Linux x64 / .NET SDK 11.0.100-rc.2.26504.105、置換前の本体 `2d7eb52` での結果：

| 深さ | 同期 | 実際に中断する入力 |
|---|---|---|
| 100 | 5ケースすべて成功 | 5ケースすべて成功 |
| 100,000 | 5ケースすべて Stack overflow で子プロセス終了 | 5ケースすべて Stack overflow で子プロセス終了 |

10万段は最低限の回帰テストの目標値であり、それを超える合成に固定の上限を設ける設計ではない。OS の core dump を無効にし、標準エラーをファイルへ送った子プロセスで再現した。大きなスタックトレースを通常のテスト出力へ流さない。修正後は終了コード0と各自己検証の成功を CI から必須にする。

## E1 の実装

Effect の表現は、定数の Result と実行デリゲートの2フィールドを維持する。合成のデリゲートの Target は内部の EffectInstruction とし、直接実行と反復実行の入口を持たせる。浅い合成には深さ予算64を渡して従来と同じ同期経路を使い、予算を使い切ると EffectExecutor のループへ移る。

- ThenInstruction は Map・MapErr・Tap・Attempt などの終端の変換を扱う。
- BindInstruction は AndThen・AndThrough・OrElse・Catch・CatchCase・LINQ の選択と射影を扱う。binder の返した Effect を実行器から直接 enter するため、動的な右側の合成でも予算をリセットして再帰し続けない。
- ProvideInstruction は環境の変換と WidenEnv を扱う。環境を作る順序を保ち、透明な転送では新しい async の文脈を作らない。
- 継続の値・失敗は EffectFrame<T, E> に型付きで保持する。共有の実行状態・無制限の静的プール・結果ごとのボックス化は導入しない。

同期のループは、実際に成功完了していない ValueTask に到達するまで async メソッドを呼ばない。待機するときは、各継続の捕捉済み ExecutionContext を保存する。流れが抑止されて Capture が null の場合、再開側の文脈を引き継ぐ。

再開時の文脈を復元するだけでは、AsyncLocal の値は一致しても通知順が異なった。そこで、元の await の入れ子が持っていた復元先も保存し、区間が中断するときと完了するときに逆順に戻す。完了時は外側の awaiter を先に進め、その後で内側の文脈を戻す。AwaitBoundary は実際の ValueTask への継続登録とこの巻き戻しを結び付ける。人工的な pending やスレッド移動は使わない。

E0 の18ケースは、外側に恒等 Map を128段足した経路でも実行し、直接経路と反復経路の両方を確かめる。追加の回帰テストは、複数回の実際の待機、flow suppression、通知と外側 callback の順序、SynchronizationContext の変更、256バイトの中間値、64実行の完了競合、Gen による観測、キャンセル、default の子、失敗の文脈を扱う。

### E1 の再現結果

| 深さ100,000 | 同期 | 実際に中断する入力 |
|---|---|---|
| map / bind / recursive-bind / provide / query / recover / conversions | 全7ケース成功 | 全7ケース成功 |
| resource | Stack overflow | Stack overflow |

Resource の取得・合成は従来の再帰実装のままなので、Issue #267 はまだ閉じない。Gen 内で深い Effect を一度実行する経路はテストするが、Gen や時間・並行操作を何重にも入れ子にした経路の保証は E2 で確認する。

### 割り当てと性能

既存の浅い同期経路の割り当てテストは維持する。深い実行はフレームを実行ごとに割り当て、待機した区間では文脈の復元先と完了通知にも領域を使う。同期完了とゼロアロケーションは別の条件であり、深い合成の割り当てが0だとはしない。

代表的な短い AndThen の BenchmarkDotNet ShortRun（InProcess、同じマシン、.NET 11 RC2）では、移行前116.5 nsに対して150.3 ns、両方とも実行割り当て0バイトだった。約29%の速度低下があり、反復経路だけでなく直接経路の選択・射影も最適化対象となる。Issue #307 で追跡する。この1ケースをライブラリ全体の倍率とはみなさず、E3 で深さ・値型サイズ・同期／非同期を分けて評価する。

NativeAOT は、本体と Moderato.Results のビルド済み DLL を参照する独立した consumer で、同じプローブソースをネイティブ化した。Effect の10万段の全14経路が成功した。Source Generator の netstandard2.0 プロジェクトへ PublishAot をグローバル指定する直接 publish は SDK エラーになるため、この検証には使わない。パッケージ経由の検証は通常の CI の package job で別途行う。

BenchmarkDotNet Dry の全246ケースには結果が得られ、実行不能なケースはなかった。Dry は動作確認であり、性能比較には使わない。詳細な性能・保持メモリの評価と、深いプローブの CI 必須化は E3 に残す。

## E2 の実装

Resource の取得を `Effect<T, E, ResourceEnvironment<R>>` として表現し、E1 の実行器へ接続した。ResourceEnvironment は既存の環境と解放先を内部で束ねる型で、公開の R と必要なサービスは変わらない。ResourceAcquisition がその記述と元の3引数の取得デリゲートをつなぐため、Resource 自体の1フィールドの表現は維持する。Fiber の取得など、既存のデリゲート経路も内部で接続できる。

Map・MapErr・Never 変換は ThenInstruction、AndThen・LINQ は BindInstruction を使う。Make は取得成功の直後に元の値と解放関数を登録する。Use・Scope.Acquire・Releases の解放ループと失敗集約の規則は変更しない。

### 同期完了時にも必要な隔離

Resource の AndThen・LINQ は、従来から無条件の async メソッドだった。Effect の通常の Bind と同じ条件付きの隔離に変えると、同期の取得や binder が変更した AsyncLocal と SynchronizationContext が外へ漏れる。

そのため、Resource の Bind には内部の隔離モードを使う。直接経路は元と同じ1つの async メソッドで取得・次の取得・射影を進める。反復経路は IsolatedFrame が入口の文脈を保存し、同期完了時、または最初の中断を外側へ返す前に復元する。後者では、外側の await が復元後の文脈を捕捉するよう、実行器の待機者の走査を更新する。すでに再開した区間の巻き戻しは E1 の処理を使う。

元の Resource の規則に合わせ、次の取得の同期例外・完了済みの失敗タスク・実際の待機後の例外をすべて Outcome に変え、先の取得の文脈を引き継ぐ。通常の Effect の Bind の規則は変えない。

### E2 の検証範囲

- 10万段の Resource.AndThen、Map、LINQ、動的に返す Resource、Never 変換、Gen での取得が、同期・中断ありの両方で成功した。Effect の既存14経路と合わせ、JIT・NativeAOT の全26経路を確認する。
- 取得途中の Err・Die・Interrupted、Timeout による中断、default の子、256バイトの射影値、解放が複数回失敗する場合を検証する。取得済みの値は逆順に一度ずつ、キャンセル不能なトークンと確定済みの ExitCase で解放する。
- 直接経路と、Resource の内側を反復経路へ移した実行を比較する。1回目・2回目の取得の同期／非同期、flow suppression の全組み合わせで、binder・mapper・利用本体・解放の文脈を比べる。
- 深い Resource を Gen・Timeout・All・Race・Fork から実行し、Gen → Fork → Join を Timeout で囲む形も確認する。Race で開始前に中断された敗者の取得数は固定せず、実際の取得と解放を照合する。

この境界検証は、Gen や Timeout のラッパー自体を10万段にするなど、すべての制御操作の任意深さの入れ子を保証するものではない。利用者が callback 内で直接行う C# の再帰も変換しない。

### 残る作業

E3 では深いプローブを CI で必須にし、性能・保持メモリを評価する。E1 で記録した直接 Bind の速度低下（Issue #307）に加え、ResourceAcquisition と命令の構築コストも測る。深いフレームは実行ごとに割り当てられ、浅い経路で割り当てを維持したことを全経路のゼロアロケーションとはみなさない。Q13 の Interrupted を返す解放の方針、Q14 の union の範囲はこの段階では変更していない。

## E3 の出荷前検証

`verify.sh` を CI の test job と package job から実行する。14種類×同期／中断ありの28ケースを深さ10万で確認し、制御ラッパーの入れ子は別の4ケースを深さ2,048で確認する。各子プロセスは60秒で打ち切り、失敗時の出力は6,000バイトまでとする。JIT と、実際に pack した nupkg を参照して publish した NativeAOT consumer の全32ケースが成功した。スクリプトが失敗終了を CI に返すことも確認した。

`retention` は記述を生かしたまま、異なる環境で Effect を2回、Resource を1回実行する。完了後に実行スレッドを終了し、環境への WeakReference がすべて回収されることを、同期・中断ありの両方で確認する。記述の寿命まで実行環境を保持しないことの検証であり、あらゆる値や callback の保持を網羅するものではない。

Release の全1,781件、空白・スタイル検証、サンプル、パッケージ consumer が成功した。BenchmarkDotNet Dry は新設24ケースを含む全270ケースが完走した。公開 API は変更していない。wiki の修正は [適用前のパッチ](wiki-patches/executor-and-failure-contracts.patch) として用意した。先行する失敗契約の修正も含み、wiki 自体にはまだ公開していない。

### 浅い実行の速度

AndThen の直接経路は、選択結果に含まれる未使用の終端 Outcome のコピーを避ける。深い経路の選択・射影・フレームは既存の BindInstruction を共有する。同期例外の置換、非同期の待機、default・delegate の検証は維持する。

同じ Linux x64 / .NET 11 RC2、BenchmarkDotNet 0.15.8、ShortRun / InProcess、各3回の測定結果。比較元は実行器導入前の `2d7eb52`。別プロセスで順に測定した。

| ケース | 導入前 Mean ± Error（99.9%） | E3 Mean ± Error（99.9%） | 実行割り当て |
|---|---:|---:|---:|
| Map | 91.94 ± 4.855 ns | 88.73 ± 4.066 ns | 両方0 B |
| AndThen | 130.56 ± 14.442 ns | 137.69 ± 6.310 ns | 両方0 B |
| 合成チェーン・成功 | 169.7 ± 52.02 ns | 153.6 ± 3.22 ns | 両方0 B |
| 合成チェーン・失敗 | 175.4 ± 10.19 ns | 157.9 ± 25.31 ns | 両方0 B |

AndThen の平均差は約5.5%。E1 時点の約29%から改善したが、測定時期が異なり、今回の信頼区間も重なる。導入前と同速になった証明や、全経路の速度改善とはしない。Issue #307 は残す。

### 構築と実行の割り当て

`ExecutorStorageBenchmarks` は深さ1・64・1,024・100,000で、Map・AndThen・Resource の構築と、構築済み記述の実行を分ける。GlobalSetup は公開 API の結果も検証する。100,000段の ShortRun の割り当ては、構築が約11.2 / 12.8 / 64.0 MB、実行が約19.2 / 45.6 / 97.2 MBだった。

InvocationCount=1 の InProcess 診断は小さい割り当てを正確に比較する用途に向かないため、同じ benchmark メソッドをウォームアップし、`GC.GetAllocatedBytesForCurrentThread` の呼び出し前後の差も2回確認した。

| 100,000段 | 構築1回 | 構築済み記述の実行1回 |
|---|---:|---:|
| Map | 11,200,000 B | 19,187,720 B |
| AndThen | 12,800,000 B | 45,570,560 B |
| Resource.AndThen | 64,000,312 B | 97,241,064〜97,241,192 B |

Map / AndThen の1段・64段の実行は同じ計測で0 B。Resource は取得・解放の管理領域を含み、64段で6,744 B、1,024段で951,384 Bだった。深いフレームの領域は段数に応じて増える。再利用で省けるのは構築コストであり、深い実行の割り当ては残る。これを減らすための共有プールや利用者向け容量設定は追加していない。

### #308 のレビューで見つかった制御ラッパーの退行

Retry / Timeout 自体は再帰する。既存のラッパーが呼ぶ2引数の Run を導入前の直接転送に戻し、余分な深さ判定のフレームを除いた。命令同士が呼ぶ3引数の Run の深さ予算は維持する。

レビューと同じ、成功する定数を Retry、または Timeout→MapErr で重ねる形を別プロセスで各3回測定した。比較元は `2d7eb52`。以下は同じマシンでの観測点であり、移植可能な最大深さではない。

| 操作・深さ | 導入前 | E3 |
|---|---|---|
| Retry・4,376 | 3/3成功 | 3/3成功 |
| Retry・4,450 | 3/3成功 | 3/3成功 |
| Retry・4,562 | 3/3 overflow | 3/3 overflow |
| Timeout→MapErr・4,376 | 3/3成功 | 3/3成功 |
| Timeout→MapErr・4,450 | 3/3成功 | 3/3 overflow |
| Timeout→MapErr・4,562 | 3/3成功 | 3/3 overflow |

Retry の報告された退行は回復した。Timeout の低下は未解決で、Issue #310 に再現と受け入れ条件を残す。タイマー・キャンセル・文脈の境界を変える強制 pending やスレッド移動は採用しない。E2 の境界テストや中核の10万段成功を、制御ラッパー自体の任意深さの保証に読み替えない。Q13・Q14 も未決のままとする。
