# 移植ノート

[README](../README.md) の補足として、次のことを記録する。README が説明しているエフェクトシステムのモデル、F# での課題と解決、方式の選び方は繰り返さない。

1. 元資料をどう読み、どこを補正したか
2. エンジンごとの実装の要点
3. 6 言語との対応
4. 機能、コスト、スタックの比較と実測値
5. FSharpPlus を既定で使わない理由
6. 制限と未検証の事項

元資料は次の 2 つ。

- [Algebraic Effects と Extensible Effects：6つの言語で学ぶエフェクトシステムの違いと共通点](https://zenn.dev/funnycat/articles/9587e5dc09e4c4)（以下「記事」）
- [OCaml マニュアル 12.24 Effect handlers](https://ocaml.org/manual/5.4/effects.html)（以下「マニュアル」）

文中のコードは同じリポジトリのプロジェクトからの抜粋で（型注釈などを一部省略している）、記事とマニュアルの実行結果を期待値にしたテストが通っている。

## 1. 元資料の分析

### 1.1 記事の骨子

記事は `ask` と `emit` という最小の題材を 6 言語で実装して比較する。

| 分類 | 言語 | 操作の定義 | ハンドラーの書き方 |
|---|---|---|---|
| Algebraic Effects | Koka | `effect` 構文 | `with ctl ask() resume(value)` |
| | Effekt | `interface` | `try { } with Ask[A] { def ask() = resume(a) }` |
| | Unison | `ability` | `Request` のパターンマッチ、再帰は手書き |
| | OCaml | 拡張可能バリアント | `match_with` と `effc` |
| Extensible Effects | Haskell (Polysemy) | GADT | `interpret $ \case Ask -> pure value` |
| | PureScript (Run) | Functor | `interpret (on _ask handleAsk send)` |

記事の比較の軸は、定義の構文、操作呼び出し時の制御の流れ、継続の明示性、再開の回数である。計算を書く側のコードはどの言語でもほぼ同じになる、という観察が記事の核になっている。

### 1.2 マニュアルが足すもの

記事が題材にしていない、実用上の論点はマニュアルにある。

- **用途:** 並行タスク間のメッセージ交換（`run_both`）、ユーザーレベルスレッド（`fork` / `yield` / `xchg`）、制御の反転（push 型の `iter` を pull 型の `Seq` へ変換する `invert`）。
- **`discontinue`:** 中断中の `perform` の位置で例外を発生させ、`finally` を走らせる。「継続は必ず `continue` か `discontinue` する」という規律が示される。
- **線形性の理由:** ワンショットはスタックのコピーが不要で安価であり、ソケットやファイル記述子のような線形リソースの扱いも壊さない。
- **Deep と Shallow:** プロトコルを強制する例 `(Send;Recv)*;Send?` は、処理する操作の集合が時間とともに変わるので Shallow が自然に書ける。
- **制限:** 効果の安全性は静的に保証されない。「少なくとも 1 回」の再開は検査されない。操作はシグナルハンドラーや C からのコールバックを越えられない。

### 1.3 記事を移植の指針にするときの補正

記事は比較として筋が通っているが、そのまま移植の設計に使うと判断を誤る点がある。次の表はソースを確認した結果である。

| 記事の記述 | 確認した内容 | F# への影響 |
|---|---|---|
| Extensible Effects の継続は暗黙で、自動で 1 回だけ再開される。再開しない選択はできない | `interpret` に限った話である。[`Run.Choose.runChoose`](https://github.com/natefaubion/purescript-run/blob/master/src/Run/Choose.purs) は `k true` と `k false` の両方を呼ぶ。[`Run.Except`](https://github.com/natefaubion/purescript-run/blob/master/src/Run/Except.purs) は `Left (Except e) -> pure (Left e)` で継続を呼ばない。[Polysemy の `runError`](https://github.com/polysemy-research/polysemy/blob/master/src/Polysemy/Error.hs) も `Throw` で打ち切る | Run の Functor 形式のままで継続を 0 回でも 2 回でも呼べる。F# でも確認した |
| Algebraic Effects は呼び出すと即ハンドラーへ、Extensible Effects はデータ構造に積まれる | ソース上の意味論というより実装方式の違いである。Koka のコンパイルも[最後はモナド変換になる](https://www.microsoft.com/en-us/research/uploads/prod/2021/08/genev-icfp21.pdf)。[Extensible Effects の論文](https://hackage.haskell.org/package/extensible-effects)自身も、クライアントとハンドラーのコルーチン的な通信と説明している | 同じ操作の宣言を両方式のエンジンで動かせる |
| 比較表に静的検査の行がない | マニュアルは OCaml について「効果の安全性を提供しない」と明記している。OCaml は Algebraic 側なのに検査がなく、Extensible 側の 2 つにはある。分類とは独立した軸である | F# で最も設計を左右する論点になる |
| OCaml は複雑度が高いと感じた | 記事は `match_with` のレコード形式で書いている。マニュアルによれば 5.3 以降の Deep ハンドラーは `try f () with effect Ask, k -> continue k env` と書ける。Shallow には今も構文がない | 比較の基準には簡潔な形を使う |
| 型に依存しないエフェクトには functor が要る | OCaml の事情である。.NET はジェネリクスが実行時に残るので `Ask<'v>` の 1 行で済む | 型引数ごとに別の操作になる。ただしマニュアルの `invert` が局所モジュールで行う「呼び出しごとに新しい操作を作る」ことは、型だけではできない |

## 2. エンジンの実装

### 2.1 Freer: Koka / Unison / Polysemy

計算を「次の操作と、その後の継続」というデータで表す。Extensible Effects の実装方式そのものである。`Eff<'a>` の定義は README の[課題 3](../README.md#課題-3-高カインド型がない)にある。ハンドラーの本体は次の形になる。

```fsharp
// src/EffectSystem/Freer.fs
let rec private deep retc cases m =
    match m with
    | Pure x -> retc x
    | Delayed th -> deep retc cases (th ())
    | Impure(e, k) ->
        match findCase cases e with
        // 扱わない操作は外側へ転送する。
        | null -> Impure(e, (fun r -> Delayed(fun () -> deep retc cases (k r))))
        // Eff.fn は継続を作らず、答えをその場で渡して進む。
        | case when case.AnswersDirectly -> deep retc cases (k (case.Answer e))
        // 再開を Delayed で包むことで run のループへ戻す。末尾再開ならスタックは伸びない。
        | case -> case.Capture(e, (fun r -> Delayed(fun () -> deep retc cases (k r))))
```

継続 `k` はヒープ上の関数なので、何度でも呼べる。再開を `Delayed` で包むのは `.tail` に頼らずスタックを伸ばさないためである。`Eff.fn` の経路は自己末尾呼び出しなので、コンパイラがループにする。Debug かつ `Tailcalls=false` でも、どちらの経路でも 100 万回の操作が通ることを確認した（4.3 節）。

3 言語の書き方をすべて写せる。

```fsharp
// Koka: with ctl ask() resume(value)
let askHandler (value: 'v) (action: Eff<'r>) : Eff<'r> =
    action |> Eff.handle [ Eff.ctl (fun (_: Ask<'v>) resume -> resume value) ]

// Koka: with fun ask() value
let askHandlerFn (value: 'v) (action: Eff<'r>) : Eff<'r> =
    action |> Eff.handle [ Eff.fn (fun (_: Ask<'v>) -> value) ]

// Unison: { Ask.ask -> k } -> handle k v with ask.handler v
let rec askHandler (v: 'v) (m: Eff<'a>) : Eff<'a> =
    Eff.delay (fun () ->
        match Eff.view m with
        | Return a -> Eff.ret a
        | Eff.Op((_: Ask<'v>), k) -> askHandler v (k v)
        | other -> Eff.forward other (askHandler v))

// Polysemy: runAsk value = interpret $ \case Ask -> pure value
let runAsk (value: 'v) (m: Eff<'a>) : Eff<'a> =
    m |> Eff.handle [ Eff.interpret (fun (_: Ask<'v>) -> Eff.ret value) ]
```

Unison の形は再帰を手で書くので、実質 Shallow ハンドラーである。OCaml の `Effect.Shallow` と同じ構造になっている。

### 2.2 Direct: OCaml

普通の関数のまま `perform` するには、中断する間スタックを保存する必要がある。.NET にスタックを切り替える手段はないので、ファイバーごとにスレッドを割り当て、実行権を手渡しする。

```text
呼び出し側のスレッド              ファイバーのスレッド
matchWith ── Resume ──────────▶ f () を実行
   │ 待機                          │
   │ ◀────── Performed(eff) ───── perform: 待機
effc を実行                        │
Deep.resume k v ── Resume ────▶ perform が v を返す
   │ 待機                          ⋮
```

同時に動くスレッドは常に 1 つなのでデータ競合は起きない。マニュアルの例が同じ形で書ける。

```fsharp
// let comp1 () = perform (Xchg 0) + perform (Xchg 1)
let comp1 () = perform (Xchg 0) + perform (Xchg 1)

// try comp1 () with | effect (Xchg n), k -> continue k (n+1)
let basics () : int =
    Deep.tryWith comp1 [ Deep.ctl (fun (Xchg n) k -> Deep.resume k (n + 1)) ]
```

OCaml の意味論はほぼ同じ形で現れる。

| OCaml | Direct | 確認方法 |
|---|---|---|
| `Effect.Unhandled` が `perform` の位置で発生 | `Unhandled` | テスト |
| `Continuation_already_resumed` | `ContinuationAlreadyResumed` | テスト |
| `discontinue` で `finally` が走る | `Deep.discontinue` | テスト |
| 操作は外側のハンドラーへ転送される | 同じ。転送先の `discontinue` も届く | テスト |
| 操作は C のコールバックを越えられない | 操作はスレッド境界を越えられない（`Task.Run` の中で `Unhandled`） | テスト |
| 再開されない継続はファイバーのメモリを漏らす | スレッドが止まったまま残る。`IsBackground` なのでプロセス終了は妨げない | 設計上の帰結 |

### 2.3 Capability: Effekt

Effekt の意味論は[能力渡しへの変換で定義されている](https://publikationen.uni-tuebingen.de/xmlui/handle/10900/123811)。F# でも同じことができ、操作を型で探さず、ハンドラーが作った能力を値として渡す。`askHandler` の実装は README の[課題 4](../README.md#課題-4-行多相がない)にある。

末尾で 1 回再開するだけの操作なら、継続も区切りも要らない。能力のメソッドは `Eff<'a>` を返すだけなので、答えをそのまま返せばよい。

```fsharp
// interface Ask[A] { def ask(): A }
type IAsk<'a> =
    abstract Ask: unit -> Eff<'a>

let askHandlerDirect (a: 'a) (action: IAsk<'a> -> Eff<'r>) : Eff<'r> =
    action
        { new IAsk<'a> with
            member _.Ask() = Eff.ret a }
```

計算から見た結果は同じで、操作のコストは約 5 分の 1 になる（4.2 節）。違いは持ち出した能力の扱いで、区切りがないので `Unhandled` にならず、そのまま答えを返す。

字句スコープの効果もはっきり出る。同じ型のハンドラーを重ねても、呼び先を指定できる。

```fsharp
handlers {
    let! outer = askHandler "outer"
    let! inner = askHandler "inner"
    return! eff {
        let! a = outer.Ask()
        let! b = inner.Ask()
        return (a, b)
    }
}   // ("outer", "inner")。型で探す Freer や OCaml では ("inner", "inner") になる
```

**Rio**

能力の引数を省く `Rio` の型推論は README の課題 4 のとおりである。環境が `IEmit` を実装していなければ、次のエラーになる。

```text
error FS0193: 型 'Env' は型 'IEmit<'a>' と互換性がありません
```

制限が 2 つある。

- **行を 1 段ずつ消せない。** 環境は最後にまとめて具体クラスとして与える。`AppEnv` のように能力の数だけ転送を書く。
- **同じジェネリックインターフェイスの別インスタンスを同時に要求できない。** `IAsk<int>` と `IAsk<string>` を同じ `'R` に要求すると、F# の推論が 1 つに単一化して FS0001 になる。`IAskAge` と `IAskName` のように名前を分ければ通る。

**RecordEnv**

インターフェイス制約を SRTP のメンバー制約に置き換えると、レコードをそのまま環境にできる。転送を書かずに済み、`IAsk<int>` と `IAsk<string>` もフィールドの名前を分ければ同時に要求できる。

```fsharp
let inline ask () : Rio< ^R, 'v> when ^R: (member Ask: IAsk<'v>) =
    Rio(fun env -> (^R: (member Ask: IAsk<'v>) env).Ask())

// フィールドの名前がラベルになる。
let inline askAge () : Rio< ^R, int> when ^R: (member Age: IAsk<int>) =
    Rio(fun env -> (^R: (member Age: IAsk<int>) env).Ask())

askEmit out |> Rio.run {| Ask = ask; Emit = emit |}
```

環境に `Emit` がなければ次のエラーになる。

```text
error FS0001: 型 '{| Ask: IAsk<'a> |}' は演算子 'get_Emit' をサポートしていません
```

行を 1 段ずつ消せない点は Rio と同じである。代わりに、環境を型変数のまま扱う関数はすべて `inline` が要り、`let rec inline` は FS3890 に、抽象メンバーの型は FS0670 になる。操作だけを `inline` にして、組み上げた計算は名前付きレコードで閉じるとこれらを避けられる。この手法の範囲とコストは[検証レポート](row-polymorphism-report.md)にある。

### 2.4 StaticRow: 行を 1 段ずつ消す

Koka のようにハンドラーごとに行から 1 つ消える型付けは、エフェクトごとに型引数を 1 つ割り当てれば、F# の単一化だけで得られる（Rémy の行の符号化に近い手法）。

```fsharp
type Row<'ask, 'emit, 'a> = private Row of Eff<'a>

let ask<'v, 'emit> () : Row<Needs<'v>, 'emit, 'v> = Row(Eff.perform Ask)
let emit<'ask, 'v> (v: 'v) : Row<'ask, Needs<'v>, unit> = Row(Eff.perform (Emit v))

let runAsk (value: 'v) (Row m: Row<Needs<'v>, 'emit, 'a>) : Row<Handled, 'emit, 'a> = ...
let run (Row m: Row<Handled, Handled, 'a>) : 'a = Eff.run m
```

推論の結果とエラーは Koka にかなり近い。

```text
val askEmit: unit -> Row<Needs<'a>,Needs<'a>,unit>
val askOnly: unit -> Row<Needs<string>,'a,int>        使わないスロットは多相のまま

askEmit () |> runAsk "Ask Value" |> run
error FS0001: 型が一致しません。
    'Row<Handled,Needs<string>,unit> -> 'a'    という指定が必要ですが、
    'Row<Handled,Handled,'b> -> 'b'            が指定されました。
    型 'Handled' は型 'Needs<string>' と一致しません
```

ハンドラーの順序は自由で、二重に処理すると型エラーになる。ただし型引数の並びをアプリケーションが決めるので、エフェクトを足すたびに `Row` とすべてのシグネチャを変える必要がある。閉じた小さな領域で、ハンドラーの付け忘れを型で防ぎたい場合に限って使う。

### 2.5 Async one-shot

`Async.FromContinuations` の `(ok, err, cancel)` と OCaml の継続の対応は README の[課題 1](../README.md#課題-1-継続を取り出す手段がない)のとおりである。`err` で再開すると `try ... finally` が走り、2 回目の再開は FSharp.Core の検査で `InvalidOperationException` になる。

```fsharp
let capture (f: Continuation<'a> -> unit) : Async<'a> =
    Async.FromContinuations(fun (ok, err, _) -> f { Resume = ok; Discontinue = err })
```

マニュアル 24.3 のスケジューラーが、キューを回すスレッド 1 本だけで動き、出力の順序も一致する（[AsyncOneShot.fs](../samples/EffectSystem.Samples/AsyncOneShot.fs)）。操作は `Scheduler` のメソッドなので、スコープの仕組みは能力渡しと同じである。

落とし穴を 1 つ踏んだ。**`Async` の実行中のスレッドから継続を再開すると、スレッドプールへ回されて順序が崩れる。** Expecto が `Async` の上でテストを動かすために発覚した。スケジューラーのキューを専用スレッドで回して解決している。

### 2.6 FSharpPlus: PureScript Run

F# にも PureScript にも GADT がないので、`AskF v a = Ask (v -> a)` という Functor の形はそのまま移植できる。

```fsharp
// data AskF v a = Ask (v -> a)
type AskF<'v, 'next> =
    | Ask of ('v -> 'next)

    static member Map(Ask k, f: 'next -> 'u) = Ask(k >> f)

// runAsk :: v -> Run (ASK v + r) ~> Run r
let rec runAsk (value: 'v) (m: Free<AskEmitLogF<'v, unit>, unit>) : Free<EmitLogF<'v, unit>, unit> =
    match Free.run m with
    | Choice1Of2() -> Free.Pure()
    | Choice2Of2(AskOp(Ask next)) -> runAsk value (next value)
    | Choice2Of2(Rest r) -> Roll(EmitLogF<'v, _>.Map(r, runAsk value))
```

型から `AskOp` が 1 段消えるので、処理の進行を型で追える。`next` はただの関数なので、2 回呼べばマルチショットになり、呼ばなければ打ち切りになる。行多相の部分は手書きの和で代用している。

## 3. 6 言語との対応

| 言語 | F# | ファイル | 再現できる点 | 再現できない点 |
|---|---|---|---|---|
| Koka | Freer | [KokaStyle.fs](../samples/EffectSystem.Samples/KokaStyle.fs) | `ctl` / `fun`、再開の回数、再開可能な例外 | 行多相、型からのエフェクトの消去 |
| Effekt | Capability | [EffektStyle.fs](../samples/EffectSystem.Samples/EffektStyle.fs) | `interface`、`with` 糖衣、字句スコープ | [能力の持ち出しの静的な禁止](https://effekt-lang.org/tour/captures)（F# では実行時に `Unhandled`） |
| Unison | Freer の `view` | [UnisonStyle.fs](../samples/EffectSystem.Samples/UnisonStyle.fs) | `Request` のパターン、手書きの再帰 | ability の型 |
| OCaml | Direct | [OCamlStyle.fs](../samples/EffectSystem.Samples/OCamlStyle.fs)、[OCamlManual.fs](../samples/EffectSystem.Samples/OCamlManual.fs) | 直接スタイル、1 回限りの再開、`discontinue`、Shallow、マニュアルの全例 | 低いコスト、呼び出しごとに新しい操作 |
| Polysemy | Freer の `interpret` | [PolysemyStyle.fs](../samples/EffectSystem.Samples/PolysemyStyle.fs) | 継続の見えない `interpret`、組み立てと実行の分離 | `Member` による行 |
| Run | FSharpPlus の `Free` | [PureScriptStyle.fs](../samples/EffectSystem.Samples/PureScriptStyle.fs)、[RunStyle.fs](../src/EffectSystem.Plus/RunStyle.fs) | Functor の形、行が 1 段消える型 | 行多相（手書きの和で代用） |

記事の出力との差は 1 点だけある。PureScript の出力は `show` により `Emit "Ask Value"` と引用符が付くが、F# 版では付けていない。

## 4. 比較と実測

### 4.1 機能

| | Freer | Capability + Rio | RecordEnv | StaticRow | Direct | Async one-shot | FSharpPlus Free |
|---|---|---|---|---|---|---|---|
| 依存 | なし | なし | なし | なし | なし | なし | FSharpPlus |
| 計算の書き方 | CE | CE | CE | CE | **普通の関数** | `async` | `monad` CE |
| 再開の回数 | 多 | 多 | 多 | 多 | 1 | 1 | 多 |
| ハンドラーの付け忘れ | 実行時 | **コンパイル時** | **コンパイル時** | **コンパイル時** | 実行時 | **コンパイル時** | **コンパイル時**（閉じた和） |
| 行が 1 段ずつ消える | なし | なし | なし | **あり** | なし | なし | **あり**（手書き） |
| 新しいエフェクトの追加 | 型を足すだけ | 型を足すだけ | 型を足すだけ | 全シグネチャの変更 | 型を足すだけ | メソッドを足すだけ | 和の型の変更 |
| ハンドラーの探索 | 最も近い型 | 能力 | 能力 | 最も近い型 | 最も近い型 | 能力 | 和の位置 |
| 同じ計算の再実行 | できる | できる | できる | できる | 関数なのでできる | できる | できる |
| `try` / `finally` | CE で対応 | Freer に準ずる | Freer に準ずる | Freer に準ずる | **そのまま使える** | **そのまま使える** | `Free` に定義なし |

### 4.2 コスト

操作 1 回あたりのおおよその時間を示す。Ryzen 7 8745H（16 論理プロセッサ）、.NET 10.0.13、Release ビルドで測った。全コアの列は 3 回の実行の幅で、1 コアの列は `ProcessorAffinity` で固定した 1 回の値である。厳密なベンチマークではなく、桁の違いを見るためのものとして読むこと。

| エンジン | 全コア | 1 コア固定 |
|---|---|---:|
| インターフェイス呼び出し（基準、JIT に最適化されている可能性あり） | 0.2 ns | 0.2 ns |
| Capability、`Eff.ret` で答える | 25–31 ns | 25 ns |
| RecordEnv、`Eff.ret` で答える | 56–74 ns | 60 ns |
| Freer + `fn` | 57–59 ns | 111 ns |
| Rio、`Eff.ret` で答える | 59–75 ns | 96 ns |
| FSharpPlus Free | 92–111 ns | 120 ns |
| Freer + `ctl` | 121–133 ns | 175 ns |
| Capability、`shift` で答える | 124–140 ns | 202 ns |
| RecordEnv、`shift` で答える | 153–181 ns | 195 ns |
| Rio、`shift` で答える | 154–185 ns | 203 ns |
| Async one-shot | 213–227 ns | 252 ns |
| Direct + `fn` | 374–468 ns | 1,812 ns |
| Direct + `ctl` | 455–577 ns | 1,852 ns |
| Direct のファイバー生成と終了 | 75–95 µs | 82 µs |
| Freer、左ネスト深さ 1,000 | 21.3–24.9 µs | 20.4 µs |
| Freer、左ネスト深さ 4,000 | 93.9–113.9 µs | 93.8 µs |
| Freer、末尾の `do!` の再帰、深さ 1,000 | 122–182 ns | 143 ns |
| Freer、末尾の `do!` の再帰、深さ 4,000 | 128–181 ns | 146 ns |

読み取れることは次のとおり。

- **Direct の切り替えは予想より速いが、生成は重い。** 全コアで約 400〜600 ns なのは、`SemaphoreSlim` のスピン待ちで済んでいるためと考えられる（推定）。1 コアでは約 4 倍になる。一方、スレッドを作るファイバーの生成は約 80 µs かかるので、タスクを大量に作る用途には向かない。
- **Async one-shot の数字は最も速い経路のものである。** このベンチマークは継続をコールバックの中で同期的に再開している。スケジューラーのように後から再開する場合は、これより遅くなると考えられる（未測定）。
- **`fn` は `ctl` の約半分で済む。** `fn` は継続のクロージャと `Delayed` を作らず、答えをその場で渡すからである。記事によれば Koka でも `fun` のほうが効率的で、同じ方向の差がこの実装でも出ている。
- **末尾で 1 回再開するだけの能力に `shift` は要らない。** `Eff.ret` で答えると継続を取り出さないので、コストが約 5 分の 1 になる（2.3 節）。
- **環境を挟むと Capability より遅くなる。** Rio は `shift` で約 1.3 倍、`Eff.ret` で約 2.4 倍かかる。RecordEnv は Rio と差がない。メンバー制約はコンパイル時に解決されるからである。
- **FSharpPlus Free の数字は同じ条件ではない。** このベンチマークはハンドラーの型による検索も遅延の足場も持たない、1 エフェクト専用の手書きループである。速さの比較に使わないこと。
- **Freer は左ネストに弱い。** 深さを 4 倍にすると操作あたりのコストが約 4.5 倍になるので、全体は深さの 2 乗に近い。Kiselyov と Ishii の論文は[継続を型整列キューで持つ](https://tagteam.harvard.edu/hub_feeds/1936/feed_items/2126301)ことでこれを解決するが、この実装には入れていない。末尾の `do!` で再帰する形だけは、`ReturnFromFinal` が `bind` を通らないので、コストが深さに比例しない。

### 4.3 スタック

1 MB のスタックで実行した結果を示す。○は完走、×はスタックオーバーフローである。数字は試した操作の回数または再帰の深さで、限界を細かく探したものではない。

| 計算 | Release（`Tailcalls=true`） | Debug（`Tailcalls=true`、このプロジェクトの既定） | Debug（`Tailcalls=false`） |
|---|---|---|---|
| 通常の非末尾再帰 | 2 万○ / 10 万× | 5 千○ / 2 万× | 5 千○ / 2 万× |
| Freer + `fn` | 100 万○ | 100 万○ | 100 万○ |
| Freer + `ctl`、末尾再開 | 100 万○ | 100 万○ | 100 万○ |
| Freer、末尾の `do!` の再帰 | 100 万○ | 100 万○ | 100 万○ |
| Freer、左ネスト | 2 万○ / 10 万× | 5 千○ / 2 万× | 5 千○ / 2 万× |
| Direct + `fn` | 10 万○ | 10 万○ | 10 万○ |
| Direct + `ctl` | 10 万○ | 10 万○ | 千○ / 5 千× |

- Freer の `fn`、末尾再開、末尾の `do!` は `.tail` に依存しない。
- Freer の左ネストの限界は、通常の非末尾再帰と同じ桁である。直接スタイルの再帰でも同じ制約があるので、Freer 固有の欠点とまでは言えない。
- Direct + `ctl` は `.tail` に依存する。`Tailcalls=true` を [Directory.Build.props](../Directory.Build.props) に入れているのはこのためである。

## 5. FSharpPlus を使うか

既定では使わない。代数的エフェクトの本質である継続の操作には、F# の判別共用体と関数だけで足りるからである。

**得られるもの**

- `Free.fold` で同じ記述を任意のモナドへ畳み込める。F# 版では `Writer` への解釈を 1 行で書けた。
- FSharpPlus には `StateT` などの変換子もあるので、すでに FSharpPlus を使っているコードベースなら、それらへ解釈できる利点がある（このリポジトリで試したのは `Writer` だけである）。

**解決しないもの**

- 行多相の代わりにはならない。

**実際に踏んだ問題**（FSharpPlus 1.9.1、F# 10）

- **入れ子の `Coproduct` が実行時に失敗する。** `Free<Coproduct<A<'v, _>, Coproduct<B, C>>, _>` を、ジェネリックな `'v` のまま右の経路で `Free.liftF` すると、コンパイルは通るのに `NotSupportedException: Dynamic invocation of Map is not supported` が発生した。`Optimize=false` で再現し、`Optimize=true` では再現しない（構成名ではなく最適化の有無で決まる）。型引数を具体型にするか、手書きの和を使えば起きない。原因までは特定していない。このリポジトリでは手書きの和を使い、挙動をテストで固定している。
- **注釈が増える。** `InL` は反対側の型を決めないので、SRTP の `Map` が解決できず FS0071 になる。
- **`monad` CE は先頭区間を遅延しない。** `Free.Delay` は `x ()` なので、最初の `let!` より前のコードは組み立て時に走る。Freer の `eff` は実行時まで遅延する。テストで両方を固定した。

## 6. 制限と未検証の事項

**F# の型では表せないもの**

- どのエンジンでも、`printfn` のような通常の副作用は型に現れない。Koka の `console` のようには追跡できない。
- 型で探す方式（Freer、Direct）では、要素型が同じ `invert` を入れ子にすると操作が混ざる。外側の `push` を内側のハンドラーが受ける。OCaml なら `[1; 20]` になるはずの計算が `[10; 20]` になる。この `[1; 20]` はマニュアルの意味論から導いた値で、OCaml で実行してはいない。

**Direct を本番で使う場合の注意**

- ファイバーごとにスレッドを消費する。再開されない継続のスレッドはプロセスが終わるまで止まったままである。OCaml の `Gc.finalise` に当たる後始末は実装していない。
- ファイバーの中は別スレッドなので、`ThreadStatic` の値や UI スレッドへの依存は引き継がれない。
- 検証は小規模な例と単一のハンドオフの計測のみで、多数のファイバーを同時に動かす負荷では評価していない。

**検証の範囲**

- 6 言語の処理系は実行していない。比較の対象は記事とマニュアルに掲載された出力で、README の図3の位置づけもこれらとソースの記述による。
- Polysemy と Run の継続の扱いはソースを読んで確認したもので、Haskell と PureScript では実行していない。図3で両者を「何回でも」に置いたのはこの結果による。
- 105 件のテストは Debug と Release の両方で通っている。コンパイルエラーの検証は `dotnet fsi` で行い、通るスクリプトを対照群として並べて、失敗の理由が狙いどおりであることを確かめている。
- SRTP で行を型レベルのリストとして探索する方式は採用していない。開いた行を注釈できず（FS0043）、コンパイル時間が行の深さに対して指数的に伸びる（[検証レポート](row-polymorphism-report.md#9-判断)）。

## 参考文献

元資料と主な論文は [README の参考文献](../README.md#参考文献)にある。ここには、確認に使ったソースコードと、この文書で引用した資料を挙げる。

**確認に使ったソースコード**

- [purescript-run: Run/Choose.purs](https://github.com/natefaubion/purescript-run/blob/master/src/Run/Choose.purs)、[Run/Except.purs](https://github.com/natefaubion/purescript-run/blob/master/src/Run/Except.purs)
- [polysemy: Polysemy/Error.hs](https://github.com/polysemy-research/polysemy/blob/master/src/Polysemy/Error.hs)、[Polysemy.NonDet](https://hackage.haskell.org/package/polysemy/docs/Polysemy-NonDet.html)
- [FSharpPlus: Data/Free.fs](https://github.com/fsprojects/FSharpPlus/blob/master/src/FSharpPlus/Data/Free.fs)、[Data/Coproduct.fs](https://github.com/fsprojects/FSharpPlus/blob/master/src/FSharpPlus/Data/Coproduct.fs)

**論文と解説**

- Kiselyov, Sabry, Swords, [Extensible Effects: An Alternative to Monad Transformers](https://hackage.haskell.org/package/extensible-effects)（リンク先は実装パッケージ）
- Kiselyov, Ishii, [Freer Monads, More Extensible Effects](https://tagteam.harvard.edu/hub_feeds/1936/feed_items/2126301)（リンク先は紹介記事）
- Xie, Leijen, [Generalized Evidence Passing for Effect Handlers](https://www.microsoft.com/en-us/research/uploads/prod/2021/08/genev-icfp21.pdf), ICFP 2021
- Xie ほか, [Effect Handlers, Evidently](https://www.microsoft.com/en-us/research/?p=671076), ICFP 2020（末尾再開の操作をその場で実行する最適化）
- [Effekt Language Tour: Captures](https://effekt-lang.org/tour/captures)
