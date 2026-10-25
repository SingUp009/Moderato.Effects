# Moderato.Mathematics.Random への要件（Moderato.Effects から）

Moderato.Effects の `Schedule.Jittered()`（再試行の待ち時間にばらつきを加える機能）で使う乱数の実装を、`Moderato.Mathematics.Random` に求める。設計の背景は `docs/design.md` の D14 / D15 を参照。`Moderato.Mathematics.Random` は Effects の依存ではなく、利用者が `System.Random` の派生クラスとして環境に入れるものの1つ。

## 依存の向き

- 2つのライブラリは**互いに依存しない**。
  - Moderato.Effects は、.NET の基本クラスライブラリと Moderato.Results にだけ依存する（D14）。
  - Moderato.Mathematics.Random も、Moderato.Effects には依存しない。数学のライブラリが Effect System に依存するのは向きが逆なので。
- 両者は **.NET 標準の `System.Random`** を共通の型としてつながる。
  - Effects 側は、環境に `IHas<System.Random>` を要求する（`Schedule<IHas<Random>>`）。
  - Random 側は、自前の乱数を `System.Random` として渡せるアダプタを提供する。

## 求めるインターフェース

```csharp
namespace Moderato.Mathematics.Random;

// System.Random を継承した、乱数のアダプタ（名前は仮）
public sealed class XxxRandom : System.Random
{
    public XxxRandom();               // 種を自動で決める（本番用）
    public XxxRandom(ulong seed);     // 種を指定する（テストで再現するため）

    // 少なくとも次の2つは、正しく、効率よく動くように override する。
    // Effects の jitter が使うのはこの2つだけ。
    public override double NextDouble();               // [0, 1) の一様乱数
    public override int Next(int minValue, int maxValue);

    // System.Random の残りの仮想メンバーも、一貫した動きになるように override するのが望ましい：
    // Next()、Next(int)、NextInt64(...)、NextSingle()、NextBytes(...)、Sample()
}
```

## 要件

1. **`System.Random` を継承していること**。`IHas<System.Random>` に入れて使えるようにするため。
2. **スレッドセーフであることが望ましい**（必須ではない）。Effects 側は、Retry を1回実行するたびに環境の `Random` から種を1つだけ（lock した上で）取り出し、その Retry 専用の `Random` を作って使う（design.md の D15）。そのため、スレッドセーフでない実装を入れても壊れない。ただし、利用者が同じインスタンスを Effects の外でも並列に使う可能性はあるので、スレッドセーフであるほうが安全。ロックなしでスレッドセーフにできる方式（種とカウンタをハッシュするカウンタベースの方式で、カウンタを `Interlocked.Increment` で進める、など）が望ましい。
3. **種を指定すれば再現できること**。テストで jitter の値を固定するため。ただし並列実行では呼び出しの順番が決まらないので、「同じ種なら、同じ順番で呼べば同じ列になる」ことまでが要件。
4. **範囲を指定した乱数に偏りがないこと**。`Next(min, max)` は剰余（`%`）ではなく、Lemire の方法や棄却法などで偏りを避ける。参考：Unity 版の `Moderato.Random.Range` は剰余を使っているため、わずかに偏りがある。
5. **ビット演算の型に注意すること**。参考：Unity 版の `Next(uint)` の `state >> 59` は `uint`（32ビット）に対するシフトなので、C# ではシフト量が下位5ビットに丸められ、実際には `>> 27` として動く。PCG の出力変換をそのまま使うなら、状態は 64 ビット（`ulong`）で持つ。
6. （任意）**独立した乱数の列に分岐できること**（split / fork）。今の Effects では必須ではない。将来、Fiber ごとに乱数の列を分けたくなったときに使う。

## Effects 側での使い方（参考）

```csharp
[Environment]
public sealed partial record AppEnv(TimeProvider Clock, [Expose<System.Random>] XxxRandom Random);

effect.Retry(Schedule.Exponential(TimeSpan.FromMilliseconds(100)).UpTo(3).Jittered(),
             when: e => e is TimeoutError);   // R に IHas<TimeProvider> と IHas<Random> が要求される

// テスト
var env = new TestEnv(new FakeTimeProvider(), new XxxRandom(seed: 42));
```
