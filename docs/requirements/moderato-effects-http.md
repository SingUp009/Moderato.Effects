# Moderato.Effects HTTP 連携の要件（草案）

ASP.NET Core のエンドポイントで Effect を実行するための議論用草案。パッケージ名・公開 API は未決。2026-11-05 は要件を整理し、製品実装は .NET 11 GA 後に進める。

本体の確認基準は main `614518d771fc1e5dbf0fc2ad71f55657d616aa5c`。[設計メモ](../design.md) の D5〜D10・D17・D18 に従う。追加の soak は利用者が帰宅後、常時起動できる PC で行う。

**2026-11-05 合意した原則**：既定が最速・最善の構成となることを目指す。通常利用で最適化用オプションを探すことを要求せず、正しさ・情報保持・寿命の契約を守ったうえで不要な処理を除く。速さは測定で確認し、任意の構成・プロバイダーで最速または割り当てゼロと断定しない。詳細追跡やスタック採取は通常利用の必須コストにしない。

## 1. 依存の向き

- HTTP パッケージから Effects 本体と ASP.NET Core の標準機構へ依存する。本体や業務の Effect に HTTP を要求しない。
- 環境 `R` は通常の DI サービスとして登録する。リクエストスコープの依存先を含む環境も Scoped とし、`IServiceProvider` を包む万能な環境は作らない。
- Hosting の公開契約や既存の診断機構は再利用できるか確認するが、内部実装への依存を前提にしない。
- Tracker は [設計メモ](../design.md) の Q11 にある本体共通の追跡機能として検討する。HTTP 固有の追跡器を重複実装せず、将来のリクエストとの関連付けを別に検討する。現行の失敗診断の接続だけで実行中の一覧を取得できるとは説明しない。HTTP v1.0 の登録で Tracker を自動的に有効にするという合意ではない。

## 2. 解決したい課題と範囲

標準のルーティングと型付き HTTP 応答を保ちながら、実行トークン、診断、終了分類をリクエストへ接続する。

`MapGet`・`MapPost`・`MapGroup`、認可、引数バインド、フィルターは標準機構を使う。独自ルーター、エラー型からの暗黙のステータス選択、自動再試行、バックグラウンド配送は初版の要件に含めない。

**2026-11-05 合意**：HTTP 連携の v1.0 は通常の値を応答へ変換する経路を対象とする。ストリーミング対応は次バージョンへ送る。Stream・SSE・非同期列挙など、本文送信中の実行やリソース寿命の管理を HTTP パッケージが担う機能は v1.0 に含めない。標準 ASP.NET Core のストリーミング機能そのものを禁止・変更する意味ではない。

## 3. 求める振る舞い

