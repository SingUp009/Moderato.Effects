# 実行器の移行（Issue #267）

2026-11-03、D1 のデリゲート連鎖から反復実行器へ移行する方針が承認された。この文書は、置換前の観測と受け入れ条件を記録する。ライブラリ本体の置換はまだ行っていない。

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

スタック安全性の対象は組み込み演算子による合成。利用者が callback 内で直接再帰する C# のコード自体を変換する保証は含まない。binder が次の Effect を返す形の再帰は対象に含む。

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

StackOverflowException は同じプロセスで回復できないため、`tests/Moderato.Effects.StackSafetyProbe` は通常のテストホストから分離する。ソリューションでビルドするが、現時点では既知の失敗を再現するための実行プログラムであり、通常のテストケースとしては実行しない。

```sh
dotnet build tests/Moderato.Effects.StackSafetyProbe -c Release
dotnet tests/Moderato.Effects.StackSafetyProbe/bin/Release/net11.0/Moderato.Effects.StackSafetyProbe.dll map 100 sync
dotnet tests/Moderato.Effects.StackSafetyProbe/bin/Release/net11.0/Moderato.Effects.StackSafetyProbe.dll map 100000 sync
```

引数は `<map|bind|recursive-bind|provide|resource> <深さ> <sync|pending>`。`map` は1段ごとに int → 値型 → int の2回の Map を行う。`recursive-bind` は binder が次の合成を返す右側の再帰を調べる。全ケースで異なる環境の2実行と再実行の結果・評価回数を確認し、Resource では実行ごとの逆順解放も確認する。pending はゲートを開く前に2実行が両方中断したことを確認する。

2026-11-03、Linux x64 / .NET SDK 11.0.100-rc.2.26504.105、置換前の本体 `2d7eb52` での結果：

| 深さ | 同期 | 実際に中断する入力 |
|---|---|---|
| 100 | 5ケースすべて成功 | 5ケースすべて成功 |
| 100,000 | 5ケースすべて Stack overflow で子プロセス終了 | 5ケースすべて Stack overflow で子プロセス終了 |

10万段は最低限の回帰テストの目標値であり、それを超える合成に固定の上限を設ける設計ではない。OS の core dump を無効にし、標準エラーをファイルへ送った子プロセスで再現した。大きなスタックトレースを通常のテスト出力へ流さない。修正後は終了コード0と各自己検証の成功を CI から必須にする。
