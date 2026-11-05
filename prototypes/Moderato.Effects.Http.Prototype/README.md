# HTTP 連携の試作

**製品コードではない。正式な API・NuGet パッケージとして採用するものではない。**

理想の利用形 `AddEffectsHttp()` → 標準 `MapPost` → `request.RunAsync(effect, env)` → エンドポイントの switch を、実際の Effects 本体で動かす試作。本体・Hosting・ソリューション・CI は変更しない。

## 起動と OpenAPI

リポジトリルートで、global.json に適合する .NET SDK を使う。

```sh
dotnet run -c Release --project prototypes/Moderato.Effects.Http.Prototype
```

表示されたローカル URL の `/openapi/v1.json` が生成された OpenAPI 文書。例えば通常のポートが5000なら、次で取得できる。

```sh
curl http://localhost:5000/openapi/v1.json -o openapi.json
curl http://localhost:5000/orders -H 'Content-Type: application/json' \
  -d '{"sku":"sample","quantity":2}'
```

最初の注文は201、同じ SKU は409、不正な数量は400。保存先はメモリだけで、再起動で消える。認証・永続化は実装していない。

生成文書のスナップショットは [generated/openapi.json](generated/openapi.json)。自己検証で取得した文書の servers だけを相対 URL `/` に正規化し、一時ポート番号による差分をなくしている。注文の201・400・409は型付き Results から生成し、共通例外ハンドラーの500は標準の ProducesProblem で明示する。検証用 endpoint は文書から除外する。独自のエラー union 列挙や OpenAPI 生成器は使用しない。

## 自己検証

```sh
dotnet run -c Release --project prototypes/Moderato.Effects.Http.Prototype -- --verify
```

空きローカルポートで起動し、実HTTP応答と `/openapi/v1.json` を確認して終了する。生成文書と簡易計測はプロジェクト配下 `artifacts/http-prototype/` に保存する。`/prototype/*` は異常を意図的に起こす自己検証用 URL で、--verify のときだけ登録する。通常起動時には存在しない。

- 201・400・409と本文スキーマ、明示した500、サービスがHTTP引数へ露出しないこと。
- Die(OCE) と独立した Interrupted が共通例外ハンドラーへ届くこと。
- 解放中に request token を取消した Die(OCE)・Die(IOException) が中断へ丸められないこと。これは制御したトークンの競合であり、物理的なネットワーク切断試験ではない。
- 実行中のキャンセルで解放を待つこと、解放がキャンセル不能であること、主失敗・付属失敗・Relation の保持。
- 接続解除後の診断が本体の既存経路へ退避し、結果を変更しないこと。未観測の子のエラー値を ToString しないこと。

簡易計測は、同期完了する定数 Effect の再利用と、DI scope の生成・解決・破棄込みの費用を見る。正式なベンチマークではなく、実HTTPの処理量、非同期経路、有効・無効ロギングの全条件を網羅しない。性能の条件付き合意を満たした証拠にはまだしない。

## 今回確認する設計

- Scoped な実行入口は標準 DI で解決する。HttpContextAccessor を保持し、DI 解決時ではなく各 RunAsync の入口で現在のトークンを読む。その同じトークンを実行に渡し、結果分類時に取消状態を一度観測する。トークンは取消状態のスナップショットではなく CTS と結び付いているため、その後の取消も観測できる。実行中に別のトークンへ RequestAborted を差し替える操作へは追従しない。HttpContext や環境そのものは実行入口へ保存しない。
- 正常・異常の同期完了も async ValueTask 経路で返す。入力・破棄済みの検証は同期的に失敗し得るが、Die・Interrupted の伝達形は実行の同期・非同期で変えない。
- 単一の Result を得る注文処理は Access と FromResult の合成で遅延し、Gen の巻き戻しを通さない。記述を構築しただけでは注文を作成しない。
- RunExitAsync の4分類を保ち、Ok・Errだけを呼び出し元へ返す。Die は元の Die を保持する非OCE・非IOExceptionの専用例外で伝える。
- Interrupted の観測時に request token が要求済みならキャンセル伝達、未要求なら専用の非キャンセル例外を投げる。
- request と Host の診断接続をそれぞれ破棄時に切る。診断通知時のロックは request → Host の順で、破棄はそれぞれの参照を切るだけにする。
- LoggerMessage と IsEnabled を使い、元のエラー値の整形を強制しない。共通の主失敗をHTTP側の診断で重複出力しない。標準ミドルウェアとアプリのログ方針は別途設定する。

## まだ確認していないこと

SDK と OpenAPI は .NET 11 RC3。GA での互換性、NativeAOT、認証・業務認可、実際の切断やHost停止との全競合、異なる複数Host、リクエスト情報の長期保持は未検証。付属情報の保持上限超過を今回の自己検証で再現してはいない。

HttpContextAccessor を使う入口、例外の型名、診断実装は候補であり、採用ではない。接続ロック・AsyncLocal・リクエストごとのオブジェクト生成に費用がある。通常利用で最善の構成にするため、製品化前に比較し直す。ストリーミングとTrackerは対象外。