- **利用者の選択**：実行結果の Ok・Err をエンドポイントの switch で HTTP 応答へ変換する形を基本例とする。同じ業務エラーを文脈ごとに別の応答へ写せるようにする。
- **利用者の選択**：OpenAPI は初版から対象とする。応答の実際のステータス・本文型と公開するメタデータを一致させる。
- **2026-11-05 合意**：HTTP 連携の既定構成は `AddEffectsHttp()` の DI 登録1行で利用できる形にする。登録名は既存の AddEffectsHosting と語順を揃える。オプションは具体的な用途が確認できた場合に検討し、用途のないオプション型・設定用オーバーロードを先に用意しない。業務環境や業務サービスの DI 登録まで自動化する意味ではない。
- 型付き `Results<...>` と `TypedResults` による標準メタデータを第一候補とし、独自のエラー列挙・生成器を必須としない。認可など型だけでは記述できない応答も確認する。
- 認証・認可の意味を metadata の都合で変えない。Forbid を Unauthorized へ置き換えたり、認証スキームへの委譲を固定ステータスへ変更したりすることを、OpenAPI 対応の必須手順にしない。必要な明示 metadata はアプリの実際の認証構成に合わせる。
- ルートの Effect を実行し、本体が管理する子 Fiber と登録済みの解放を含む完了を待つ。キャンセルを理由に待機だけを打ち切って成功したように戻らない。
- `RequestAborted` を実行入口へ接続する。結果確定後の要求で Ok・Err・Die を一律に Interrupted へ置き換えない。
- 公開入口で Result を返す場合も、内部では `RunExitAsync` による4分類を保つ案を検討する。Ok・Err のみを利用者の switch へ渡し、Die・Interrupted の伝達は境界の方針で定める。Exit を見ることで分類を保てるが、Interrupted の物理的な原因を復元できるわけではない。
- **2026-11-05 合意**：Die は OCE・IOException を継承しない HTTP 境界専用の例外に包んで伝える。元例外を InnerException として保持し、保持された付属情報の順序・Relation・省略件数を取得できるようにする。専用例外は Hosting と揃えて EffectHttpException に統一し、Exit: ExitCase に元の Die または Interrupted を保持する。アプリは標準の共通例外ハンドラーでこれを識別でき、業務の Err は引き続きエンドポイントの switch で扱う。
- 専用例外は、Die の元例外型だけを理由に標準の中断分岐へ入ることを避けるためのもの。常に500応答を返すことや診断ログの配送完了は保証しない。応答の形式・公開情報・ログ方針はアプリが設定する。通常の元例外型への catch は最上位で一致しなくなるため、元例外の参照方法を案内する。本体の RunAsync の伝達規則は変更しない。
- **2026-11-05 合意**：Interrupted は、最終結果を受け取った時点で RequestAborted.IsCancellationRequested を一度観測し、要求済みならキャンセルとして ASP.NET Core へ伝える。Ok・Err の変換は呼ばず、パッケージから業務応答や独自のエラー本文を書かない。未要求なら EffectHttpException で共通例外ハンドラーへ異常として伝える。この場合の Exit は Interrupted、InnerException は null とする。Interrupted だったという分類を保持し、Die へ分類し直さない。
- この判定は Hosting の停止トークンの観測方針と揃える。子の終了と解放を含む完了後に観測し、後からトークン状態が変わったことで判定を覆さない。要求済みなら「その要求が中断の原因だった」と断定する規則ではない。Hosting の正常停止と HTTP のキャンセル伝達は、それぞれの実行基盤に合わせた表現とする。
- 診断先は `EffectRunOptions` で実行ごとに接続する。グローバルの診断設定を書き換えない。終了・破棄・遅れて届く診断に対する接続の寿命を定義する。
- **2026-11-05 条件付き合意**：性能に大きな影響を与えないことを確認したうえで、診断接続をリクエストの寿命に合わせる。中断要求だけでは接続を解除せず、子と解放を含む実行完了を待つ間は通知できるようにする。リクエスト終了時にその所有する接続を解除し、解除後はリクエスト所有のサービスを呼ばず、本体の既存診断経路へ退避する。退避後にリクエスト固有のログ情報が残るとは保証しない。Host 破棄と競合する場合も破棄済みサービスへ通知しない設計を確認する。
- この合意は各 Effect への接続オブジェクトの追加や、独自のログ scope の常時生成を要求するものではない。通常の ILogger の寿命とリクエスト所有の依存先の寿命を区別し、必要な接続・解除の単位とコストを検証する。利用者に解除や退避の配線を毎回手書きさせない。
- **2026-11-05 合意**：既定の失敗診断は標準の ILogger へ接続する。出力先・レベル・ZLogger などのプロバイダーはアプリの通常の Logging 設定で選ぶ。ルートの Err はアプリの switch が扱い、HTTP 連携が自動でエラーログへ出さない。ルートの Die は共通例外ハンドラーへ伝え、HTTP 連携は同じ主失敗を独自に重複出力しない。未観測の子の失敗など、本体が診断として通知するものを既定の ILogger 経路へ送る。
- 診断経路の失敗で本来の実行結果を変更しない。Logging のプロバイダーやログ scope と、診断接続の所有権・解除の寿命は別に定める。ILogger への接続は Tracker・Metrics・Trace の実装を意味しない。
- 既定の診断経路ではログ無効時に不要な文字列整形・付属情報の列挙・任意のエラー値の ToString を実行しない。ロガーの有効判定と出力方式を検証し、診断のためだけに環境・HttpContext・リクエスト本文を完了後まで保持しない。
- エラー値や元例外をそのまま HTTP 本文へ公開しない。公開する情報はアプリケーションが選ぶ。

