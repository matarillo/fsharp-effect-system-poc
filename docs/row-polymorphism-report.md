# 行多相の模倣の検証

[README の課題 4](../README.md#課題-4-行多相がない) の補足として、SRTP で F# の行多相を模倣する手法がこのプロジェクトに使えるかを検証した結果を記録する。

1. 結論
2. F# で模倣できる範囲
3. RecordEnv: レコードを環境にする
4. ListRow と EnvRow: 行の型レベル探索
5. F# 11 の Record spreads
6. 実際のビルドでの挙動
7. コスト
8. 方式の比較
9. 判断
10. 未検証の事項

RecordEnv は [RecordEnv.fs](../samples/EffectSystem.Samples/RecordEnv.fs) にあり、コンパイルエラーの主張は [tests/compile-fail/](../tests/compile-fail/) の `record-*.fsx` で固定している。ListRow と EnvRow は採用していないのでリポジトリにはなく、要点のコードをこの文書に載せる。文中の F# のコードは、抜き出して動くことを確かめている。

## 1. 結論

SRTP のメンバー制約は、行を「閉じる側」の手間を減らせるが、行を「消す側」の問題は解決しない。エンジンと既定の方式（Capability + Rio）は変えない。

| 手法 | 解決するもの | 解決しないもの | 判断 |
|---|---|---|---|
| **RecordEnv**: レコードを環境にしてメンバー制約で受ける | `AppEnv` の転送クラス、Capability の引数の増加、`IAsk<int>` と `IAsk<string>` の同時要求（ラベルを分ける） | 行を 1 段ずつ消す（FS3245） | サンプルとして使う |
| **ListRow / EnvRow**: 行を型レベルのリストにして SRTP で探索する | 行を 1 段ずつ消す、StaticRow のシグネチャ変更、同じ操作の別インスタンスの同時要求 | 開いた行の注釈、多相な回答型、深い行のコンパイル時間 | 採用しない |
| **Record spreads**（F# 11） | 具体型の環境の合流、`AppEnv` の転送 | 行変数（FS3895、FS3891） | .NET 11 へ移るときに使う |

判断を左右した実測は次の 3 つである。

- **行を消す汎用関数は書けない。** `{| env with Ask = ask |}` は FS3245 になり、F# 11 の `{| ...env; Ask = ask |}` も FS3895 になる。これはハンドラーが行う操作そのものである。
- **型レベル探索のコンパイル時間は、行の深さに対して指数的に伸びる。** Rio と RecordEnv が 40 エフェクトで約 3 秒なのに対し、深さ 12 で 11〜129 秒、深さ 14 で 53 秒以上かかる（[7.1 節](#71-行の深さとコンパイル時間)）。
- **`inline` を重ねるとコードが倍々に増える。** 合成の深さ 12 で、DLL は Rio の 16 KB に対して 52 MB になる。環境を名前付きレコードで閉じれば 18 KB で済む（[7.2 節](#72-合成の深さとコードの大きさ)）。

## 2. F# で模倣できる範囲

F# に行多相はなく、言語提案（[fslang-suggestions #11](https://github.com/fsharp/fslang-suggestions/issues/11)）は `declined` である。[RFC FS-1151](https://github.com/fsharp/fslang-design/blob/main/RFCs/FS-1151-record-spreads.md) は、この提案を .NET の型システムの制約の中では実装できないものとして挙げている。模倣の手段は SRTP のメンバー制約で、できることは次の範囲である。

| 操作 | 結果 |
|---|---|
| 特定のフィールドを持つ任意のレコードを受け取る | できる |
| 複数のフィールドを要求する | できる |
| 元の型を保って返す | できる |
| 残りのフィールドを表す行変数を宣言する | できない（`{ ...'r; ... }` は FS3891） |
| 任意のレコードにフィールドを足す汎用関数 | できない（FS3245、spread でも FS3895） |
| 行多相な型を普通の型引数として扱う | できない（抽象メンバーの型は FS0670） |
| 拡張メンバーで制約を満たす | できない（FS0193） |

[Polymorphwhat?](https://medium.com/@pmbanka/polymorphwhat-e3b36bbb2f99) は SRTP を F# の行多相として紹介し、インターフェイスに対する利点を 2 つ挙げている。所有していない型にも使えることと、元の型を保って返せることである。ただし、所有していない型に使えるのは、そのメンバーをすでに持っている場合に限る。同じ記事が欠点に挙げるとおり拡張メソッドでは制約を満たせず、F# 10 と F# 11 でも同じである（`--langversion:preview` を含む）。

## 3. RecordEnv: レコードを環境にする

[Rio](../README.md#課題-4-行多相がない) の `'R :> IAsk<'v>` をメンバー制約に置き換える。`Rio` と `rio { }` はそのまま使える。

```fsharp
let inline ask () : Rio< ^R, 'v> when ^R: (member Ask: IAsk<'v>) =
    Rio(fun env -> (^R: (member Ask: IAsk<'v>) env).Ask())

let inline emit (v: 'v) : Rio< ^R, unit> when ^R: (member Emit: IEmit<'v>) =
    Rio(fun env -> (^R: (member Emit: IEmit<'v>) env).Emit v)

let inline askEmit (out: Log) =
    rio {
        out "Start"
        let! v = ask ()
        do! emit v
        out "End"
    }

// AppEnv のクラスも転送も要らない。余分なフィールドがあってもよい。
handlers {
    let! ask = askHandler "Ask Value"
    let! emit = emitHandler out
    return! askEmit out |> Rio.run {| Ask = ask; Emit = emit |}
}
|> Eff.run
```

推論される型は Rio と同じくらい読める。

```text
val inline askEmit<^a,'b when ^a: (member Ask: IAsk<'b>) and ^a: (member Emit: IEmit<'b>)> :
  out: Log -> Rio<^a,unit>
```

アクティブパターンに制約を隠すと（[Phillip Carter の回答](https://stackoverflow.com/questions/67892986/partial-anonymous-record-in-f)）、`Rio` なしで Capability の引数を 1 つにまとめられる。

```fsharp
let inline (|HasAsk|) (env: ^R) = (^R: (member Ask: IAsk<'a>) env)
let inline (|HasEmit|) (env: ^R) = (^R: (member Emit: IEmit<'a>) env)

// Effekt の def askEmit[A](): Unit / { Ask[A], Emit[A] } に当たる。必要な能力が引数のパターンに現れる。
let inline askEmitCapability (HasAsk ask & HasEmit emit) (out: Log) =
    eff {
        out "Start"
        let! v = ask.Ask()
        do! emit.Emit v
        out "End"
    }

handlers {
    let! ask = askHandler "Ask Value"
    let! emit = emitHandler out
    return! askEmitCapability {| Ask = ask; Emit = emit |} out
}
|> Eff.run
```

Rio との違いは次のとおりである。

| | Rio | RecordEnv | 確認 |
|---|---|---|---|
| 環境 | インターフェイスを実装するクラス | レコード、または同名のプロパティを持つ型 | `record-all-provided.fsx` |
| 環境に能力がないとき | FS0193 | FS0001（`型 '{\| Ask: IAsk<'a> \|}' は演算子 'get_Emit' をサポートしていません`） | `record-missing-emit.fsx` |
| `IAsk<int>` と `IAsk<string>` を同時に要求 | インターフェイスを分ける | フィールドの名前を分ける | `record-two-labels.fsx` |
| 同じ名前で別の型を要求 | FS0001 | FS0001 | `record-same-label.fsx` |
| `inline` | 不要 | 環境を型変数のまま扱う関数すべてに要る | `record-no-inline.fsx` |
| トップレベルの再帰 | できる | `let rec inline` は FS3890。局所の `let rec` か、閉じた環境なら書ける | `record-rec-inline.fsx`、`record-local-rec.fsx` |
| 能力を足して残りを渡す | 残りを転送クラスに列挙すれば書ける | 残りが具体型のときだけ書ける（型変数なら FS3245） | `rio-local-all-provided.fsx`、`record-concrete-extend.fsx`、`record-generic-extend.fsx` |
| 抽象メンバーの型に書く | できる | FS0670 | `rio-abstract-member.fsx`、`record-abstract-member.fsx` |
| 2 種類の環境で動かす | 書けない（ランク 2） | 書けない（FS0001） | `record-rank2.fsx` |
| 拡張メンバーで能力を後付けする | | FS0193 | `record-extension-member.fsx` |

`inline` を忘れた場合は、定義ではエラーにならない。最初の使用で環境の型が固定され、別の環境で使ったところで FS0001 になる。

実行コストは Rio と差がなく（[7.4 節](#74-実行コスト)）、コンパイル時間も 40 エフェクトまで平坦である（[7.1 節](#71-行の深さとコンパイル時間)）。

## 4. ListRow と EnvRow: 行の型レベル探索

行を型レベルのリストで持ち、SRTP のオーバーロード解決で要素を探す方式である。

- **ListRow:** Freer の計算に幽霊型の行を付ける。StaticRow の位置のスロットをリストに置き換えた形である。
- **EnvRow:** 同じ探索で、実体のある環境 `Cons<'h, 't>` から能力を取り出す。既存の `Rio` の上に載る。

行の先頭に足すだけなら普通のジェネリック関数で書けるので、ハンドラーに `inline` は要らない。

```fsharp
type Nil = class end
type Cons<'h, 't> = class end

// 行 'r に操作 'e が含まれるという証拠。here と there でしか作れない。
[<Struct; NoEquality; NoComparison>]
type Has<'r, 'e> = private { Dummy: unit }

module Has =
    let here<'e, 't> : Has<Cons<'e, 't>, 'e> = { Dummy = () }
    let there (_: Has<'t, 'e>) : Has<Cons<'x, 't>, 'e> = { Dummy = () }

type Find =
    static member Find(_: Cons<'e, 't>, _: 'e, _: Find) : Has<Cons<'e, 't>, 'e> = Has.here

    static member inline Find(_: Cons<'x, ^t>, e: ^e, m: ^m) : Has<Cons<'x, ^t>, ^e> =
        Has.there (((^m or ^t or ^e): (static member Find: ^t * ^e * ^m -> Has< ^t, ^e >)
                                          (Unchecked.defaultof< ^t >, e, m)))

let inline callFind (m: ^m) (r: ^r) (e: ^e) : Has< ^r, ^e > =
    ((^m or ^r or ^e): (static member Find: ^r * ^e * ^m -> Has< ^r, ^e >) (r, e, m))

let inline evidence () =
    callFind Unchecked.defaultof<Find> Unchecked.defaultof<_> Unchecked.defaultof<_>

type Row<'r, 'a> = private Row of Eff<'a>

// inline ではないので、Row のコンストラクターを隠したままにできる。
let performWith (_: Has<'r, 'e>) (op: 'e when 'e :> Effect<'x>) : Row<'r, 'x> = Row(Eff.perform op)
let inline perform (op: ^e) : Row< ^r, 'x> = performWith (evidence ()) op
let inline ask () = perform Ask
let inline emit v = perform (Emit v)

// 行を 1 段消す。inline は要らない。
let runFn (f: 'e -> 'x) (Row m: Row<Cons<'e, 'r>, 'a>) : Row<'r, 'a> when 'e :> Effect<'x> =
    Row(m |> Eff.handle [ Eff.fn f ])

let runAsk (value: 'v) (m: Row<Cons<Ask<'v>, 'r>, 'a>) : Row<'r, 'a> =
    runFn (fun (_: Ask<'v>) -> value) m

let runEmit (out: 'v -> unit) (m: Row<Cons<Emit<'v>, 'r>, 'a>) : Row<'r, 'a> =
    runFn (fun (Emit(v: 'v)) -> out v) m

let run (Row m: Row<Nil, 'a>) : 'a = Eff.run m
```

`row { }` は [StaticRow.fs](../samples/EffectSystem.Samples/StaticRow.fs) と同じ形のビルダーに、`ReturnFromFinal` を足したものである。

```fsharp
let inline askEmit () =
    row {
        let! (v: string) = ask ()
        do! emit v
    }

askEmit () |> runAsk "a" |> runEmit log.Add |> run
askEmit () |> runEmit log.Add |> runAsk "b" |> run    // 順序は自由
```

`(^m or ^r or ^e)` の `^e` は省けない。同じ `^r` に同名の制約が 2 つ付くと、F# はサポート型が同じ制約を単一化するからである（[ConstraintSolver.fs の `TraitsAreRelated`](https://github.com/dotnet/fsharp/blob/main/src/Compiler/Checking/ConstraintSolver.fs)）。同じ箇所のコメントにある「型変数の集合は、同名の 2 つのトレイト制約も、同じインターフェイスの別のインスタンスによる制約も持てない」が、`record-same-label.fsx` と `rio-two-instantiations.fsx` の原因でもある。

### 4.1 動くもの

| 確かめたこと | 結果 |
|---|---|
| 行を 1 段ずつ消す | 通る。ハンドラーの順序は自由 |
| ハンドラーの付け忘れ | コンパイルエラー（F# 10 は FS0001、F# 11 は FS0041） |
| `Ask<int>` と `Ask<string>` の同時要求（Koka の `<ask<int>, ask<string>>`） | 通る |
| 継続を 2 回呼ぶハンドラー、回答型の変更、順序による意味の違い | [Advanced.fs](../samples/EffectSystem.Samples/Advanced.fs) と同じ結果 |
| Capability のマルチショット（EnvRow） | 記事の Effekt の出力と一致 |
| 残りの行の操作を使うハンドラー（Polysemy の `reinterpret`） | 通る。行になければコンパイルエラー |
| 再帰 | 局所の `let rec`、または証拠を引数で渡す形で通る |
| 使うエフェクトが違う計算の合成 | 通る。操作の型を足しても `Row` と既存のシグネチャは変わらない |
| 同じ型のハンドラーを 2 回被せる | 曖昧さで拒否される |
| `Row` のコンストラクターの偽造 | FS1093 で拒否される |

付け忘れのエラーは長いが、`Nil` と `Emit<string>` が読み取れる（抜粋）。

```text
error FS0001: 型が一致しません。 'Row<Nil,unit> -> 'a' という指定が必要ですが、'Row<Nil,'b> -> 'b' が指定されました。
メソッド 'Find' に一致するオーバーロードはありません。
既知の型パラメーター: < Nil , EffectSystem.Samples.Effects.Emit<string> , Find >
```

### 4.2 動かないもの

| 確かめたこと | 結果 | 回避策 |
|---|---|---|
| Koka の `<ask<string>\|e>` に当たる `Row<Cons<Ask<string>, 'r>, _>` の注釈 | **FS0043** | なし |
| 行多相の親が局所的にハンドラーを被せる | **FS0043** | 親の行を閉じる、証拠を手で渡す、Capability を使う |
| 回答型が多相のままの計算 | **FS0043** | 型を指定する引数を足す。Effekt の `askEmit[String]()` に近い |
| `inline` でないジェネリック関数の中の探索 | **FS0043** | 証拠を手で渡す |
| 行を具体型で注釈した計算のハンドラーの順序変更 | FS0001 | 注釈しない |

原因はどれも同じで、行の末尾や回答型が未確定だと 2 つのオーバーロードがどちらも候補に残り、解決できない。Polysemy が `@Type` の注釈を求めるのと同じ構造である。

優先度付きのオーバーロードと、早期解決を防ぐダミーのオーバーロードを足すと、開いた行の注釈と局所ハンドラーは通る。しかし `^r` が定義の時点で最初の操作に固定され、順序の自由と合成が失われる。両立する形は見つかっていない。

### 4.3 型の穴

- `Unchecked.defaultof` は証拠を偽造でき、実行時に `Unhandled` になる。明示的に危険な関数を使ったときだけなので、許容できる。
- 分岐のリストを受ける関数は、行の先頭と分岐の操作の型を結びつけない。上の `runFn` のように、操作の型を行の先頭へ結びつける API だけを公開する必要がある。

## 5. F# 11 の Record spreads

[F# 11 の Record spreads](https://github.com/fsharp/fslang-design/blob/main/RFCs/FS-1151-record-spreads.md) を、SDK 11.0.100-rc.3 で確かめた。

| 確かめたこと | 結果 |
|---|---|
| `let provideAsk (env: 'R) = {\| ...env; Ask = ask \|}` | FS3895 |
| `inline` と `^R` にする、メンバー制約を付ける | FS3895 |
| `type WithAsk<'r, 'a> = { ...'r; Ask: IAsk<'a> }` | FS3891 |
| 残りの環境が具体型なら spread で能力を足す | 通る |
| 2 つの具体的な環境を `{\| ...readers; ...writers \|}` で合流する | 通る |
| `type AppEnv<'a> = { ...Readers<'a>; ...Writers<'a> }` | 通る |

型の spread を使うと、`AppEnv` の転送も、環境の型を部品から組み立てる手間もなくなる。一方で、spread の元はレコード型と分かっていなければならないので、行変数の代わりにはならない。

この文書と [tests/compile-fail/](../tests/compile-fail/) の主張は、F# 11 でも同じ結果になる。違いはエラーコードだけで、ListRow と EnvRow の付け忘れが FS0001 から FS0041 に変わる。

## 6. 実際のビルドでの挙動

`TreatWarningsAsErrors` を有効にした 3 プロジェクト構成（本体、ライブラリ、アプリケーション）で、ライブラリの `inline` をアプリケーションから使って確かめた。

| 確かめたこと | Debug（`Optimize=false`） | Release |
|---|---|---|
| `inline` の計算をアセンブリの外で展開（RecordEnv、EnvRow、ListRow。ListRow は深さ 6 まで） | 動く | 動く |
| `inline` でないジェネリック関数に、証拠を手で渡す | 動く | 動く |
| 警告 | なし | なし |
| 公開の `inline` の中で `private` のコンストラクターを使う | 通る | **FS1116、FS1118** |
| `private` のアクティブパターン | 動く | 動く |
| `inline` の本体をリフレクションで呼ぶ | `NotSupportedException` | `NotSupportedException` |

- **実行時の失敗はコンパイル時に止まる。** FSharpPlus の `Coproduct` は同じ形で実行時に落ちるが（[移植ノートの 5 節](porting-notes.md#5-fsharpplus-を使うか)）、`'v` を残したジェネリック関数の探索は FS0043 で止まる。
- **`inline` の中で隠したコンストラクターを使うと、Debug と Release で結果が変わる。** Debug と `dotnet fsi` では通るので、Release をビルドするまで分からない。原因は追っていない。`Has` のように証拠を挟み、コンストラクターを使う部分を `inline` の外へ出す必要がある。
- **匿名レコードの型はアセンブリごとに別物である。** メンバー制約で受ける分には問題ないが、ライブラリの匿名レコードとアプリケーションの匿名レコードを同じ型として扱うと、異なるアセンブリの匿名レコードだという FS0001 になる。環境の型をシグネチャに書くなら、名前付きのレコードにする。

1 MB のスタックで 100 万回の操作を実行した結果は次のとおりである。○は完走で、いずれも末尾再帰の形である。左ネストは測っていないが、Freer の性質がそのまま残ると考えられる。

| 計算 | Release | Debug（`Tailcalls=true`） | Debug（`Tailcalls=false`） |
|---|---|---|---|
| ListRow、局所の `let rec` | ○ | ○ | ○ |
| ListRow、証拠を渡す再帰 | ○ | ○ | ○ |
| RecordEnv | ○ | ○ | ○ |
| EnvRow | ○ | ○ | ○ |

## 7. コスト

Ryzen 7 8745H（16 論理プロセッサ）、.NET 10.0.13、F# 10（SDK 10.0.402）で測った。厳密なベンチマークではなく、伸び方を見るためのものとして読むこと。7.1 から 7.3 の時間は壁時計で、幅は 2 回の実行の差である。1 つの値は 1 回の実行である。

### 7.1 行の深さとコンパイル時間

D 種類のエフェクトを 1 回ずつ使う計算を 1 つ生成し、`dotnet fsi` で型検査して実行した。単位は秒で、起動の固定費として約 2 秒を含む。

| 深さ | Rio | RecordEnv | ListRow（行を注釈） | ListRow（`inline`） | EnvRow（`inline`） |
|---:|---:|---:|---:|---:|---:|
| 1 | 1.8 | 1.8 | 1.6–2.0 | 2.0 | 1.6–2.0 |
| 5 | 2.1 | 1.7 | 2.0–2.2 | 2.3 | 1.9–2.2 |
| 8 | 1.8 | 1.7 | 4.1 | 3.6 | 2.8–3.3 |
| 10 | 1.8 | 1.8 | 8.9–9.3 | 6.0 | 5.0–5.8 |
| 12 | 1.9 | 1.8 | **70.5–128.6** | 13.6 | 10.7–12.7 |
| 14 | 1.9 | 1.8 | **150 秒で打ち切り** | | **53.3–63.8** |
| 16 | 1.9 | 1.9 | | | **150 秒で打ち切り** |
| 40 | 3.2 | 3.0 | | | |

メンバー制約は名前で解決するので平坦である。型レベル探索は深さ 10 を超えると実用にならない。伸びる原因はコンパイラの中まで追っていない。候補の検査で入れ子の制約を解くことが関係すると考えているが、推定である。

### 7.2 合成の深さとコードの大きさ

`f_k` が `f_(k-1)` を 2 回呼ぶ計算を生成し、`dotnet build`（Release）でビルドした。`inline` の展開を最も強く出す人工的な形である。固定費は約 2 秒である。

| 合成の深さ | Rio | RecordEnv（閉じた環境） | ListRow（行を注釈） | RecordEnv（`inline`） | EnvRow（`inline`） | ListRow（`inline`） |
|---:|---:|---:|---:|---:|---:|---:|
| 0 | 8.7 KB | 12 KB | 12 KB | 15 KB | 13 KB | 15 KB |
| 8 | 14 KB | 16 KB | 16 KB | 3.0 MB、6 秒 | 3.1 MB、6 秒 | 2.5 MB、6 秒 |
| 10 | 15 KB | 17 KB | 17 KB | 12.7 MB、13 秒 | 12.8 MB、14–16 秒 | 10.1 MB、13–14 秒 |
| 12 | 16 KB、2 秒 | 18 KB、2 秒 | 18 KB、2 秒 | **52 MB、39–40 秒** | **53 MB、47–52 秒** | **42 MB、41–44 秒** |

`inline` の DLL は深さ 2 ごとに 4 倍、つまり展開の回数に比例する。

環境を閉じれば膨張はなくなる。操作だけを `inline` にして、計算を `Rio<AppEnv, int>` のように名前付きレコードで注釈すると、普通の関数になり、Rio と同じ桁に収まる。

### 7.3 呼び出し箇所の数

操作を 10 回呼ぶ計算を M 個生成し、それぞれを 1 回実行した。行の深さは 5 である。

| M | Rio | RecordEnv（閉じた環境） | RecordEnv（`inline`） | ListRow（`inline`） | EnvRow（`inline`） | ListRow（行を注釈） |
|---:|---:|---:|---:|---:|---:|---:|
| 10 | 2 秒、44 KB | 2 秒、60 KB | 2–3 秒、241 KB | 4 秒、181 KB | 5 秒、245 KB | 5 秒、53 KB |
| 50 | 3 秒、185 KB | 3 秒、252 KB | 5 秒、1.2 MB | 8 秒、869 KB | 9 秒、1.2 MB | 14–15 秒、207 KB |
| 200 | 5 秒、722 KB | 6 秒、1.1 MB | 9–10 秒、5.9 MB | 19–20 秒、3.8 MB | 23–25 秒、6.0 MB | **184 秒**、796 KB |

行を注釈した ListRow が 7.1 節と同じく `inline` より遅い理由は分かっていない。

### 7.4 実行コスト

操作 1 回あたりのナノ秒で、3 回の実行の幅を示す。ListRow と EnvRow と並べるため、試作のプロジェクトで [Bench.fs](../samples/EffectSystem.Samples/Bench.fs) と同じ形で測った。実行が別なので、[移植ノートの 4.2 節](porting-notes.md#42-コスト)とは直接比べず、この表の中の相対値を読むこと。

| 方式 | `Eff.ret` / `fn` | `reset`・`shift` / `ctl` |
|---|---:|---:|
| Capability | 25–26 | 125–128 |
| Rio | 60–63 | 156–166 |
| RecordEnv | 57–59 | 161–168 |
| Freer | 55–56 | 112–121 |
| ListRow | 73–76 | 142–146 |
| EnvRow | 96–99 | 222–235 |
| EnvRow、深さ 5 | 110–121 | |

| 5 段のハンドラーを転送する場合 | `fn` |
|---|---:|
| Freer | 232–236 |
| ListRow | 251–252 |

- **RecordEnv は Rio と差がない。** SRTP の解決はコンパイル時に終わっている。
- **ListRow は Freer に約 20 ns を足す。** ビルダーの包みの分と考えられる。行は幽霊型なので、深さのコストは実行時には現れない。
- **EnvRow は Rio より遅い。** `Eff.ret` で約 35 ns、`reset`・`shift` で約 65 ns の差がある。構造体の環境の受け渡しが関係していると考えられるが、切り分けていない。

## 8. 方式の比較

| | Capability | Rio | StaticRow | RecordEnv | ListRow | EnvRow |
|---|---|---|---|---|---|---|
| 要求の表し方 | 引数 | インターフェイス制約 | 位置のスロット | フィールド名の制約 | 型のリスト | 型のリスト |
| 環境の用意 | 不要 | クラスで転送 | 不要 | レコード | 不要 | ハンドラーが積む |
| 行を 1 段ずつ消す | スコープで代替 | できない | できる | できない | **できる** | **できる** |
| 親が行多相のまま局所処理 | できる | 残りを列挙すればできる | スロットが空くならできる | できない | できない | できない |
| 操作を足すときの変更 | 型のみ | 型のみ | 全シグネチャ | 型のみ | 型のみ | 型のみ |
| 別の型の同じ操作を同時に要求 | できる | 名前を分ける | できない | ラベルを分ける | **できる** | **できる** |
| 回答型が多相のまま | できる | できる | できる | できる | 注釈が要る | 注釈が要る |
| `inline` | 不要 | 不要 | 不要 | 行多相なら要る | 計算だけ要る | 計算だけ要る |
| トップレベルの再帰 | できる | できる | できる | 閉じた環境ならできる | 閉じた行か証拠渡し | 閉じた環境ならできる |
| 抽象メンバーの型 | できる | できる | 未検証 | できない | 行多相ならできないと考えられる | 同左 |
| 付け忘れのエラー | 書けない | FS0193 | FS0001 | FS0001（`get_Emit`） | FS0001（`Find`） | FS0001（`Find`） |
| 深さに対するコンパイル時間 | 未計測 | 平坦 | 未計測 | 平坦 | **指数的** | **指数的** |
| 実装の行数 | 58（Rio 込み） | 同左 | 52 | 操作ごとに 2 行 | 73 | 31 |

行数はコメントと空行を除いたものである。Capability と Rio の 58 は README の値で、StaticRow の 52 はサンプルの計算を含む。ListRow と EnvRow は試作の値で、サンプルの計算を除く。

Rio の局所処理は、残りの行を列挙した転送クラスを `Rio.provideSome` に渡す形である（[ZIO の検証の 4.2 節](zio-report.md#42-行の一部を消す)）。残りを開いたままにはできない。

## 9. 判断

**RecordEnv はサンプルとして使う。** 本体には入れず、[RecordEnv.fs](../samples/EffectSystem.Samples/RecordEnv.fs) に置いている。本体の変更も依存の追加も要らない。使うときの条件は次のとおりである。

1. **`inline` は操作だけに付ける。** 組み上げた計算は環境を名前付きレコードで注釈して閉じる（`Rio<AppEnv, int>`）。コードは膨らまず（[7.2 節](#72-合成の深さとコードの大きさ)）、再帰も普通に書け、アセンブリも越えられる。行多相なのは操作の部分だけになるが、`AppEnv` の転送をなくすという目的には足りる。
2. **公開 API にしない。** [F# component design guidelines](https://learn.microsoft.com/dotnet/fsharp/style-guide/component-design-guidelines) は、型クラスとダックタイピングをメンバー制約で模倣するのを避けるよう勧めている。インターフェイスの背後に隠せないことも、同じ結論を裏づける。アプリケーションの組み立ての層に限る。
3. **再利用する部品の行多相には Rio を使う。** 抽象メンバーの型に書けて、`inline` も要らない。

**型レベル探索は採用しない。** 解決するのは StaticRow の弱点だが、代わりの制限のほうが重い。

- 開いた行を注釈できないので、Koka の行の主な用途に届かない。行多相の親が局所的に処理する形も書けない。この形は Capability なら SRTP なしで書ける。
- コンパイル時間が深さに対して指数的である。計算 1 つなら深さ 8 まで許容できるが、[7.3 節](#73-呼び出し箇所の数)のとおり計算の数でも伸びるうえ、上限を型で強制できない。
- SRTP の解決の順序に依存している。`^e` をサポートに含める工夫も、4.2 節の優先度の問題も、ソルバーの挙動に左右される。F# 11 でエラーコードが変わるのも同じ種類の変化である。

StaticRow の「閉じた小さな領域で使う」という範囲に限れば置き換えの候補にはなるが、回答型の注釈が増えるので、明確な改善とはいえない。

**Record spreads は .NET 11 へ移るときに使う。** `type AppEnv = { ...Readers; ...Writers }` は転送をなくす。行多相のためにアップグレードする理由にはならない。

## 10. 未検証の事項

- **htsign、Zenn、Bruinbrown の記事、fslang-suggestions #11、FCS のリリースノートは要約で確認した。** 全文は読んでいない。
- **F# 11 は RC3 である。** 正式版で変わる可能性がある。
- **実際のアプリケーションでは試していない。** 規模の計測は生成した人工的なコードで、特に 7.2 節は最悪に近い形である。
- **指数的な伸びは測定からの判断で、コンパイラの中で原因を確かめていない。** 探索の書き方を変えれば改善する可能性は残る。
- **7 節の ListRow、EnvRow の計測と、規模の計測は、リポジトリのコマンドでは再現できない。** 採用しなかった方式のコードと生成スクリプトを含めていないからである。
- **StaticRow のコンパイル時間とコストは測っていない。**
- **IDE の応答は確かめていない。** 型検査が重いので、エディタでも同じ遅さが出ると考えられるが、未確認である。
- **C# からの利用は試していない。** リフレクションの結果から、`inline` の関数は他の言語からは使えないと考えられる。

## 参考文献

元資料と主な論文は [README の参考文献](../README.md#参考文献)にある。ここには、この文書で引用した資料を挙げる。

**行多相の模倣**

- Paweł Bańka, [Polymorphwhat?](https://medium.com/@pmbanka/polymorphwhat-e3b36bbb2f99), 2017
- [Does F# have row polymorphism (or something similar?)](https://stackoverflow.com/questions/29531852/does-f-have-row-polymorphism-or-something-similar), Stack Overflow, 2015
- [Partial anonymous record in F#](https://stackoverflow.com/questions/67892986/partial-anonymous-record-in-f), Stack Overflow, 2021
- [Generically add field to anonymous record using a function](https://stackoverflow.com/questions/74693431/generically-add-field-to-anonymous-record-using-a-function), Stack Overflow, 2022
- htsign, [F# で構造的部分型のメソッドを呼び出す方法](https://htsign.hateblo.jp/entry/2022/05/05/170648), 2022
- midoliy, [SRTP入門](https://zenn.dev/midoliy/articles/fsharp-srtp-intro), 2020
- [Looking at the details of F#'s duck typing](https://bruinbrown.wordpress.com/2014/03/02/looking-at-the-details-of-fs-duck-typing/), F# for fun and games, 2014

**F# の言語と処理系**

- [Add row polymorphism aka extensible records](https://github.com/fsharp/fslang-suggestions/issues/11), fslang-suggestions #11
- [F# RFC FS-1151: Record spreads](https://github.com/fsharp/fslang-design/blob/main/RFCs/FS-1151-record-spreads.md)
- [FSharp.Compiler.Service のリリースノート](https://fsharp.github.io/fsharp-compiler-docs/release-notes/FSharp.Compiler.Service.html)
- [F# component design guidelines](https://learn.microsoft.com/dotnet/fsharp/style-guide/component-design-guidelines)
- [ConstraintSolver.fs](https://github.com/dotnet/fsharp/blob/main/src/Compiler/Checking/ConstraintSolver.fs)
