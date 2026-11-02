# Orsak と FIO の検証

[README](../README.md) の補足として、F# の先行例である [Orsak](https://github.com/JohSand/Orsak) と [FIO](https://github.com/fs-fio/FIO) を調査した結果を記録する。このプロジェクトとの共通点と違い、設計の考え方と実装、機能と性能を比べ、README、本体、サンプルに取り込めるものを検証した。

1. 結論
2. 位置づけ
3. Orsak: Rio と Zio を Task の上に載せたもの
4. FIO: ZIO の実行系を写したもの
5. 中断と構造化並行性
6. コスト
7. このプロジェクトへの取り込み
8. 未検証の事項

調査の対象は NuGet の公開版の Orsak 0.7.0（タグのコミット [a5ff3da](https://github.com/JohSand/Orsak/tree/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1)、2026-10-18）と FSharp.FIO 0.5.0-beta（タグのコミット [fe10c67](https://github.com/fs-fio/FIO/tree/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe)、2026-10-11）である。**どちらも実行して確かめた。** ソースは同じ版のものを読んだ。どちらも開発が続いており、Orsak の main（2c1244c、2026-10-27）は中断のスコープを足し、FIO は 0.6.0-beta（eeb0116、2026-11-01）をタグにしたが NuGet にはまだない。版で変わる点は本文に注記する。

検証のコードはリポジトリにある。ライブラリを使うサンプルと計測は [samples/EffectSystem.PriorArt/](../samples/EffectSystem.PriorArt/) に、挙動を固定するテストは [PriorArtTests.fs](../tests/EffectSystem.Tests/PriorArtTests.fs) と [tests/compile-fail/](../tests/compile-fail/) の `orsak-*.fsx` にある。

## 1. 結論

Orsak と FIO はどちらも ZIO に倣った F# のライブラリだが、ZIO の別々の層を写している（[2 節](#2-位置づけ)）。Orsak は環境 `R` と失敗 `E` の型を写し、実行は .NET の Task に任せる。FIO は実行系（閉じた命令の木、ファイバー、中断、後始末）を写し、環境の型を持たない。

どちらも利用者に開く制御が末尾で 1 回の操作に限られ、複数回の再開もないので、このプロジェクトのエンジンの置き換えにはならない。一方で、[README の課題 4](../README.md#課題-4-行多相がない)（行多相）と[課題 8](../README.md#課題-8-失敗と中断を分けられない)（中断）に対して、実際に使える F# のライブラリが出した答えになっている。

| 対象 | Orsak / FIO の手法 | 確かめた結果 | 判断 |
|---|---|---|---|
| **環境** | Orsak: Rio と同じインターフェイス制約 | 推論も検査も Rio と同じ。提供者インターフェイスの名前がラベルになる | README に位置づけを記載。サンプルに写しがある |
| **失敗を合わせる** | Orsak: `and!` が失敗の型の `+` で合わせる | Zio に `validate` として写せる。`+` がなければ FS0001 | **サンプルに入れている**（[ZioStyle.fs](../samples/EffectSystem.Samples/ZioStyle.fs) の `checkLine`） |
| **途中で 1 回** | Orsak: `CompletingScope`、FIO: `acquireReleaseWith` | 継続を渡さないので、捨てられた経路で後始末が飛ばない。FIO は外からの中断でも rollback する | サンプルにある。本体には入れない |
| **中断と構造化並行性** | FIO: ZIO と同じ親子の寿命、中断不可の領域 | ZIO の検証で比べた 11 の場面のうち 7 つで ZIO と同じ結果になる。中断したファイバーの結果を後始末より先に返す点と、失敗を 1 つの値で記録する点が違う | [README の課題 8](../README.md#課題-8-失敗と中断を分けられない) と[やらなかったこと](../README.md#やらなかったこと)に記載 |
| **スタック** | Orsak: スレッドごとのトランポリン、FIO: ヒープ上のスタック | Orsak は左ネストが 1 MB のスタックで 750 段で溢れる。FIO は 100 万が完走する | 本体には入れない。Freer の方式で足りている |
| **ライブラリそのもの** | NuGet のパッケージ | 別のプロジェクトで使える。`eff` の名前が衝突する | [samples/EffectSystem.PriorArt/](../samples/EffectSystem.PriorArt/) に入れている |

判断を左右した実測は次の 3 つである。

- **Orsak の操作 1 回は Rio と同じ桁である。** `while` の中で 62–63 ns で、同じ実行の Rio（`Eff.ret`）は 56–57 ns だった。ただし末尾の `return!` で再帰すると 655–691 ns になる（[6.1 節](#61-操作-1-回)）。
- **FIO のファイバーは Direct のスレッドより 1 桁以上軽いが、ZIO の検証の試作より 1 桁重い。** 生成と待機は 2.5–2.9 µs（Direct は 74–75 µs、試作は 225–238 ns）、止まっているファイバー 1 本は 3.3–6.0 KB（試作は 331–335 バイト）だった（[6.2 節](#62-ファイバーと切り替え)、[6.4 節](#64-止まっている計算のメモリ)）。
- **FIO は中断したファイバーの結果を、その後始末より先に返す。** ZIO の `interrupt` は後始末を待つ。FIO でも親は終わるときに子の後始末を待つので、後始末が飛ぶことはない（[5.2 節](#52-中断の結果と後始末の順序)）。

## 2. 位置づけ

[README の設計軸](../README.md#実装を分ける設計軸)の上では次の位置にある。比べやすいように、このプロジェクトの Rio と、[ZIO の検証](zio-report.md#2-zio-の位置づけ)の ZIO を並べた。

| 軸 | Rio | Orsak | FIO | ZIO |
|---|---|---|---|---|
| 操作の意味を決めるもの | 利用者のハンドラー（能力の実装） | 末尾で 1 回の操作はサービス、制御は Task | 末尾で 1 回の操作は能力、制御はランタイムの命令（19 種類） | 末尾で 1 回の操作はサービス、制御はランタイムの命令（12 種類） |
| 継続の表現 | ヒープ上のクロージャ（Freer） | .NET の非同期のステートマシン | ヒープ上の可変配列（`Stack<Cont>`） | ヒープ上の可変配列 |
| 継続の寿命 | 値として持ち出せる（`shift` の継続） | 利用者には渡らない | 利用者には渡らない（`FIO.async` のコールバックだけ） | 利用者には渡らない（`ZIO.async` のコールバックだけ） |
| 再開の回数 | 何回でも | 1 | 1 | 1 |
| ハンドラーの範囲 | Deep | Deep に当たる（サービス） | 当てはまらない（引数で渡す能力） | Deep に当たる（サービス） |
| 探し方 | 能力（環境の値） | 能力（環境の値） | 能力（関数の引数） | 最も近いもの（型タグ） |
| 静的検査 | あり（`'R` の制約） | あり（`'r` の制約） | あり（引数） | あり（`R`） |
| 書き方 | `rio { }` | `eff { }`。`Task`、`Async`、`Result` を直接 `let!` できる | `fio { }` | `for` |

ZIO を型の追跡、利用者の操作、底の実行系の 3 つの層に分けると、Orsak と FIO は別々の層を受け継いでいる。底の実行系は、README のモデルの `run` を、ファイバー、中断、後始末を持つものに膨らませたものである。

| | 型の追跡 | 利用者の操作 | 底の実行系 |
|---|---|---|---|
| ZIO | `R`（サービスの行）、`E` | サービス | ファイバー、中断、後始末、構造化並行性 |
| Orsak | `'r`、`'e`（ZIO と同じ） | サービス（提供者インターフェイス） | .NET の Task。中断は協調的で、親子の関係はない |
| FIO | `'E` | 引数で渡す能力 | ZIO と同じ形のファイバー |
| Rio と Zio | `'R`、`'E`（Zio だけ） | 能力。`Eff.ret` で答えれば末尾で 1 回、`shift` を使えば継続まで開く | Freer |

Rio と Zio は ZIO と同じ型の層を持つが、底が Freer なので継続まで開けてしまい、ZIO と同じ保証は持てない。利用者に開く制御の段階と後始末の責任は [ZIO の検証の 2 節](zio-report.md#2-zio-の位置づけ)に、README での要約は [ZIO 系の位置づけ](../README.md#zio-系の位置づけ)にある。

Orsak の `Effect<'r, 'a, 'e>` は、[ZioStyle.fs](../samples/EffectSystem.Samples/ZioStyle.fs) の `Zio<'R, 'E, 'a>` と同じ形を、Freer の代わりに `ValueTask` の上に作ったものである。

```fsharp
// このプロジェクト
type Zio<'R, 'E, 'a> = Zio of ('R -> Eff<Result<'a, 'E>>)

// Orsak
type Effect<'r, 'a, 'e> = Effect of EffectDelegate<'r, 'a, 'e>   // delegate of 'r -> ValueTask<Result<'a, 'e>>
```

FIO は、[ZIO の検証の 6.3 節](zio-report.md#63-ファイバーの試作)の試作を、環境の型引数を除いてマルチスレッドにしたものに当たる。

[継続の使い方の表](../README.md#継続の使い方が機能を決める)の各行は、次のものに当たる。

| `k` の使い方 | 得られる機能 | Orsak | FIO |
|---|---|---|---|
| 呼ばない | 例外、早期脱出 | `return! Error e` | `FIO.fail` |
| 末尾で 1 回 | 依存性の注入、Reader | 環境のサービス（`Effect.Create`） | 関数の引数 |
| 途中で 1 回 | 前後処理、トランザクション | `CompletingScope.Within`、`use` | `Ensuring`、`acquireReleaseWith` |
| 状態を渡しながら 1 回 | State | なし（サービスに持たせる） | `Ref` |
| 保存して後で | ジェネレーター、非同期、スケジューラー | `Task` の `await`、`effSeq { }` | `FIO.async`、`Fork`、`Channel` |
| 複数回 | 非決定計算、バックトラック | **なし** | **なし** |

どちらも ZIO と同じく、複数回の再開だけを持たない。

## 3. Orsak: Rio と Zio を Task の上に載せたもの

### 3.1 計算の表現

[`Effect<'r, 'a, 'e>`](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/EffectBuilder.fs#L63) は環境を受け取って `ValueTask<Result<'a, 'e>>` を始める関数で、`eff { }` は `task { }` と同じ再開可能なコード（resumable code）で書かれている。ビルダーのメンバーは `inline` なので、ステートマシンは利用者のアセンブリに作られる。本体（`src/Orsak`）はコメントと空行を除いて約 4,900 行で、多くはビルダーの `Bind` の多重定義と、静的と動的の 2 通りの実装である。

| 確かめたこと | 結果 | 確認 |
|---|---|---|
| 組み立てただけで動くか | 動かない。同じ値を実行のたびに動かす | `組み立てただけでは動かず…` |
| 継続 | ステートマシンが持ち、利用者に渡らない。再開は 1 回 | ソース |
| 例外 | `'e` にならず、タスクの例外になる | `例外は失敗にならず…` |
| 名前 | Orsak もこのプロジェクトも `eff` を自動で開くので、後から開いた方が前を隠す | [Bench.fs](../samples/EffectSystem.PriorArt/Bench.fs) は Freer の節の後で Orsak を開く |

確認の列のテスト名は [PriorArtTests.fs](../tests/EffectSystem.Tests/PriorArtTests.fs) のものである（以下も同じ）。

**Debug では別の実装になる。** Orsak の [`Directory.Build.props`](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Directory.Build.props#L41) によると、SDK 10.0.400 以降のコンパイラは、最適化なしでは再開可能なコードを静的に組み立てる保証がなく、組み立てられないビルダーは黙って Orsak の動的な実装に切り替わる。Orsak は自分のビルドで `Optimize` を既定で有効にしている。このリポジトリの Debug は最適化なしで、`TreatWarningsAsErrors` でも警告は出ず、テストは Debug と Release のどちらでも通る。変わるのはコストとスタックの限界で、操作 1 回が約 2.3 倍になり（[6.5 節](#65-debug)）、左ネストは Release の 600 段に対して 500 段までしか確かめられていない。どちらも 750 段で溢れる（[3.5 節](#35-スタック)）。

### 3.2 環境

環境の扱いは Rio と同じで、注釈なしで同じ形の型が推論される。

```fsharp
// Rio（EffektStyle.fs）
let ask<'R, 'v when 'R :> IAsk<'v>> () : Rio<'R, 'v> = Rio(fun env -> env.Ask())
// val askEmitRow: out: Log -> Rio<'a,unit> when 'a :> IEmit<'b> and 'a :> IAsk<'b>

// Orsak（OrsakStyle.fs）
let ask () = Effect.Create(fun (p: #IAskProvider<'v>) -> p.Ask.Ask())
// val askEmit: out: Log -> Effect<'a,unit,'c> when 'a :> IEmitProvider<'b> and 'a :> IAskProvider<'b>
```

違うのは、サービスを直接制約にせず、提供者インターフェイス（provider interface）を挟む慣習である。提供者のメンバー名がラベルの役割を果たすので、[RecordEnv](../samples/EffectSystem.Samples/RecordEnv.fs) のフィールド名と同じく、型が同じサービスも名前を分ければ同時に要求できる。ただし環境のクラスは提供者をすべて実装する必要があり、その手間は Rio の転送クラスと変わらない。Orsak は [Orsak.Myriad](https://johsand.github.io/Orsak/myriad.html) でこの定型をコードから生成するが、今回は試していない。

| 確かめたこと | 結果 | 確認 |
|---|---|---|
| 注釈なしの型 | 上のとおり。失敗の型 `'c` は自由なまま残る | `OrsakStyle.askEmit` |
| 提供者が欠けた環境で実行する | FS0193。Rio と同じ | `orsak-missing-provider.fsx` |
| 名前の違う提供者を同時に要求する | 通る | `orsak-distinct-providers.fsx` |
| 同じジェネリックな提供者の 2 つのインスタンス | FS0041。Rio の FS0001 と同じ理由（2 つの環境の型変数が 1 つにならない）だが、`Bind` が多重定義されているので報告が変わる | `orsak-two-instantiations.fsx` |
| 行の一部を消す | `IGrowsInto`（[ZIO の検証の 4.2 節](zio-report.md#42-行の一部を消す)）。スコープの中で必要な提供者が、外側の型から消える | `OrsakStyle.transaction` |

### 3.3 失敗

`'e` の扱いは [ZioStyle.fs](../samples/EffectSystem.Samples/ZioStyle.fs) と同じで、失敗しない計算の `'e` は自由な型変数として残り、`Effect.changeError` が `mapError` に当たる。F# に変性がないので、違う失敗の型は共通の共用体へ手で移す（[Errors](https://johsand.github.io/Orsak/errors.html)）。

Zio にない機能が `and!` である。`and!` は両方の計算を始めてから待ち、両方が失敗したら失敗の型の [`+` で合わせる](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/EffectBuilder.fs#L621)。

```fsharp
type Problems =
    | Problems of string list
    static member (+)(Problems a, Problems b) = Problems(a @ b)

let checkLine sku quantity =
    eff {
        let! sku = checkSku sku
        and! quantity = checkQuantity quantity
        return sku, quantity
    }
// checkLine "" 0 → Error (Problems ["sku is empty"; "quantity must be positive"])
```

| 確かめたこと | 結果 | 確認 |
|---|---|---|
| `let!` で 2 つの失敗する計算をつなぐ | 最初の失敗で止まり、2 つ目は始まらない | `let! は最初の失敗で止まる` |
| `and!` で同じことをする | 両方の失敗が `+` で合わさる | `OrsakStyle.checkLine` |
| 失敗の型が `+` を持たない | FS0001（`op_Addition`） | `orsak-and-without-plus.fsx` |
| `Effect.par`、`Effect.whenAll` で 2 つが失敗する | 1 つしか残らない | `par と whenAll は…` |
| 失敗と `finally`、`use` | 失敗は値として戻るので、どちらも走る | `失敗は値として戻るので…` |
| 本体と `finally` が例外を投げる | `finally` の例外だけが残る。`Async` と同じ | `finally の例外は本体の例外を消す` |

ZIO は 2 つの失敗を `Cause.Both` に残すので `E` に条件がない。失敗のスロットが 1 つの Orsak と Zio では、失敗の型に合わせ方を持たせることになる。同じ形を Zio に `validate` として写した（[7 節](#7-このプロジェクトへの取り込み)）。

失敗は `'e` と例外の 2 種類で、ZIO の中断に当たる型はない。中断は `OperationCanceledException` として例外の側に入る。

### 3.4 途中で 1 回: スコープ

トランザクションのように前後に処理を挟む形は、継続を受け取らず、スコープの始まりと終わりで書く。[`CompletingScope.Within`](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/ScopedEffect.fs#L78) は、環境に [`IGrowsInto`](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/ScopedEffect.fs#L43) でスコープを始めさせ、成功したら完了し、どの終わり方でも破棄する。

同じトランザクションを 3 通りに書いて比べた。Freer は [Advanced.fs](../samples/EffectSystem.Samples/Advanced.fs) の `transaction`、Orsak と FIO は [OrsakStyle.fs](../samples/EffectSystem.PriorArt/OrsakStyle.fs) と [FioStyle.fs](../samples/EffectSystem.PriorArt/FioStyle.fs) の `transaction` である。

| | Freer | Orsak | FIO |
|---|---|---|---|
| 仕組み | 継続を受け取り、前後に処理を書く | スコープの完了と破棄 | 獲得と解放（中断不可の領域で動く） |
| 成功 | `begin`、`work`、`commit` | `begin`、`work`、`commit` | `begin`、`work`、`commit` |
| 失敗 | `begin`、`rollback: work failed`（例外） | `begin`、`work`、`rollback`（`Error`） | `begin`、`work`、`rollback`（`Failed`） |
| 継続が捨てられる | commit も rollback も走らない | 起きない | 起きない |
| 外から中断される | モデルの外 | トークンを見る処理が例外で抜ければ、破棄で rollback が走る（ソース） | `begin`、`work`、`rollback` |

継続を利用者に渡さないので、Freer の[課題 8](../README.md#課題-8-失敗と中断を分けられない)の「継続を呼ばない形は `finally` を飛ばす」問題は、どちらにも起きない。

### 3.5 スタック

完了したタスクの `await` は、同じスタックの上で続きを動かす。Orsak は末尾の `return!` を、スレッドごとの[トランポリン](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/Trampoline.fs#L19)で 50 段ごとに戻している（[`ReturnFromFinal`](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/EffectBuilder.fs#L3573)）。

1 MB のスタックで動かした結果は次のとおりである（`limit`）。

| 計算 | Release | Debug |
|---|---|---|
| 末尾の `return!`、100 万回 | ○ | ○ |
| `while`、100 万回 | ○ | ○ |
| 左ネスト | 600 ○ / 750 × | 500 ○ / 750 × |
| fold（`Effect.bind` を左へ積む） | 600 ○ / 750 × | 500 ○ / 750 × |
| 比較: `task { }` の左ネスト | 2,000 ○ / 5,000 × | |

左ネストは、深い位置の計算が同期に完了するたびに、外側のステートマシンが同じスタックの上で続きを動かすので溢れる。`task { }` も同じ形で溢れ、溢れる深さから見て Orsak の方が 1 段のフレームが大きい。Freer は同じ形がどれも 100 万で完走する（[移植ノートの 4.3 節](porting-notes.md#43-スタック)）。

トランポリンにはコストがある。末尾の `return!` で再帰すると、操作 1 回が `while` の約 10 倍になる（[6.1 節](#61-操作-1-回)）。Orsak で繰り返すなら `while` か `for` を使うことになる。

### 3.6 中断

Orsak にファイバーはなく、計算の親子の関係もない。中断は環境の `CancellationTokenSource`（`ICancellationProvider`）のトークンを、計算が自分で見る協調的なものである。

| 確かめたこと | 結果 | 確認 |
|---|---|---|
| [`Effect.race`](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/Effect.fs#L465) の負けた側 | 止まらず、最後まで走る | `race は負けた側を止めない` |
| [`Effect.timeout`](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/Effect.fs#L435) で時間切れになった計算 | 止まらず、最後まで走る | `timeout は計算を止めない` |
| 環境のトークンで中断する | トークンを渡した待機が `TaskCanceledException` で終わり、`finally` が走る。失敗の `'e` にはならない | `環境のトークンを渡した待機は…` |

main（0.7.0 の後）は `CancellationScope` を足し、`Cancellation.timeout` は計算を止める、と変更履歴にある。今回は試していない。

## 4. FIO: ZIO の実行系を写したもの

### 4.1 実行系

[`FIO<'A, 'E>`](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/Core.fs#L654) は `internal` な判別共用体で、0.5.0 では 19 種類の命令を持つ。`ChainSuccess`（`FlatMap`）、`ChainError`（`CatchAll`）、`OnFinalize`、`Suspend`、`WithSuppression`（中断の抑止）、`AcquireRelease`、チャンネル、`Fork` と `Join`、`AwaitTask` などである。実行ループの [`handleSharedCase`](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/Runtime/InterpreterCore.fs#L205) は `ChainSuccess` を受けると、続きを [`Cont`](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/Core.fs#L34) としてファイバーの `Stack<Cont>` に積み、前の計算へ進む。ZIO の `FlatMap` を `_stack` に積むのと同じ形である。0.6.0 は判別共用体をノードのクラスに置き換えた（23 種類）。

利用者は命令を足せず、`resume` もない。利用者が定義できるのは、引数で渡す能力のような末尾で 1 回の操作だけである。

主なランタイムは 2 つある。`DirectRuntime` はファイバーをスレッドプールのタスクとして動かし、`WorkStealingRuntime`（既定）は論理プロセッサの数に応じたワーカーが仕事を盗み合う。後者はファイバーを [200 ステップごと](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/Runtime/Runtime.fs#L16)に譲らせる。

ZIO の検証の試作と並べると次のようになる。

| | ZIO の検証の試作 | FIO 0.5.0 |
|---|---|---|
| 命令 | 閉じたデータ | 閉じた判別共用体（19 種類） |
| 継続 | ファイバーの可変配列 | `Stack<Cont>`（[容量 32](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/Runtime/Runtime.fs#L29) で確保し、使い回す） |
| 環境 | 幽霊型の `'R` | ない |
| 失敗の記録 | `Cause`（`Both`、`Then`） | `FiberResult`（`Succeeded`、`Failed`、`Interrupted`） |
| スレッド | 単一 | スレッドプールの上のマルチスレッド |
| 大きさ（コメントと空行を除く） | 約 440 行 | DSL と Runtime で約 3,800 行 |

### 4.2 環境がない

環境の型引数がないので、能力は普通の引数で渡す。[EffektStyle.fs](../samples/EffectSystem.Samples/EffektStyle.fs) の `askEmit` と同じ形で、能力を持っていなければ呼べないので、検査はコンパイル時になる。能力のメソッドが答えを直接返すので、継続を取り出さない `askHandlerDirect` に当たる。

```fsharp
// FioStyle.fs
let askEmit (ask: IAsk<'a>) (emit: IEmit<'a>) (out: Log) : FIO<unit, exn> =
    fio {
        out "Start"
        let! v = ask.Ask()
        do! emit.Emit v
        out "End"
    }
```

### 4.3 失敗の分類

ZIO の 3 種類（[ZIO の検証の 5.1 節](zio-report.md#51-zio-の分類)）は、FIO では次のように表れる。

| 種類 | ZIO | FIO |
|---|---|---|
| 予期したエラー | `Fail`（`E`） | `Failed 'E` |
| 欠陥 | `Die` | `Interrupted`、原因が `Defect ex`。中断の原因の 1 つとして扱う |
| 中断 | `Interrupt` | `Interrupted`、原因が `ExplicitInterrupt` または `ParentInterrupted` |

[`InterruptionCause`](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/Exceptions.fs#L6) には、ほかに `InvalidArgument` と `ResourceExhaustion` がある。

| 確かめたこと | 結果 | 確認 |
|---|---|---|
| 計算の中で例外を投げる | 欠陥になる。`fio { }` の `try ... with` は捕まえない | `計算の中の例外は欠陥になり…` |
| `try ... with` で `FIO.fail` を受ける | 捕まえる。[`TryWith`](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/CE.fs#L47) は例外ではなく `'E` を受ける | `try/with は型付きの失敗を捕まえる` |
| `try ... finally` の `finally` | `FIO<unit, 'E>` を書く（[`TryFinally`](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/CE.fs#L50)）。型付きの失敗でも走る | `try/finally の後始末は FIO で書き…` |
| 本体と後始末が `FIO.fail` で失敗する | 本体の失敗が残る | `本体と後始末が失敗すると…` |
| 本体が成功し、後始末が失敗する | 後始末の失敗になる | `本体が成功して後始末が失敗すると…` |
| 本体が失敗し、後始末が例外を投げる | 欠陥が本体の失敗を消す | `後始末の例外は欠陥になり…` |

FIO の `try ... with` が例外を捕まえない点は、F# の `try` の読み方と違うので注意が要る。ZIO の `catchAll` も欠陥を捕まえないので、ZIO と同じ形である。

## 5. 中断と構造化並行性

### 5.1 ZIO と Async との比較

[ZIO の検証の 6.2 節](zio-report.md#62-async-との比較)の表に、FIO と Orsak の列を足した。ZIO の列はドキュメントとソースによる。それ以外は実行した結果である。

| 場面 | ZIO | `Async` | FIO 0.5.0 | Orsak 0.7.0 |
|---|---|---|---|---|
| 親が正常に終わったときの子 | 中断される | **走り続ける** | 中断され、親の結果は子の後始末の後に返る | 親子の関係がない |
| 子を親から外す | `forkDaemon` | | `ForkDaemon`。親が終わっても走り続ける | |
| 親を中断したときの子 | 中断される | 中断される | 中断される | 親子の関係がない。トークンを共有すれば協調的に止まる |
| 中断の要求が返るまでに後始末が終わるか | 終わる | **保証がない** | **終わらない**（[5.2 節](#52-中断の結果と後始末の順序)） | |
| 後始末の中の非同期処理 | 書ける | **`do!` は FS0750** | 書ける。後始末も `FIO` | `IAsyncDisposable` の解放を待てる（ソース） |
| 中断を遅らせる領域 | `ZIO.uninterruptible` | **標準の手段がない** | `FIO.uninterruptible`、`uninterruptibleMask` | **ない** |
| タイムアウトの計算 | 中断される | 中断される | 中断される | **止まらない**（`Effect.timeout`） |
| 競争の負けた側 | 中断される | 中断される | 中断される。勝った側を後始末の前に返す | **止まらない**（`Effect.race`） |
| 並列の失敗 | `Cause.Both` に両方残る | **1 つだけ残る** | `ZipPar` は **1 つだけ**。`ZipParError` は両方を組で返す | `par`、`whenAll` は **1 つだけ**。`and!` は `+` で両方 |
| 本体と後始末の失敗 | `Cause.Then` に両方残る | **本体の例外が消える** | **後始末の失敗が消える**。後始末の例外は本体の失敗を消す | **本体の例外が消える** |
| 継続の 2 回目の呼び出し | 無視される | `InvalidOperationException` | 無視される（`FIO.async`） | |

FIO は 11 行のうち 7 行で ZIO と同じ結果になった。違う 4 行のうち、中断の要求と競争の 2 行は、中断したファイバーの結果を後始末より先に返すことから来る（5.2 節）。並列の失敗と本体と後始末の失敗の 2 行は、失敗の記録が `Cause` ではなく `FiberResult` の 1 つの値であることから来る。

Orsak は Task の上にあり、親子の寿命も中断不可の領域も持たない。タイムアウトと競争では `Async` より弱く、相手を止めない。一方で、失敗の型の `+` を使えば並列の失敗を両方残せる。

FIO の列は [FioStyle.fs](../samples/EffectSystem.PriorArt/FioStyle.fs) の例と [PriorArtTests.fs](../tests/EffectSystem.Tests/PriorArtTests.fs) の `FIO 0.5.0-beta の挙動` で、Orsak の列は `Orsak 0.7.0 の挙動` で固定している。

### 5.2 中断の結果と後始末の順序

ZIO の `Fiber#interrupt` は後始末の完了を待って戻る。FIO の [`InterruptAwait`](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/Core.fs#L469) は、中断してから結果を待つが、結果は後始末より先に返る。

```fsharp
let child = (FIO.never ()).Ensuring(slowFinalizer)   // 後始末に時間がかかる子

fio {
    let! fiber = child.Fork()
    let! result = fiber.InterruptAwaitNow()   // Interrupted が返る。後始末はまだ終わっていない
    return result
}   // 親はここで子の後始末を待つ
```

後始末に 300 ms かかる子で試すと、どちらのランタイムでも結果は 10 ms 以内に返り、その時点で後始末は終わっていなかった（リポジトリのコマンドにはない測定である）。テスト（`中断したファイバーの結果は、その後始末より先に返る`）では、後始末をゲートで止めて順序だけを固定している。

これは FIO の設計で、開発者向けの文書は、中断されたファイバーは後始末より先に結果を公開し、完了するファイバーだけが子の後始末を待ってから公開する、と説明している（0.5.0 の [CLAUDE.md](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/CLAUDE.md#L93)、0.6.0 では[独立した項目](https://github.com/fs-fio/FIO/blob/eeb01164757a124454628a9b27d2555e817ea1c8/CLAUDE.md#L308)）。親は終わるときに子の後始末を待つので、ルートのファイバーの結果を受け取った時点では、すべての後始末が終わっている。`FIOApp` もこの性質に頼っている。

後始末が飛ぶことはないが、「中断を待ったら後始末も終わっている」という ZIO の前提で書いたコードは、FIO では成り立たない。後始末の完了を待つには、そのファイバーを子に持つ親の終了を待つ。

## 6. コスト

Ryzen 7 8745H（16 論理プロセッサ）、.NET 10.0.13、F# 10（SDK 10.0.402）、Release ビルドで測った。幅は 3 回の実行の差である。表の値は `dotnet run -c Release --project samples/EffectSystem.PriorArt -- bench` で、同じ実行の中で Rio と Zio も測っている。Direct の値は同じ日に測った `samples/EffectSystem.Samples` の `bench` で、ZIO の検証の試作の値は[ZIO の検証の 8.2 節](zio-report.md#82-ファイバーの試作)からの引用である（リポジトリのコマンドでは再現できない）。

### 6.1 操作 1 回

環境のサービスを呼んで 1 を受け取る操作を 200 万回繰り返した、1 回あたりの時間である。

| 計算 | 時間 |
|---|---:|
| Rio（`Eff.ret`） | 56–57 ns |
| Zio（`Eff.ret`） | 112–125 ns |
| Orsak、サービスを呼ぶ、`while` | 62–63 ns |
| Orsak、完了した `Task` を返すサービス、`while` | 98–102 ns |
| Orsak、サービスを呼ぶ、末尾の `return!` | 655–691 ns |
| FIO `DirectRuntime`、`succeedWith`、末尾の `return!` | 130–135 ns |
| FIO `WorkStealingRuntime`、同上 | 128–131 ns |

- **Orsak の `while` は Rio に近い。** 1 つのステートマシンの中で回り、完了した `ValueTask` をその場で読む。同じ `Result` を持つ Zio より速いのは、Zio が `bind` ごとに Freer の節と `Result` を作るためと考えられるが、切り分けていない。
- **Orsak の末尾の `return!` は約 10 倍遅い。** 呼び出しのたびに新しいステートマシンを始め、50 段ごとにトランポリンへ戻る。
- **FIO は操作 1 回に 3 つの命令を使う。** `fio { }` の `Delay` が `Suspend` を、`let!` が `ChainSuccess` を、`succeedWith` が `Action` を作り、それぞれを実行ループが解釈する。

### 6.2 ファイバーと切り替え

| 測定 | FIO `DirectRuntime` | FIO `WorkStealingRuntime` | 比較 |
|---|---:|---:|---|
| コールバックを同期に呼ぶ再開 | 518–545 ns | 482–487 ns | `Async.FromContinuations`: 189–197 ns、試作: 116–119 ns |
| 切り替え（`yieldNow`） | 1,203–1,237 ns | 2,104–2,244 ns | Direct の操作 1 回（スレッドの手渡し）: 331–462 ns、試作: 65–67 ns |
| ファイバーの生成と待機 | 2.8–2.9 µs | 2.5–2.6 µs | `Async.StartChild` と待機: 889–908 ns、Direct: 74–75 µs、試作: 225–238 ns |

- **`FIO.async` は `TaskCompletionSource` を介して待つ**（[ソース](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/Factories.fs#L107)）。コールバックを同期に呼んでも、タスクの完了と待機の分だけ `Async.FromContinuations` より遅い。
- **0.5.0 の `yieldNow` は `Task.Run` を待つ**（[ソース](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/Factories.fs#L133)）。スレッドプールを 1 往復するので、Direct のスレッドの手渡しより遅い。
- **ファイバーの生成はスレッドよりはるかに安い。** Direct より 1 桁以上速い。試作との差には、マルチスレッドの同期と、ファイバーごとの `CancellationTokenSource` などの割り当てが含まれると考えられるが、切り分けていない。

### 6.3 左ネスト

| 計算 | Freer | FIO `DirectRuntime` | FIO `WorkStealingRuntime` |
|---|---:|---:|---:|
| 左ネスト、深さ 4,000 | 225–236 ns | 383–499 ns | 317–384 ns |
| 左ネスト、深さ 256,000 | 384–411 ns | 617–670 ns | 650–787 ns |
| fold、深さ 256,000 | | 639–761 ns | |

Orsak は深さ 500 で 231–239 ns だった。それより深いと、1 MB のスタックでは溢れる（[3.5 節](#35-スタック)）。FIO は継続をヒープのスタックに積むので、左ネストも fold も深さ 100 万が完走する。ただし FIO の計算はランタイムのスレッドで動くので、1 MB のスタックの条件では測れていない。

### 6.4 止まっている計算のメモリ

10 万個を止めて、GC の後の使用中のメモリの差を個数で割った（`parked`）。

| 計算 | 1 つあたり | 作成と開始 | 中断して後始末を終えるまで |
|---|---:|---:|---:|
| FIO `DirectRuntime`、`FIO.never` で待つ | 5,450 バイト | 1.1–1.3 s | 0.9–1.0 s |
| FIO `WorkStealingRuntime`、`FIO.never` で待つ | 5,979–5,989 バイト | 1.0 s | 0.5–0.6 s |
| FIO `DirectRuntime`、1 本のチャンネルを全員で待つ | 3,891–3,912 バイト | 0.5 s | 0.6 s |
| FIO `WorkStealingRuntime`、同上 | 3,320 バイト | 0.6 s | 0.4 s |
| Orsak、完了していない `Task` を待つ | 178 バイト | | 再開まで 134–136 ms |
| 比較: 試作 | 331–335 バイト | | |
| 比較: Direct | スレッド 1 本 | 生成と終了で 1 本 74–75 µs | |

どの値も、計算を保持する配列の要素の分を含む。

- **FIO の `never` は、呼ぶたびにチャンネルを作る**（[ソース](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/Factories.fs#L138)）。チャンネルを共有すると 1.5–2.7 KB 減る。
- 残りの 3.3–3.9 KB はファイバーの持ち物で、`CancellationTokenSource`、`TaskCompletionSource`、容量 32 の継続のスタックなどがある。内訳は測っていない。
- Orsak の値は、待っている非同期のステートマシン 1 つとそのタスクの大きさである。親子の関係も中断の記録も持たないので小さい。

### 6.5 Debug

`TreatWarningsAsErrors` のまま、このリポジトリの Debug（最適化なし）で同じ計測を 2 回行った。

| 計算 | Release | Debug |
|---|---:|---:|
| Rio（`Eff.ret`） | 56–57 ns | 147 ns |
| Orsak、`while` | 62–63 ns | 141–143 ns |
| Orsak、`Task` を返すサービス、`while` | 98–102 ns | 165–171 ns |
| Orsak、末尾の `return!` | 655–691 ns | 1,477–1,481 ns |
| FIO `DirectRuntime` | 130–135 ns | 175–207 ns |

Orsak の `while` は約 2.3 倍、Rio は約 2.6 倍で、遅くなる比率は同じ程度である。FIO はパッケージが Release でビルドされているので、利用者のクロージャの分しか変わらない。

## 7. このプロジェクトへの取り込み

| 対象 | 判断 | 理由 |
|---|---|---|
| **Orsak の `and!`（失敗の型の `+`）** | **サンプルに入れている**（[ZioStyle.fs](../samples/EffectSystem.Samples/ZioStyle.fs) の `Zio.validate` と `checkLine`） | ZIO の `validate` に当たる。失敗のスロットが 1 つでも、`+` があれば両方の失敗を残せる。`+` がなければ FS0001（`zio-validate-without-plus.fsx`）。Freer の上なので、Orsak と違って順に実行する |
| **ライブラリの使い方** | **サンプルに入れている**（[samples/EffectSystem.PriorArt/](../samples/EffectSystem.PriorArt/)） | 記事の askEmit と途中で 1 回の例が、どちらのライブラリでも書ける。FSharpPlus と同じく別のプロジェクトにして、本体と既存のサンプルの依存を増やさない |
| **ライブラリの挙動** | **テストで固定している**（[PriorArtTests.fs](../tests/EffectSystem.Tests/PriorArtTests.fs)、`orsak-*.fsx`） | 版を上げて変わったら分かるようにする。FSharpPlus の扱いと同じ |
| **位置づけ** | **README に記載**（設計軸の表の ZIO の列、ZIO 系の位置づけ、課題 8、やらなかったこと、方式の選び方、参考文献） | このプロジェクトでやらなかったことの一部を、実際に使えるライブラリが実装している |
| Orsak の環境の型 | 本体に入れない | Rio と同じものである |
| 提供者インターフェイスとコード生成（Orsak.Myriad） | 入れない | 環境のクラスの定型を減らすが、コード生成器への依存が増える。試していない |
| Orsak のトランポリン | 入れない | Freer は `Delayed` とループで、末尾の再帰も左ネストも 100 万が完走する。Orsak の `return!` は操作 1 回が約 10 倍になる |
| `Task`、`Async`、`Result` を直接 `let!` する多重定義 | 入れない | Freer の計算は実行系を持たないので、`Task` を待つにはハンドラーかランタイムが要る |
| `IGrowsInto` | 入れない | [ZIO の検証の 9 節](zio-report.md#9-f-のコードへの活用)と同じ理由である |
| FIO の実行系 | 本体に入れない | ZIO の検証の 9 節の判断を変えない。マルチスレッドで動く実装は FIO にあるが、利用者が制御の操作を定義できないので、このプロジェクトの主題から外れる |
| Direct のファイバーを FIO の上で動かす | しない | Direct の継続はスレッドのスタックで、FIO は継続を利用者に渡さない。FIO の上では OCaml の `perform` を写せない |

## 8. 未検証の事項

- **新しい版は動かしていない。** Orsak の main の `CancellationScope`、FIO 0.6.0-beta のノードのクラス、`Promise`、`Semaphore`、`OnExit`、`acquireReleaseExitWith` は、ソースと変更履歴を読んだだけである。
- **周辺のパッケージは試していない。** Orsak.Myriad、Orsak.AspNetCore、FIO の Http、Sockets、WebSockets、`FIOApp`。
- **負荷の下の挙動は測っていない。** 複数のコアでのスループット、公平性、ブロッキング、FIO のベンチマーク（Pingpong など）は対象外である。
- **FIO のファイバーのメモリの内訳は調べていない。**
- **Orsak が Debug で動的な実装に切り替わることは、Orsak の文書とコストの差からの推定である。** IL は確かめていない。
- **Orsak の `and!` が同時に動くことは、ソースで両方を始めてから待つことを確かめただけで、計測はしていない。**
- **実際のアプリケーションでは試していない。**

## 参考文献

元資料と主な論文は [README の参考文献](../README.md#参考文献)に、ZIO の出典は [ZIO の検証の参考文献](zio-report.md#参考文献)にある。ここには、この文書で引用した資料を挙げる。

**Orsak**（0.7.0、コミット a5ff3da）

- [リポジトリ](https://github.com/JohSand/Orsak)、[ドキュメント](https://johsand.github.io/Orsak)（[Errors](https://johsand.github.io/Orsak/errors.html)、[Scopes](https://johsand.github.io/Orsak/scopes.html)、[Generating code](https://johsand.github.io/Orsak/myriad.html)）、[NuGet](https://www.nuget.org/packages/Orsak)、[変更履歴](https://github.com/JohSand/Orsak/blob/main/CHANGELOG.md)
- [EffectBuilder.fs](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/EffectBuilder.fs)、[Effect.fs](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/Effect.fs)、[Trampoline.fs](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/Trampoline.fs)、[ScopedEffect.fs](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Orsak/ScopedEffect.fs)、[src/Directory.Build.props](https://github.com/JohSand/Orsak/blob/a5ff3daea24f7cc754e4f08c6efc7e74deeb0fa1/src/Directory.Build.props)

**FIO**（0.5.0-beta、コミット fe10c67）

- [リポジトリ](https://github.com/fs-fio/FIO)、[NuGet](https://www.nuget.org/packages/FSharp.FIO)
- [Core.fs](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/Core.fs)、[Factories.fs](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/Factories.fs)、[Extensions.fs](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/Extensions.fs)、[CE.fs](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/DSL/CE.fs)、[InterpreterCore.fs](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/Runtime/InterpreterCore.fs)、[Runtime.fs](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/src/FIO/Runtime/Runtime.fs)
- 開発者向けの文書 [CLAUDE.md](https://github.com/fs-fio/FIO/blob/fe10c67c0277e05f5fd6761b6ca4a871d3a97efe/CLAUDE.md)（0.5.0）、[CLAUDE.md](https://github.com/fs-fio/FIO/blob/eeb01164757a124454628a9b27d2555e817ea1c8/CLAUDE.md)（0.6.0）