## 4. 保証すること・しないこと

- Interrupted は物理的な中断原因を保持しない。例外のトークンの一致や観測時の `RequestAborted` の状態だけで、独立した中断との競合を除外できない。
- 本体の通常の Fork は構造化された子として管理される。リクエストを越えて生存することを標準の Fork の性質として説明しない。利用者が別の Task で実行を切り離す場合の寿命は別問題とする。
- Effect の解放完了と HTTP 本文の送信完了は別である。返されたストリームを Effect の解放が閉じてしまう構成を安全だと説明しない。
- 応答開始後に失敗しても、送信済みステータスや本文を取り消せない。切断後のエラー応答の到達、期限内の解放、外部副作用の取り消しは保証しない。
- union の網羅性診断と、その診断でビルドを失敗させる設定を区別する。診断 ID・severity と正負の例を確認する。
- HTTP 応答型の数と業務エラーケースの数は一致しない。複数ケースを同じ応答型へ写せる。`Results<...>` の引数上限をそのままエラーケース数の上限にしない。

## 5. 未決事項

| 項目 | 確認・決定が必要なこと |
|---|---|
| 実行入口 | 登録は AddEffectsHttp() に決定。仮称 EffectRequest の名前、解決方法、実行ごとの状態。環境そのものは保持しない案。オプションは具体的な用途が生じた場合に検討 |
| 境界例外の製品化 | EffectHttpException と Exit プロパティの形は合意済み。製品パッケージ・名前空間・公開 API 記録、GA のミドルウェアでの再確認。試作の型を公開済みとしない |
| 診断の寿命と公開形 | 標準 ILogger 接続とログの責務分担は合意済み。接続解除と既存経路への退避は性能確認を条件に合意。具体的な所有・解除方法、Host 破棄との競合、ログのカテゴリ・レベル・構造化項目、差し替えの必要性 |
| 応答送信 | v1.0 は通常の値が対象。応答開始後の失敗の責務と説明。ストリームの所有権・送信中の実行は次バージョンで設計する |
| OpenAPI | Forbid・Challenge・共通の異常応答などを標準 metadata で補う方法、応答型の上限を超える場合 |
| 書き心地 | 入れ子 union の switch、名前が衝突する Ok 型、型推論、認可あり・なしの実例 |

## 6. 受け入れ確認

