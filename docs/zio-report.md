# ZIO の検証

[README](../README.md) の補足として、Scala の [ZIO](https://zio.dev/overview/summary) を調査し、README の論点の根拠と、このプロジェクトの F# のコードに活用できるものを検証した結果を記録する。

1. 結論
2. ZIO の位置づけ
3. 実行ループ: Freer の左ネストを解く
4. 環境: 行の閉じ方と、行に載せるもの
5. 失敗: 3 種類に分ける
6. 中断と構造化並行性
7. レイヤー: 順序が意味を持たないハンドラー
8. コスト
9. F# のコードへの活用
10. 未検証の事項

調査の対象は ZIO 2.1.27 のドキュメントと、ソースのコミット [54cc16d](https://github.com/zio/zio/tree/54cc16d47b97c28e47b9d8b22f9991ab9ec83396)（2026-10-30）である。**ZIO は実行していない。** ZIO の挙動についての記述はドキュメント、ソース、テストを読んだ結果で、Scala のコードはそこからの引用か、それに沿った説明用の断片である。`Eff.bind` の節、`Rio.provideSome`、`transaction`、`scoped`、失敗の型 `E` は本体とサンプルにある。それ以外の試作は採用していないのでリポジトリにはなく、要点のコードをこの文書に載せる。試作のコードは、動くことを確かめたものからの抜粋である（書式などを一部変えている）。

## 1. 結論

ZIO から得た論点は README に載せている。F# のコードには、Freer の `bind`、`Rio.provideSome`、トランザクション、能力の寿命、失敗の型のサンプルを取り入れている。

ZIO で利用者が足せる操作は末尾で 1 回のもの（サービス）に限られ、継続を扱うハンドラーはないので、エンジンの置き換えにはならない。一方、継続をランタイムが握ることで得ているもの（中断、リソースの保証、失敗の分類）は、ハンドラーのモデルだけでは出てこない論点である（[README の課題 8](../README.md#課題-8-失敗と中断を分けられない)）。

| 対象 | ZIO の手法 | F# で確かめた結果 | 判断 |
|---|---|---|---|
| **実行ループ** | `flatMap` を節にして、ループで展開する | 左ネストのコストが深さに比例しなくなる。`perform` に続く `let!` は節を作らないので、`fn` と `ctl` の経路のコストは増えない | **本体に入れている** |
| **環境の局所処理** | `provideSomeLayer[R0]` | Rio でも、残りの行を列挙すれば親の行から能力を消せる | `Rio.provideSome` を本体に入れている |
| **行に載せるものの基準** | 局所的な能力だけを環境に載せる | | [README の方式の選び方](../README.md#何を行に載せるか)に記載 |
| **失敗の分類** | `E`、欠陥、中断を分ける | `E` は書ける。継続を捨てる失敗は `finally` を飛ばす | [README の課題 8](../README.md#課題-8-失敗と中断を分けられない)に記載。`E` はサンプルにあり、本体には入れない |
| **中断と構造化並行性** | 親子の寿命、中断不可の領域、`Cause` | `Async` に足りないものを確認した。試作のファイバーで同じ意味論が動く | [README の課題 8](../README.md#課題-8-失敗と中断を分けられない)に記載。本体には入れない |
| **ZLayer** | 依存グラフの自動配線 | 共有と解放の順序は `handlers { }` で得られる。自動配線は書けない | 型は作らない。`scoped` の例がサンプルにある |

判断を左右した実測は次の 3 つである。

- **左ネストは型整列キューなしで解ける。** 深さ 4,000 の操作 1 回あたりが約 200〜270 ns、深さ 256,000 でも約 460〜510 ns で、1 MB のスタックで深さ 100 万が完走する。この検証の時点の 149 件のテストは Debug（`Tailcalls=false`）と Release の両方で通る（[3 節](#3-実行ループ-freer-の左ネストを解く)）。
- **「継続を呼ばない」失敗は `finally` を飛ばす。** 同じ計算を 3 通りの失敗で動かすと、継続を捨てる形だけ後始末が走らない（[5.2 節](#52-失敗の表し方と-finally)）。
- **`Async` は寿命と失敗の情報を保たない。** 親が終わっても `StartChild` の子は走り続け、`Async.Parallel` は同時の失敗を 1 つしか残さず、`finally` の例外は本体の例外を消す（[6.2 節](#62-async-との比較)）。

## 2. ZIO の位置づけ

`ZIO[R, E, A]` は、環境 `R` を必要とし、`E` で失敗するか `A` で成功する計算の記述である。組み立てただけでは何も起きず、ランタイムが実行する点は Freer の `Eff<'a>` と同じである。

違いは、木の節が閉じていることである。[実行ループ](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core/shared/src/main/scala/zio/internal/FiberRuntime.scala#L1085)が扱う命令は `Exit`（成功と失敗）、`Sync`、`Async`、`FlatMap`、`Mapped`、`FoldZIO`、`Stateful`、`WhileLoop`、`YieldNow`、`UpdateRuntimeFlags`、`UpdateRuntimeFlagsWithin` の 12 種類である。`ZIO` は `sealed` で、`Exit` 以外の命令は `private[zio]` なので、利用者は命令を足せない。`resume` もない。

利用者の操作は、`ZIO` を返すメソッドを持つ[サービス](https://zio.dev/reference/service-pattern/)である。これは README の能力で `Eff.ret` を返す形（[EffektStyle.fs](../samples/EffectSystem.Samples/EffektStyle.fs) の `askHandlerDirect`）と同じで、ZIO の別名 `RIO[R, A]` はこのプロジェクトの `Rio<'R, 'a>` と同じ役割を持つ。

```scala
trait Ask[V] { def ask: UIO[V] }
ZIO.serviceWithZIO[Ask[V]](_.ask)    // Rio の ask () に当たる
```

[継続の使い方の表](../README.md#継続の使い方が機能を決める)の各行は、ZIO では次のものに当たる。

| `k` の使い方 | 得られる機能 | ZIO での実現 |
|---|---|---|
| 呼ばない | 例外、早期脱出 | `ZIO.fail`。実行ループがスタックを巻き戻す |
| 末尾で 1 回 | 依存性の注入、Reader | `ZIO.service` とサービスのメソッド |
| 途中で 1 回 | 前後処理、トランザクション | `ZIO#ensuring`、`ZIO.acquireReleaseWith` |
| 状態を渡しながら 1 回 | State | `Ref`、`FiberRef` |
| 保存して後で | ジェネレーター、非同期、スケジューラー | `ZIO.async`、`fork`、`ZIO.yieldNow` |
| 複数回 | 非決定計算、バックトラック | **なし** |

利用者に開くのは末尾で 1 回の行（サービス）だけで、残りのうち 4 行をランタイムの命令として持ち、複数回の再開は持たない。これが ZIO の設計の要点である。

この要点は、ハンドラーにどこまでの制御を開くかで並べると分かりやすい。ハンドラーが継続をどこまで扱えるかには段階があり、下の段ほど後始末の責任が利用者に移る。Koka はこの段階を `fun`、`ctl`、`raw ctl` として言語で区別している（[Koka の文書](https://koka-lang.github.io/koka/doc/book.html)）。表は README の設計軸のうち、操作の意味を決めるもの、継続の寿命、再開の回数の 3 つを、後始末の責任の順に並べ直したものである。

| 利用者に開く制御 | 後始末の責任 | 例 |
|---|---|---|
| 末尾で 1 回の操作だけ。継続は見えない | 制御の流れが一本道なので、`finally` は普通に走る | ZIO のサービス、Koka の `fun`、Polysemy の `interpret`、Capability と Rio（`Eff.ret` で答える場合） |
| 継続を節の中だけで使う | 言語が持つ。Koka は、再開せずに終わった `ctl` の節から自動で巻き戻し、`finally` を走らせる | Koka の `ctl` |
| 継続を値として持ち出せる（再開は 1 回） | 利用者が持つ。必ず再開するか、例外で再開する（OCaml の `discontinue`） | OCaml、Koka の `raw ctl`、Direct、Async one-shot |
| 継続を複数回再開できる | 利用者が持つ。`finally` は再開のたびに走るので、二重の解放は利用者が防ぐ。Koka も `initially` と数え上げで手当てしている | Koka、Effekt、Unison、Freer |

ZIO は一番上の段にある。継続を利用者に渡さないので、ランタイムは継続が必ず 1 回で終わることを前提にでき、中断の最中でも後始末を保証できる。下の段ほど、この保証はハンドラーの規律に頼ることになり、複数回の再開では資源を何回解放するかが定まらない。これは .NET に限った話ではなく、利用者に継続を開くかどうかの選択から来る。

[設計軸](../README.md#実装を分ける設計軸)の上では次の位置にある。README の言語の比較表の ZIO の列は、この表を要約したものである。

| 軸 | ZIO |
|---|---|
| 操作の意味を決めるもの | ランタイムの命令。利用者が足せるのは末尾で 1 回の操作（サービス）だけ |
| 継続の表現 | ヒープ上の可変配列 |
| 継続の寿命 | 利用者には渡らない。実行系だけが持つ |
| 再開の回数 | 1。利用者に渡るのは `ZIO.async` のコールバックだけで、[2 回目は無視される](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core/shared/src/main/scala/zio/internal/FiberRuntime.scala#L1729) |
| ハンドラーの範囲 | Deep に当たる。利用者のハンドラーはサービスだけで、環境に置いたまま計算全体で使える |
| 探し方 | 最も近いもの（型タグ） |
| 静的検査 | あり（`R`） |
| 書き方 | モナド（`for`）。[zio-direct](https://zio.dev/zio-direct/) はマクロで `flatMap` へ書き換える |

STM、`ZStream`、`Schedule`、ZIO Test は、エフェクトのモデルから離れるので README では扱わない。

周辺のライブラリとの関係は次のとおりである。

| ライブラリ | 言語 | 型 | ZIO との関係 |
|---|---|---|---|
| [Effect](https://effect.website/docs/requirements-management/services/) | TypeScript | `Effect<A, E, R>` | 同じ 3 つの型引数。`R` は合併型で、`Exclude` で引ける（[4.2 節](#42-行の一部を消す)） |
| [Kyo](https://virtuslab.com/blog/scala/comparing-effect-systems-in-scala-kyo-gears-and-ox) | Scala 3 | `A < S` | ZIO の fused effects を代数的エフェクトの実行系へ一般化した |
| [Orsak](https://github.com/JohSand/Orsak) | F# | `Effect<'r, 'a, 'e>` | ZIO に着想を得た。`'r` はインターフェイス制約で、Rio と同じ形 |
| [FIO](https://github.com/fs-fio/FIO) | F# | `FIO<'A, 'E>` | ZIO と Cats Effect に着想を得たファイバーの実行系。環境の型引数はない |

F# での `'r` の符号化は Bartosz Sypytkowski の[記事](https://www.bartoszsypytkowski.com/dealing-with-complex-dependency-injection-in-f/)（2020）に遡る。Rio のインターフェイス制約は F# で知られた手法である。Orsak と FIO は後から動かして比べた（[Orsak と FIO の検証](orsak-fio-report.md)）。

## 3. 実行ループ: Freer の左ネストを解く

### 3.1 ZIO の実行ループ

[`FlatMap`](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core/shared/src/main/scala/zio/ZIO.scala#L6177) は節で、実行ループはこれをファイバーの持つ配列 `_stack` へ積み、前の計算へ進む。

```scala
case flatmap: FlatMap[Any, Any, Any, Any] =>
  // 抜粋
  stackIndex = pushStackFrame(flatmap, stackIndex)
  cur = first
```

- **`flatMap` は積むだけなので、左ネストのコストが深さに比例しない。** ZIO のテストには `foldLeft` で 1 万段ネストする [`deep attempt`](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core-tests/shared/src/test/scala/zio/ZIOSpec.scala#L2733) がある。
- **継続はヒープにあるが、再開は 1 回に限られる。** `_stack` は可変で、複製しない。[README の設計軸](../README.md#実装を分ける設計軸)の「ヒープ上の可変配列」に当たる。
- **フレームをデータとしてたどれる。** 失敗すると、配列を戻りながら `FoldZIO` を探す。中断の処理もこの走査の中にある（[6.1 節](#61-zio-の中断)）。

### 3.2 Freer への適用

Freer はマルチショットを保つ必要があるので、可変配列は使えない。節にする考え方だけを取り入れ、左ネストはループの中で右へ付け替える。この付け替えは [Bjarnason の手法](https://blog.higher-order.com/assets/trampolines.pdf)と同じである。

```fsharp
type Eff<'a> =
    | Pure of 'a
    | Impure of effect: obj * resume: (Result<obj, exn> -> Eff<'a>)
    | Delayed of (unit -> Eff<'a>)
    | Bound of BindNode<'a>

and [<AbstractClass>] BindNode<'a> internal () =
    abstract Step: unit -> Eff<'a>
    abstract Then<'b> : ('a -> Eff<'b>) -> Eff<'b>

and [<Sealed>] internal BindOf<'x, 'a>(m: Eff<'x>, f: 'x -> Eff<'a>) =
    inherit BindNode<'a>()

    // 1 段だけ進める。
    override _.Step() =
        match m with
        | Pure x -> f x
        | Delayed th -> Bound(BindOf<'x, 'a>(th (), f))
        | Impure(e, k) ->
            Impure(
                e,
                (fun r ->
                    match k r with
                    | Pure x -> f x
                    | n -> Bound(BindOf<'x, 'a>(n, f)))
            )
        | Bound inner -> inner.Then f

    // 左ネストを右へ付け替える。
    override _.Then<'b>(g: 'a -> Eff<'b>) : Eff<'b> =
        Bound(BindOf<'x, 'b>(m, (fun x -> Bound(BindOf<'a, 'b>(Delayed(fun () -> f x), g)))))
```

`bind` は再帰しない。`run`、`tryWith`、`tryFinally`、`deep`、`view` には `Bound` の節が 1 行ずつある。

```fsharp
let bind (f: 'a -> Eff<'b>) (m: Eff<'a>) : Eff<'b> =
    match m with
    | Pure x -> f x
    // 作りたての perform は節を作らずに包む。いちばん多い形で、再帰もしない。
    | Impure(e, k) when obj.ReferenceEquals(k, PerformResume<'a>.Instance) ->
        Impure(
            e,
            (fun r ->
                match k r with
                | Pure x -> f x
                | n -> Bound(BindOf<'a, 'b>(n, f)))
        )
    | _ -> Bound(BindOf<'a, 'b>(m, f))

// deep の場合
| Bound n -> deep retc cases (n.Step())
```

`Impure` の包み方で、コストと fold の安全性が変わる。fold は `Eff.bind` を遅延なしで左へ積む形で、ZIO の `deep attempt` と同じである。コストの幅は 2〜3 回の実行の差である。

| `Impure` の扱い | `fn` | `ctl` | fold、1 MB のスタック |
|---|---:|---:|---|
| 常に包む | 60–76 ns | 129–147 ns | 2 万× |
| 常に節を作る | 77–79 ns | 149–150 ns | 100 万○ |
| **作りたての `perform` だけ包む（上のコード）** | 60–61 ns | 127–132 ns | 100 万○ |

節の進め方には、さらに 2 つの注意が要る。

- **`try` は節をその場で進める。** `tryWith` と `tryFinally` が `Bound` を `Delayed` で返すと、入れ子の `try` を 1 歩ごとに通り直す。各段に `try` がある左ネストでは、深さ 1,000 の実行が約 100 倍遅くなる。
- **`Then` は `f x` を `Delayed` に包む。** その場で呼ぶと、同じ前の計算に足し重ねた節の分だけ再帰する。観測と `bind` を交互に重ねる形で、1 MB のスタックが深さ 16,000 で溢れる。

### 3.3 確かめたこと

| 確かめたこと | 結果 |
|---|---|
| テスト 149 件（この検証の時点） | Release、Debug、Debug（`Tailcalls=false`）のすべてで通る |
| 左ネストの意味論 | 左ネストにマルチショット、`finally`、`Discontinue`、転送、未処理、再実行、`view` の 2 回の再開を組み合わせた計算を [Tests.fs](../tests/EffectSystem.Tests/Tests.fs) で固定している |
| 左ネスト、fold、観測と `bind` の交互、1 MB のスタック | 100 万○（[移植ノートの 4.3 節](porting-notes.md#43-スタック)） |
| `ctl` と `view` のハンドラーの左ネスト、外側の `try` の中の左ネスト | 深さ 100 万が完走する |
| `fn`、`ctl`、末尾の `do!`、1 MB のスタック | 100 万○。Debug（`Tailcalls=false`）でも同じ |
| 各段に `try` がある左ネスト、1 MB のスタック | 千○ / 2 千×。操作が全段の `try` を通るので、コストも深さに比例する |
| 深い位置で止まった同じ継続の繰り返し再開 | 1 回 327–577 ns で、深さ 64,000 まで伸びない |
| 同じ左ネストの値の繰り返し `view` | 1 回の `view` が深さに比例する（深さ 1 段あたり 91–383 ns）。結果を覚えないので、再実行と同じである |
| コスト | [8.1 節](#81-freer-の-bind) |

繰り返しの再開と `view` の 2 行は、リポジトリのコマンドにはない測定である。

`Eff<'a>` の節を直接扱うコードは [Freer.fs](../src/EffectSystem/Freer.fs) の外にない。

## 4. 環境: 行の閉じ方と、行に載せるもの

### 4.1 `R` の実体

`R` は実行時に現れない幽霊型である。[`provideEnvironment`](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core/shared/src/main/scala/zio/ZIO.scala#L1274) は計算を `ZIO[Any, E, A]` にキャストし、環境をファイバーローカルな値として局所的に差し替える。

```scala
final def provideEnvironment(r: => ZEnvironment[R])(implicit trace: Trace): IO[E, A] =
  FiberRef.currentEnvironment.locallyWith(_ => r)(self.asInstanceOf[ZIO[Any, E, A]])
```

環境の実体の [`ZEnvironment`](https://zio.dev/reference/contextual/zenvironment) は、型タグをキーにしたマップである。README の言葉では、StaticRow と同じ幽霊型の行に、Freer と同じ「型で最も近いものを探す」実行時の探索を組み合わせたものになる。

| | ZIO の `R` | Rio |
|---|---|---|
| 実行時の実体 | なし。環境はファイバーローカルなマップ | 関数の引数 |
| 行の表し方 | 交差型 `A & B`。型として書ける | 型変数の制約。型としては書けない |
| 行が集まる仕組み | 反変の `R` と `flatMap[R1 <: R, ...]` | 単一化 |
| 同じ総称型の別インスタンス | 型タグが別なので区別される | FS0001（`rio-two-instantiations.fsx`） |
| 行を閉じる | `provide`。足りない層を[マクロがコンパイル時に列挙する](https://zio.dev/reference/di/automatic-layer-construction) | `Rio.run`。足りなければ FS0193 |
| 行の一部を消す | `provideSomeLayer[R0]`。残りの `R0` を利用者が書く | `Rio.provideSome` と転送クラス。残りをクラスに列挙する |

### 4.2 行の一部を消す

ZIO にも行の引き算はない。[`provideSomeLayer`](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core/shared/src/main/scala/zio/ZIO.scala#L5549) は残りの行 `R0` を型引数で受け取り、`R0` と層の出力の交差が `R` になることを証拠で確かめる。

```scala
final class ProvideSomeLayer[R0, -R, +E, +A](private val self: ZIO[R, E, A]) extends AnyVal {
  def apply[E1 >: E, R1](
    layer: => ZLayer[R0, E1, R1]
  )(implicit
    ev: R0 with R1 <:< R,
    tagged: EnvironmentTag[R1],
    trace: Trace
  ): ZIO[R0, E1, A] =
    self.asInstanceOf[ZIO[R0 with R1, E, A]].provideLayer(ZLayer.environment[R0] <*> layer)
}
```

Effect の [`provideService`](https://github.com/Effect-TS/effect/blob/main/packages/effect/src/Effect.ts) は TypeScript の条件型で `Effect<A, E, Exclude<R, I>>` と計算できる。残りの行の扱いは言語の機能で分かれる。

| | 残りの行 |
|---|---|
| Koka | 行変数として推論する |
| Effect | `Exclude<R, I>` で計算する |
| ZIO | 利用者が `R0` と書く |
| Rio | 転送クラスに列挙する |

ZIO と同じように残りの行を明示すれば、Rio でも局所的に能力を消せる。[Capability.fs](../src/EffectSystem/Capability.fs) の `Rio.provideSome` と、[EffektStyle.fs](../samples/EffectSystem.Samples/EffektStyle.fs) の転送クラスで書く。

```fsharp
// ZIO#provideSomeEnvironment に当たる。
let provideSome (f: 'R0 -> 'R) (m: Rio<'R, 'a>) : Rio<'R0, 'a> = Rio(fun env -> run (f env) m)

// 残りの行を列挙した転送クラス。ZIO の R0 に当たるものがクラスの制約に現れる。
type AskOver<'R, 'v when 'R :> IEmit<'v>>(ask: IAsk<'v>, rest: 'R) =
    interface IAsk<'v> with
        member _.Ask() = ask.Ask()

    interface IEmit<'v> with
        member _.Emit v = rest.Emit v

let askEmitLength () =
    rio {
        let! (v: string) = ask ()
        do! emit v
        return v.Length
    }

let localAsk () =
    let local =
        { new IAsk<string> with
            member _.Ask() = Eff.ret "local" }

    rio {
        do! emit "before"
        let! n = Rio.provideSome (fun env -> AskOver<_, string>(local, env)) (askEmitLength ())
        do! emit "after"
        return n
    }
```

| 確かめたこと | 結果 | 確認 |
|---|---|---|
| 親の行から `IAsk` が消える | `val localAsk: unit -> Rio<#IEmit<string>,int>` | `rio-local-all-provided.fsx` |
| `IEmit` だけを実装した環境で実行する | 通る | `exampleLocalAsk` |
| 子が転送クラスの列挙にない能力を要求する | FS0001 | `rio-local-unforwarded.fsx` |
| 外側の環境が残りの行を満たさない | FS0193 | `rio-local-rest-missing.fsx` |
| 残りを列挙しない転送クラス（開いた行） | FS0001 | `rio-local-open-rest.fsx` |

閉じた行なら書けるが、Koka の `<ask|e>` のような開いた行は書けない。局所処理の箇所ごとに転送クラスが要るので、使うのは少数の箇所に限られる。

Orsak の [`IGrowsInto`](https://johsand.github.io/Orsak/scopes.html) は同じ問題の別解で、転送を環境の側へ移す。`Rio` でも同じ形で書ける。

```fsharp
type IGrowsInto<'Cap, 'Inner> =
    abstract Grow: 'Cap -> 'Inner

let within (cap: 'Cap) (Rio g: Rio<'Inner, 'a>) : Rio<'Outer, 'a> when 'Outer :> IGrowsInto<'Cap, 'Inner> =
    Rio(fun outer -> g (outer.Grow cap))

// val transferInTx: tx: ITx -> Rio<#IGrowsInto<ITx,'b>,unit> when 'b :> ITx and 'b :> ILog
let transferInTx (tx: ITx) = within tx (transfer ())
```

外側の環境は `IGrowsInto` を実装するだけで、外側の行に `ITx` は要らない。代わりに、内側の環境の型が外側の環境のクラスに固定される。

### 4.3 何を行に載せるか

ZIO はこの点で実際の使用から方針を変えており、README の「[何を行に載せるか](../README.md#何を行に載せるか)」の根拠になる。

- **低レベルのサービスを環境から外した。** [2.0.0-RC4](https://github.com/zio/zio/releases/tag/v2.0.0-RC4) で `Clock`、`Console`、`Random`、`System` は環境から外れ、ランタイムへ組み込まれた。低レベルでどのサービスからも使いたいこと、テスト用以外の実装を利用者がほとんど用意しないことが理由である。現在はファイバーローカルな値として差し替える。
- **実装の依存を型に漏らさない。** [環境の 3 つの法則](https://zio.dev/reference/service-pattern/the-three-laws-of-zio-environment)は、サービスのインターフェイスに環境を使わず、実装はコンストラクターで依存を受け取り、環境は業務のロジックがサービスを使うときに使う、と定める。アクセサーのメソッドは[非推奨になった](https://zio.dev/reference/service-pattern/accessor-methods)。
- **環境の最も慣用的な用途は局所的な能力である。** [環境の用途](https://zio.dev/reference/contextual/zio-environment-use-cases)は、局所的で、消す演算子があり、型で追う価値があるものを挙げる。`Scope` は `ZIO.scoped` で消え、トランザクションは `atomically` で消える。ログは同じ文脈情報でも、既定の値で動いて害がないので型に載せない。

| 分類 | 例 | 置き場所 | このプロジェクトでは |
|---|---|---|---|
| 局所的で、消す演算子があり、型で追う価値がある | `Scope`、トランザクション、HTTP リクエスト | 環境 | Rio の行、`Rio.provideSome` |
| サービスの実装の依存 | リポジトリが使う接続 | コンストラクター | 能力を閉じ込めたオブジェクト |
| どこでも使う低レベルのもの | 時刻、ログ、乱数 | ランタイム | 行に載せない |

[移植ノート](porting-notes.md#6-制限と未検証の事項)は `printfn` が型に現れないことを制限に挙げているが、ZIO の方針に照らせば、必ずしも失うものではない。

## 5. 失敗: 3 種類に分ける

### 5.1 ZIO の分類

ZIO は失敗を 3 種類に分け、[`Cause`](https://zio.dev/reference/core/cause) にまとめる。

| 種類 | 意味 | 型 | 回復 |
|---|---|---|---|
| 失敗（`Fail`） | [予期したエラー](https://zio.dev/reference/error-management/expected-and-unexpected-errors) | `E` に現れる | `catchAll` |
| 欠陥（`Die`） | 予期しないエラー、プログラムの誤り | 現れない | `sandbox`、ログ |
| 中断（`Interrupt`） | 外から止められた | 現れない | 止められない。遅らせるだけ |

[`E = Nothing` でも欠陥と中断は起きる](https://zio.dev/reference/error-management/typed-errors-guarantees)。`Cause` は `Both`（並列）と `Then`（順次）で失敗を組み合わせるので、後始末の失敗で本体の失敗が失われない。

README のモデルでは、失敗は「継続を呼ばない」の 1 行で、種類を区別していない。

### 5.2 失敗の表し方と `finally`

同じ計算を 3 通りの失敗で動かした。

| 表し方 | 失敗の型 | `finally` |
|---|---|---|
| 継続を呼ばない（`Eff.ctl` で `resume` を捨てる） | 操作の型。付け忘れは実行時 | **走らない** |
| `Eff.ctlK` の `Discontinue` | `exn`。型に現れない | 走る |
| `E` のチャネル（[ZioStyle.fs](../samples/EffectSystem.Samples/ZioStyle.fs) の `Zio`） | `'E`。コンパイル時に分かる | 走る |

1 行目は [Advanced.fs](../samples/EffectSystem.Samples/Advanced.fs) の `finallySkippedWhenAbandoned` で固定している挙動である。README の表が例外の例に挙げる形が、リソースを持つ計算では後始末を飛ばす。型付きの失敗を「継続を呼ばない」で表すと、この問題を避けられない。

### 5.3 `E` を F# で表す

`E` は Rio に型引数を足せば書ける（[ZioStyle.fs](../samples/EffectSystem.Samples/ZioStyle.fs)）。失敗を値として計算の中を戻すので、`finally` の区間を通る。

```fsharp
type Zio<'R, 'E, 'a> = Zio of ('R -> Eff<Result<'a, 'E>>)

let bind (f: 'a -> Zio<'R, 'E, 'b>) (m: Zio<'R, 'E, 'a>) : Zio<'R, 'E, 'b> =
    Zio(fun env ->
        run env m
        |> Eff.bind (function
            | Ok x -> run env (f x)
            | Error e -> Eff.ret (Error e)))

let catchAll (h: 'E -> Zio<'R, 'E2, 'a>) (m: Zio<'R, 'E, 'a>) : Zio<'R, 'E2, 'a> =
    Zio(fun env ->
        run env m
        |> Eff.bind (function
            | Ok x -> Eff.ret (Ok x)
            | Error e -> run env (h e)))
```

| 確かめたこと | 結果 |
|---|---|
| 失敗しない計算の `'E` | 自由な型変数のまま残り、どの `E` にも合う。ZIO の `Nothing` の役を果たす（`val never: unit -> Zio<'a,'b,int>`） |
| 同じ計算を値にする | 値の制限で 1 つの型に固定され、2 つ目の型で FS0001 になる。`()` を受ける関数にする（`zio-never-value.fsx`、`zio-never-function.fsx`） |
| `E` の違う計算を `let!` でつなぐ | FS0001。ZIO は `E1 >: E` で上界へ広がるが、F# に変性はない（`zio-error-mismatch.fsx`） |
| `mapError` で共通の共用体へ移す | 通る。Orsak の [`changeError`](https://johsand.github.io/Orsak/errors.html) と同じ解決である（`zio-map-error.fsx`） |
| `catchAll` で `E` を消す | 通る（`val reserveOrZero: sku: string -> Zio<'a,'b,int>`）。スロットが 1 つなので、消す操作が普通の関数になる |
| 静的抽象メンバーで `E` の行を作る（試作） | 通る（`when 'b :> IOfPayment<'b> and 'b :> IOfStock<'b>`）。最後に共用体で閉じる。1 段ずつは消せない |
| `and!` で両方の失敗を残す | 失敗の型が `+` を持てば通る（`Zio.validate`、ZIO の `validate` に当たる）。持たなければ FS0001。Orsak の `and!` と同じ解決である（`zio-validate.fsx`、`zio-validate-without-plus.fsx`） |

型に現れる部分は Rio と同じ構造で、行の問題も同じである。この `Zio` は `bind` ごとに `Result` を作るので、操作 1 回が約 50 ns 増える（[8.1 節](#81-freer-の-bind)）。ZIO は失敗を実行ループのスタックの走査で扱い、`E` は幽霊型なので、`flatMap` ごとの判定がない。6 節の試作も同じ方式である。

## 6. 中断と構造化並行性

README のモデルと移植ノートの「中断」は `perform` で止まることを指し、外から止めること（キャンセル）はモデルの外にある（[README の課題 8](../README.md#課題-8-失敗と中断を分けられない)）。ZIO のドキュメントは、この論点に[専用の章](https://zio.dev/reference/interruption/)を設けている。

### 6.1 ZIO の中断

- **親子の寿命が結びつく。** `fork` の子は[親の寿命に従い](https://zio.dev/reference/fiber/)、親が終わると中断される。`forkDaemon` と `forkScoped` で外せる。
- **中断は断れず、遅らせるだけである。** 中断不可の領域で届いた中断は記録され、領域の終わりに届く。
- **後始末は必ず、中断不可の領域で走る。** `Fiber#interrupt` は[後始末の完了を待って](https://zio.dev/reference/interruption/interruption-and-finalizers)戻る。後始末も計算なので、非同期でもよい。

仕組みは小さい。[`onExit`](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core/shared/src/main/scala/zio/ZIO.scala#L1123) は全体を中断不可にして、本体だけを `restore` で元に戻す。実行ループは失敗の走査で、中断が届いていて中断可能なら `FoldZIO` を飛ばす。後始末の `FoldZIO` は中断不可の領域にあるので飛ばされない。

```scala
final def onExit[R1 <: R](cleanup: Exit[E, A] => URIO[R1, Any])(implicit trace: Trace): ZIO[R1, E, A] =
  ZIO.uninterruptibleMask { restore =>
    restore(self).foldCauseZIO(
      failure1 => {
        val result = Exit.failCause(failure1)
        cleanup(result).foldCauseZIO(
          failure2 => Exit.failCause(failure1 ++ failure2),
          _ => result
        )
      },
      success => {
        val result = Exit.succeed(success)
        cleanup(result).flatMap(_ => result)
      }
    )
  }
```

継続をランタイムが持ち、1 回で終わるからできることである。Freer のマルチショットでは、[README の課題 7](../README.md#課題-7-マルチショットと可変状態)のとおり `finally` が経路の数だけ走り、捨てられた経路では走らない。

### 6.2 `Async` との比較

F# の `Async` はトークンによる中断と継続を持つので、最も近い既存の仕組みである。同じ場面を実行して比べた。試作は 6.3 節のファイバーである。ZIO の列はドキュメントとソースによる。

| 場面 | ZIO | `Async` | 試作 |
|---|---|---|---|
| 親が正常に終わったときの子 | 中断される | **走り続ける**（`StartChild`） | 中断され、後始末が走る |
| 親を中断したときの子 | 中断される | 中断される（トークンを共有） | 中断される |
| 中断の要求が返るまでに後始末が終わるか | 終わる | **保証がない**（後述） | 終わる |
| 後始末の中の非同期処理 | 書ける | **`do!` は FS0750**。スレッドを塞ぐことになる | 書ける |
| 中断を遅らせる領域 | `ZIO.uninterruptible` | **標準の手段がない**。別のトークンで動かして `AwaitTask` で待つ関数を書くと遅らせられる | 領域の終わりに届く |
| タイムアウトの計算 | 中断される | 中断される（`StartChild` の第 2 引数） | `race` で書ける |
| 競争の負けた側 | 中断される | 中断される（`Async.Choice`） | 中断し、後始末を待つ |
| 並列の失敗 | `Cause.Both` に両方残る | **1 つだけ残る**（`Async.Parallel`） | `Both(Fail "A", Fail "B")` |
| 本体と後始末の失敗 | `Cause.Then` に両方残る | **本体の例外が消える** | `Then(Fail "body", Die finalizer)` |
| 継続の 2 回目の呼び出し | 無視される | `InvalidOperationException` | 無視される |

`Cancel` が後始末を待つかどうかは、中断される側の状態で変わる。

| 中断される側の状態 | `Cancel` が戻るまで | 後始末の完了まで |
|---|---:|---:|
| `Async.Sleep` で待機中（コールバックが `Cancel` のスレッドで走る） | 204 ms | 211 ms |
| 計算中（次の `let!` まで気づかない） | 0 ms | 311 ms |

`FromContinuations` の `cancel` は自動では呼ばれない。使わない形で止まっている計算は、`Cancel` しても `finally` が走らず、止まったままになる。サンプルの [`AsyncOneShot.capture`](../samples/EffectSystem.Samples/AsyncOneShot.fs) はトークンに登録して自分で呼ぶ。

`Async` の列のうち、親子の寿命と中断、並列の失敗、後始末の例外、`cancel` を呼ばない形は [Tests.fs](../tests/EffectSystem.Tests/Tests.fs) で固定している。

### 6.3 ファイバーの試作

ZIO の実行ループの構造を F# へ写したものを試作した。コメントと空行を除いて約 440 行で、外部への依存はない。

- 計算は閉じた命令のデータで、継続はファイバーの可変配列に置く。
- `'R` と `'E` は幽霊型で、環境はファイバーの持つ値である。
- スケジューラーは単一スレッドである。**複数のコアでの並列実行は実装していない。**

```fsharp
// 失敗の走査。中断が届いていて中断可能なら、回復の処理を飛ばす。
match fr with
| :? Fold as f ->
    if not (interruptRequested && interruptible) then
        next <- f.OnFail cause
| :? RestoreMask as r -> interruptible <- r.Flag
| :? RestoreEnv as r -> env <- r.Env
| _ -> ()

// ZIO の onExit と同じ形。後始末も計算なので、非同期でもよい。
let ensuring (fin: Zio<'R, Never, unit>) (m: Zio<'R, 'E, 'a>) : Zio<'R, 'E, 'a> =
    uninterruptibleMask (fun restore ->
        Zio(
            Fold(
                (restore.Apply m).Op,
                (fun c -> Fold(fin.Op, (fun c2 -> Failure(Then(c, c2))), (fun _ -> Failure c))),
                (fun v -> FlatMap(fin.Op, (fun _ -> Succeed v)))
            )
        ))
```

6.2 節の試作の列はすべて実行した結果である。F# に写すときに ZIO と変わる点は次のとおりである。

| ZIO | 試作 | 理由 |
|---|---|---|
| `restore` は多相の関数 | `Restore` クラスのジェネリックメソッド | F# の関数はランク 2 多相を引数に取れない |
| `UIO[A]` は任意の `ZIO[R, E, A]` として使える | 能力の戻り値を `widen` で変換する | F# に変性も部分型の `Nothing` もない |
| `for` | `zio { }`。`if` と `for` をそのまま書ける | F# のコンピュテーション式はマクロなしで書ける |

コストは [8.2 節](#82-ファイバーの試作)にある。Direct のスレッドを、止まっている間は約 330 バイトのオブジェクトに置き換えられる。代償は計算が `zio { }` で書かれること、再開が 1 回に限られること、利用者が制御の操作を定義できないことである。

## 7. レイヤー: 順序が意味を持たないハンドラー

### 7.1 ZLayer と `handlers { }`

[`ZLayer[-RIn, +E, +ROut]`](https://zio.dev/reference/contextual/zlayer) は環境を作るレシピで、失敗と解放を含む。`provide` は渡された層から依存グラフをコンパイル時に組み立て、[渡す順序を問わない](https://zio.dev/reference/di/automatic-layer-construction)。同じ層は[1 回だけ作られる](https://zio.dev/reference/di/dependency-memoization)。

順序を問わないのは、サービスがすべて「末尾で 1 回」だからである。[ハンドラーの順序が意味を決める](../README.md#ハンドラーの順序が意味を決める)のは、継続を複製したり、保存したり、捨てたりするものがあるときで、別々の操作を扱うハンドラーが末尾で再開するだけなら順序は結果を変えない。

```fsharp
prog |> Eff.handle [ Eff.fn (fun (_: Ask<string>) -> "x") ]
     |> Eff.handle [ Eff.fn (fun (Emit(v: string)) -> log.Add v) ]
     |> Eff.run
// 順序を入れ替えても、結果も出力も同じ。
```

`handlers { }` のハンドラーの型 `('cap -> Eff<'r>) -> Eff<'r>` は、能力を作り、スコープの終わりに解放するので、すでにレイヤーの役割を持っている（[EffektStyle.fs](../samples/EffectSystem.Samples/EffektStyle.fs)）。

```fsharp
let scoped (out: Log) (name: string) (make: unit -> 'cap) (action: 'cap -> Eff<'r>) : Eff<'r> =
    eff {
        out $"acquire {name}"
        let cap = make ()

        try
            return! action cap
        finally
            out $"release {name}"
    }

handlers {
    let! db = dbHandler out
    let! repo = repoHandler out db
    let! cache = cacheHandler out db

    return!
        eff {
            let! a = repo.Find "a"
            let! b = cache.Get "b"
            return a + "," + b
        }
}
|> Eff.run
```

| ZLayer の機能 | `handlers { }` | 結果 |
|---|---|---|
| 依存の共有 | `let!` の値を渡す | `db` の `acquire` は 1 回 |
| 逆順の解放 | ハンドラーの入れ子 | `release cache`、`release repo`、`release db` |
| 構築の途中の失敗 | 同じ | 作れたものだけ解放される |
| 足りない依存の検出 | 能力の引数、または `Rio.run` | コンパイルエラー |
| 依存グラフの自動配線 | **書けない** | F# にマクロがない |
| 依存の順序を問わない | **順序を手で書く** | `let!` の順序が依存の順序になる |

自動配線のためにリフレクションを使うと、コンパイル時の検査という利点が失われる。レイヤーの型は作らない。

### 7.2 途中で 1 回の例

[継続の使い方の表](../README.md#継続の使い方が機能を決める)の「途中で 1 回」の例である。ZIO の `atomically` に当たる形を、そのまま Freer で書ける（[Advanced.fs](../samples/EffectSystem.Samples/Advanced.fs)）。

```fsharp
type Begin =
    | Begin

    interface Effect<unit>

let transaction (out: Log) (m: Eff<'a>) : Eff<'a> =
    m
    |> Eff.handle
        [ Eff.ctl (fun Begin resume ->
              eff {
                  out "begin"

                  try
                      let! r = resume ()
                      out "commit"
                      return r
                  with e ->
                      out $"rollback: {e.Message}"
                      return raise e
              }) ]
```

| 計算 | 記録 |
|---|---|
| `Begin` の後に `work` | `begin`、`work`、`commit` |
| `Begin` を 2 回 | `begin`、`a`、`begin`、`b`、`commit`、`commit` |
| `Begin` の後に例外 | `begin`、`rollback: work failed` |

`try` がなければ、例外のときに `commit` も `rollback` も走らない。

## 8. コスト

Ryzen 7 8745H（16 論理プロセッサ）、.NET 10.0.13、F# 10（SDK 10.0.402）、Release ビルドで測った。幅は 3 回の実行の差である。ZIO は測っていない。エンジン全体のコストは[移植ノートの 4.2 節](porting-notes.md#42-コスト)にある。

### 8.1 Freer の `bind`

`bench` で測った、操作 1 回あたりの時間である。

| 計算 | 時間 |
|---|---:|
| 左ネスト、深さ 1,000 | 198–203 ns |
| 左ネスト、深さ 4,000 | 203–273 ns |
| 左ネスト、深さ 16,000 | 271–362 ns |
| 左ネスト、深さ 64,000 | 385–454 ns |
| 左ネスト、深さ 256,000 | 460–513 ns |
| fold、深さ 4,000 | 154–173 ns |
| 観測と `bind` の交互、深さ 4,000 | 377–390 ns |
| 観測と `bind` の交互、深さ 256,000 | 981–1,055 ns |
| 各段に `try` がある左ネスト、深さ 250 | 22.3–23.8 µs |
| 各段に `try` がある左ネスト、深さ 1,000 | 63.4–68.6 µs |

左ネストの深さ 64,000 以上で伸びるのは、長く生きるクロージャを GC が運ぶためと考えられるが、切り分けていない。

`Eff` の上に `E` を載せた 5.3 節の `Zio` は 107–110 ns で、同じ実行の Rio（`Eff.ret`）の 59–62 ns より約 50 ns 遅い。

### 8.2 ファイバーの試作

[Bench.fs](../samples/EffectSystem.Samples/Bench.fs) と同じ形で測った。試作はリポジトリにないので、リポジトリのコマンドでは再現できない。実行が別なので、[移植ノートの 4.2 節](porting-notes.md#42-コスト)とは直接比べず、この節の中の相対値を読むこと。

| 測定 | 試作 | 比較 |
|---|---:|---|
| 値を返す `let!` | 32 ns | |
| 環境の能力を呼ぶ `let!` | 64–66 ns | Rio、`Eff.ret`: 59–62 ns |
| コールバックを同期に呼ぶ再開 | 116–119 ns | `Async.FromContinuations`: 213–234 ns |
| 切り替え（`yield`） | 65–67 ns | Direct: 382–509 ns（スレッドの手渡し） |
| ファイバーの生成と待機 | 225–238 ns | `StartChild` と待機: 881–1,228 ns、Direct: 74–86 µs |
| 左ネスト、深さ 4,000 | 117–130 ns | |
| 左ネスト、深さ 256,000 | 309–369 ns | |
| fold、深さ 256,000 | 253–285 ns | |
| 止まっているファイバー 1 本のメモリ | 331–335 バイト | Direct はスレッド 1 本 |

- 10 万本と 100 万本のファイバーを止めて中断でき、1 MB のスタックで深さ 100 万の左ネストと fold が完走した。
- 試作は単一スレッドなので、切り替えにロックもスレッドの手渡しもない。Direct との差はこの条件の差を含む。
- 環境を幽霊型にしてファイバーに持たせても、能力を呼ぶコストは Rio とほとんど変わらない。

## 9. F# のコードへの活用

| 対象 | 判断 | 理由 |
|---|---|---|
| **`bind` を節にする** | **本体に入れている**（[Freer.fs](../src/EffectSystem/Freer.fs)） | `fn` と `ctl` の経路のコストを増やさずに、Freer の左ネストの弱点が解ける。型整列キューより小さく、依存も増えない。入れ子の `try` のコストは残る |
| **`Rio.provideSome`** | 本体に入れている（[Capability.fs](../src/EffectSystem/Capability.fs)）。転送クラスはサンプルにある（[EffektStyle.fs](../samples/EffectSystem.Samples/EffektStyle.fs)） | 1 行で書け、局所処理が Rio でも書けることを示せる |
| **途中で 1 回の `transaction`** | サンプルに入れている（[Advanced.fs](../samples/EffectSystem.Samples/Advanced.fs)） | README の継続の使い方の表が指す例になる |
| `IGrowsInto` | 入れない | 転送の置き場所が変わるだけで、開いた行は書けない。同じ形は Orsak にある |
| `E` のチャネル | 本体に入れない。サンプルにある（[ZioStyle.fs](../samples/EffectSystem.Samples/ZioStyle.fs)） | 操作ごとに約 50 ns 増え、型の問題は Rio と同じである。Orsak が同じ形を Task の上に実装している（[比較](orsak-fio-report.md#33-失敗)） |
| ファイバー | 本体に入れない | 約 440 行で「一度に読み切れる大きさ」を超え、単一スレッドに限られる。利用者が制御の操作を定義できないので、このプロジェクトの主題から外れる。F# の実装には FIO がある（[比較](orsak-fio-report.md#4-fio-zio-の実行系を写したもの)） |
| ZLayer | 型は作らない。`scoped` の例がサンプルにある（[EffektStyle.fs](../samples/EffectSystem.Samples/EffektStyle.fs)） | `handlers { }` で足りる。自動配線はマクロなしでは書けない |
| zio-direct | 書けない | マクロが要る。F# のコンピュテーション式が `if`、`while`、`try` を含めて同じ役割を果たす |
| 既定のサービス、`FiberRef` | 作らない | サンプルの計算に必要がない |

## 10. 未検証の事項

- **ZIO は実行していない。** ZIO の記述はドキュメント、ソース、テストによる。特に次の点はシグネチャからの読み取りで、コンパイルして確かめていない。
  - `ZIO[Ask[Int] & Ask[String], ...]` が書けて、型タグで区別されること
  - `ZIO.scoped` が `Scope with R` から `R` をどこまで推論できるか
  - `E1 >: E` で広がった `E` が Scala 2 と 3 で何になるか
- **ZIO のコストは測っていない。** 8 節の数値はすべて F# のものである。
- **Effect と Kyo は動かしていない。** Effect はソースのシグネチャ、Kyo は VirtusLab の記事の要約で確認した。Orsak と FIO は、この文書の後に動かして比べた（[Orsak と FIO の検証](orsak-fio-report.md)）。
- **試作は単一スレッドである。** 複数コア、ブロッキング、スレッドの移動、公平性、負荷の下での挙動は評価していない。ZIO の 10,240 操作ごとの譲渡は写したが、効果は測っていない。
- **`bind` の節の計算量は実測による。** 3.3 節の形を測ったもので、証明はしていない。測っていない形では、型整列キューが与える保証より弱い可能性がある。
- **`Async` の比較は小さな例である。** `Task` と `task { }` は試していない。
- **実際のアプリケーションでは試していない。**
- **調査の範囲外。** STM、`ZStream`、`Schedule`、ZIO Test、Executor は読んでいない。

## 参考文献

元資料と主な論文は [README の参考文献](../README.md#参考文献)にある。ここには、この文書で引用した資料を挙げる。

**ZIO のドキュメント**

- [Summary](https://zio.dev/overview/summary)、[Handling Errors](https://zio.dev/overview/handling-errors)、[Handling Resources](https://zio.dev/overview/handling-resources)、[Basic Concurrency](https://zio.dev/overview/basic-concurrency)
- [Introduction to the ZIO's Contextual Data Types](https://zio.dev/reference/contextual/)、[ZEnvironment](https://zio.dev/reference/contextual/zenvironment)、[ZLayer](https://zio.dev/reference/contextual/zlayer)、[ZIO Environment Use-cases](https://zio.dev/reference/contextual/zio-environment-use-cases)
- [The Four Elements of Service Pattern](https://zio.dev/reference/service-pattern/)、[The Three Laws of ZIO Environment](https://zio.dev/reference/service-pattern/the-three-laws-of-zio-environment)、[Accessor Methods](https://zio.dev/reference/service-pattern/accessor-methods)
- [Automatic Layer Construction](https://zio.dev/reference/di/automatic-layer-construction)、[Layers Are Shared by Default](https://zio.dev/reference/di/dependency-memoization)
- [Expected and Unexpected Errors](https://zio.dev/reference/error-management/expected-and-unexpected-errors)、[Typed Errors Guarantees](https://zio.dev/reference/error-management/typed-errors-guarantees)、[Cause](https://zio.dev/reference/core/cause)
- [Introduction to ZIO Fibers](https://zio.dev/reference/fiber/)、[Introduction to ZIO's Interruption Model](https://zio.dev/reference/interruption/)、[Interruption, Finalizers, and Resource Safety](https://zio.dev/reference/interruption/interruption-and-finalizers)、[Scope](https://zio.dev/reference/resource/scope)、[FiberRef](https://zio.dev/reference/state-management/fiberref)
- [ZIO 2.x Migration Guide](https://zio.dev/guides/migrate/zio-2.x-migration-guide/)、[v2.0.0-RC4 のリリースノート](https://github.com/zio/zio/releases/tag/v2.0.0-RC4)
- [Introduction to ZIO Direct Style](https://zio.dev/zio-direct/)

**確認に使ったソースコード**（コミット 54cc16d）

- [FiberRuntime.scala](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core/shared/src/main/scala/zio/internal/FiberRuntime.scala)
- [ZIO.scala](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core/shared/src/main/scala/zio/ZIO.scala)
- [ZEnvironment.scala](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core/shared/src/main/scala/zio/ZEnvironment.scala)
- [ZIOSpec.scala](https://github.com/zio/zio/blob/54cc16d47b97c28e47b9d8b22f9991ab9ec83396/core-tests/shared/src/test/scala/zio/ZIOSpec.scala)
- [Effect.ts](https://github.com/Effect-TS/effect/blob/main/packages/effect/src/Effect.ts)（Effect の `provideService`）

**F# の先行例**

- [Orsak](https://github.com/JohSand/Orsak)、[ドキュメント](https://johsand.github.io/Orsak)（[Composition](https://johsand.github.io/Orsak/2_Motivation.html)、[Errors](https://johsand.github.io/Orsak/errors.html)、[Scopes](https://johsand.github.io/Orsak/scopes.html)）
- [FIO](https://github.com/fs-fio/FIO)
- Bartosz Sypytkowski, [Dealing with complex dependency injection in F#](https://www.bartoszsypytkowski.com/dealing-with-complex-dependency-injection-in-f/), 2020

**論文と解説**

- Rúnar Bjarnason, [Stackless Scala With Free Monads](https://blog.higher-order.com/assets/trampolines.pdf), 2012
- [The Koka Programming Language](https://koka-lang.github.io/koka/doc/book.html)（3.4.11 Initially and Finally、Raw Control）
- VirtusLab, [Comparing effect systems in Scala: Kyo, Gears, and Ox](https://virtuslab.com/blog/scala/comparing-effect-systems-in-scala-kyo-gears-and-ox), 2026
- [Effect: Managing Services](https://effect.website/docs/requirements-management/services/)