- 標準の MapGet・MapPost・MapGroup と Scoped な環境で、認可あり・なしの利用例をコンパイルする。
- エラーケース追加の網羅性診断と、宣言外の HTTP 応答型を返した場合のコンパイル失敗を別々に確認する。
- 実際の OpenAPI 文書で、成功・業務エラー・認可のステータスと本文スキーマを確認する。
- 同期完了・非同期完了の Ok・Err・Die・Interrupted、独立 Interrupted と中断要求の競合、解放失敗、Die(OCE) を確認する。
- リクエスト中断中の Die(OCE)・Die(IOException) も専用例外として共通例外ハンドラーへ届くこと、元例外と付属情報・省略件数を保持することを確認する。応答未開始・開始済みを分け、500応答の到達と異常の識別を同一の保証にしない。
- Interrupted の結果観測時に RequestAborted が要求済み・未要求の両方を確認する。観測後の取消で決定を覆さないこと、どちらも業務の応答変換を呼ばないこと、未要求時の例外から Interrupted の分類を識別できることを確認する。
- 子の終了と解放を待つこと、診断先のリクエスト間分離、ログ無効・失敗・接続解除の経路を確認する。
- 標準 Logging の設定が既定診断に反映されること、ルート Err を自動でログへ出さないこと、共通例外ハンドラーへ渡す主 Die を HTTP 連携が重複して出力しないことを確認する。
- 診断接続が実行・解放中は有効で、解除後はリクエスト所有のサービスを呼ばず既存診断経路へ通知することを確認する。通常の Fork が残留するという前提で試験を構成せず、遅延通知や解除との競合を個別に再現する。
- 性能の条件付き合意は、同じ Effect と応答を使う直接実行の対照と比較して確認する。正常経路・診断発生時、ログ無効・有効、同期完了・非同期完了を分け、時間と割り当てを測る。接続の生成・解除を含む経路と実際の HTTP 経路を確認し、プロバイダーの費用を区別する。大きな差があれば既定構成を見直し、方針を満たしたことにしない。許容差や結果の判断は測定条件とともに記録し、未測定の段階で最速・割り当てゼロを保証しない。
- v1.0 では通常の値を返す経路の応答開始前後の失敗とクライアント切断を確認する。ストリーミングを対応済みとして案内しないことも確認する。有限の検証を無期限の安全性の証明としない。

次バージョンのストリーミング対応では、本文送信までのリソース所有権、送信中の失敗・キャンセル、子と解放の完了を確認する。v1.0 の完了条件には含めない。

## 7. 検証記録

- 製品実装は未着手。実行可能な試作は製品 main から分離した [prototype/http の 0a856d3](https://github.com/SingUp009/Moderato.Effects/tree/0a856d3/prototypes/Moderato.Effects.Http.Prototype) に保存した。試作の確認を全受け入れ条件の完了や正式 API の採用としない。
- 試作は .NET 11 SDK 11.0.100-rc.3.26522.148、Microsoft.AspNetCore.OpenApi 11.0.0-rc.3.26522.148 でビルドし、実HTTPを含む自己検証50項目を確認した。製品の本体・Hosting の変更はない。50項目に最終完了行を加えると PASS の出力は51行になる。
- /orders の201・400・409は標準の型付き Results から生成し、共通ハンドラーの500は ProducesProblem(500) で明示した。Produces 系の宣言と実応答の一致はコンパイラが保証しないため、生成文書と実応答を別途照合する。
- 認可の試作は RequireAuthorization を付ける MapGroup に security と401の operation transformer をまとめ、公開ルートと分離した。業務 Err の JSON 403は Produces<AccessDenied>(403) を個別に明示した。固定ユーザー用ヘッダーは実運用の認証ではない。拒否応答は認証設定・endpoint metadata に依存するため、この例の401を任意の構成へ一般化しない。
- 診断接続の解除・退避は試作で確認したが、singleton のロガーとリクエスト所有の接続の必要性は別に評価する。性能の条件付き合意は未確認であり、実HTTP・非同期・ロギングの全条件の評価を残す。
- 本体の `Effect.cs` の ToResult は Die の元例外を投げ、Interrupted をキャンセルされた ValueTask に変換する。この変換だけでは HTTP 境界で両者を必ず区別できない。製品実装時は、本体の対象コミットでも実行・解放・診断の契約を再確認する。
- ASP.NET Core [ExceptionHandlerMiddlewareImpl](https://github.com/dotnet/aspnetcore/blob/release/11.0/src/Middleware/Diagnostics/src/ExceptionHandler/ExceptionHandlerMiddlewareImpl.cs) の確認時の実装は、OCE または IOException と `RequestAborted.IsCancellationRequested` の組み合わせで中断分岐へ入る。例外内トークンの等価性は調べない。製品実装時は GA の対象版で再確認する。
- [標準の応答型と metadata](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses?view=aspnetcore-10.0) を参考にする。IUnionCase の列挙で曖昧な葉を得られないことを、OpenAPI 導出一般の不可能性や Analyzer 必須の根拠にはしない。
- レビュー担当から、参照 XML 上では ForbidHttpResult・ChallengeHttpResult に PopulateMetadata の宣言がないとの報告がある。これは生成した OpenAPI の検証ではない。標準の Produces による補完、認証スキームの実際の応答、RequireAuthorization がハンドラー実行前に返す応答を、受け入れ確認で照合する。

## 8. 利用者コードの形

次はアプリ側の構成例。業務型・サービス登録は省略している。`EffectRequest` は仮称。登録名 AddEffectsHttp と、EffectHttpException の Exit プロパティは合意済みだが、製品パッケージとしては未実装である。試作の再現手順は節7のリンク先を参照する。以下の文書用の抜粋そのものをコンパイル済みとは扱わない。ASP.NET Core と Moderato.Results の Ok 型は名前を区別する。

```csharp
using Microsoft.AspNetCore.Diagnostics;
using Moderato.Results;
using HttpResults = Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<AppEnv>();
builder.Services.AddEffectsHttp();

var app = builder.Build();
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    if (error is EffectHttpException failure && failure.Exit.Value is Die die)
    {
        app.Logger.LogError(failure, "Effect failed: {FailureType}",
            die.Exception.GetType().Name);
    }

    await TypedResults.Problem(statusCode: 500).ExecuteAsync(context);
}));

var orders = app.MapGroup("/orders").RequireAuthorization();
orders.MapPost("/", CreateOrder);

app.Run();

static async Task<HttpResults.Results<
    HttpResults.Created<Order>,
    HttpResults.BadRequest<ValidationError>,
    HttpResults.Conflict<DuplicateOrder>>>
CreateOrder(CreateOrderRequest command, AppEnv env, EffectRequest request)
{
    var result = await request.RunAsync(Orders<AppEnv>.Create(command), env);
    return result switch
    {
        Ok<Order>(var order) => TypedResults.Created($"/orders/{order.Id}", order),
        Err<CreateOrderError>(var error) => error switch
        {
            ValidationError e => TypedResults.BadRequest(e),
            DuplicateOrder e => TypedResults.Conflict(e),
        },
    };
}
```

例外ハンドラーはアプリ設定の一例であり、HTTP パッケージが自動登録するものではない。標準ミドルウェア自身のログとアプリの明示ログは、アプリで重複を調整する。この LogError は例外オブジェクトを渡すため、プロバイダーによって元例外のメッセージなどもログに含まれる。HTTP 本文へ詳細を出さないことと、ログへ出してよい情報の判断は別であり、ログの公開範囲・機密情報の扱いはアプリで定める。中断未要求の Interrupted も同じ EffectHttpException で届き、Exit.Value is Interrupted で識別できる。例では標準ミドルウェアの例外処理へ委ねている。Problem Details への変換は、応答送信の成功を保証しない。

認可なしの例は通常の app.MapPost へ同じハンドラーを登録する。認可ありでは、この例とは別に通常の認証・認可サービスとポリシーを設定する必要がある。その構成に応じた拒否応答も OpenAPI へ記述する。業務認可を Effect の Err として返す実行可能な例と生成 OpenAPI は、節7の試作に保存した。製品化時は GA と実際の認証構成で再確認する。

登録名は `AddEffectsHttp` とする。Tracker の登録・有効化や具体的なオプションは、この例では決定していない。

認可を ASP.NET Core のポリシーに置く場合と、業務 Effect の型付きエラーに含める場合を分けて例示する。ASP.NET Core の認可でもリソースに基づく判定を表せるため、認可の内容だけで配置を固定しない。上記の RequireAuthorization が業務内の認可を代替するとはしない。switch が長い場合は通常のメソッドへ切り出せる。別の実行入口や専用 DSL を、そのためだけに必須にしない。
