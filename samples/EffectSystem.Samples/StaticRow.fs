/// <summary>ハンドラーごとに行から 1 つ消える型付けを、F# の単一化だけで得る手法。</summary>
/// <remarks>
/// <para>
/// Koka の <c>&lt;ask&lt;a&gt;, emit&lt;a&gt; | e&gt;</c> は、ハンドラーを被せるたびに 1 つ消える。F# に行はないが、
/// エフェクトごとに型引数を 1 つ割り当て、「未処理（<see cref="T:EffectSystem.Samples.StaticRow.Needs`1"/>）」か
/// 「処理済み（<see cref="T:EffectSystem.Samples.StaticRow.Handled"/>）」かを持たせれば、同じ性質が得られる。
/// Rémy の行の符号化に近い手法である。
/// </para>
/// <para>
/// <b>なぜ動くのか。</b>
/// 計算をつなげると、すべての区間が同じ型引数を共有する。使わない操作のスロットは多相のまま残り、
/// 使った操作のスロットは単一化によって <c>Needs</c> に確定する。特別な仕組みは要らず、Hindley-Milner の推論だけで動く。
/// </para>
/// <para>
/// <b>代償。</b>
/// 型引数の並びはアプリケーションが決めるので、閉じた領域でしか使えない。
/// エフェクトを足すたびに <see cref="T:EffectSystem.Samples.StaticRow.Row`3"/> とすべてのシグネチャを変える必要がある。
/// </para>
/// </remarks>
/// <category index="4">発展</category>
module EffectSystem.Samples.StaticRow

open EffectSystem.Freer
open EffectSystem.Samples.Effects

/// <summary>このエフェクトは未処理である、という印。</summary>
/// <typeparam name="'v">操作が扱う値の型。同じ計算の中の操作がすべて同じ型になることも、単一化が保証する。</typeparam>
type Needs<'v> =
    class
    end

/// <summary>このエフェクトは処理済みである、という印。</summary>
type Handled =
    class
    end

/// <summary>エフェクトの状態を型引数に持つ計算。実体は Freer の計算である。</summary>
/// <typeparam name="'ask">ask のスロット。</typeparam>
/// <typeparam name="'emit">emit のスロット。</typeparam>
/// <typeparam name="'a">計算が最後に返す値の型。</typeparam>
/// <remarks>コンストラクターを隠しているので、スロットの状態はこのモジュールの関数でしか変えられない。</remarks>
[<Struct; NoEquality; NoComparison>]
type Row<'ask, 'emit, 'a> = private Row of Eff<'a>

/// <summary>ask を要求する。ask のスロットが <c>Needs</c> に確定し、emit のスロットは多相のまま残る。</summary>
let ask<'v, 'emit> () : Row<Needs<'v>, 'emit, 'v> = Row(Eff.perform Ask)

/// <summary>値を発する。emit のスロットが <c>Needs</c> に確定する。</summary>
/// <param name="v">発する値。</param>
let emit<'ask, 'v> (v: 'v) : Row<'ask, Needs<'v>, unit> = Row(Eff.perform (Emit v))

/// <summary>Freer の計算を持ち上げる。</summary>
/// <remarks>スロットを変えないので、持ち上げた計算が使う操作は型で検査されない。</remarks>
/// <param name="m">持ち上げる計算。</param>
let lift (m: Eff<'a>) : Row<'ask, 'emit, 'a> = Row m

/// <summary>ask を処理する。ask のスロットが <c>Needs</c> から <c>Handled</c> へ変わる。</summary>
/// <remarks>Polysemy の <c>runAsk :: v -> Sem (Ask v ': r) a -> Sem r a</c> に当たる。すでに処理済みの計算に使うと型エラーになる。</remarks>
let runAsk (value: 'v) (Row m: Row<Needs<'v>, 'emit, 'a>) : Row<Handled, 'emit, 'a> =
    Row(m |> Eff.handle [ Eff.fn (fun (_: Ask<'v>) -> value) ])

/// <summary>emit を処理する。emit のスロットが <c>Needs</c> から <c>Handled</c> へ変わる。</summary>
let runEmit (out: Log) (Row m: Row<'ask, Needs<string>, 'a>) : Row<'ask, Handled, 'a> =
    Row(m |> Eff.handle [ Eff.fn (fun (Emit(s: string)) -> out ("Emit " + s)) ])

/// <summary>計算を実行する。すべてのスロットが <c>Handled</c> でなければ呼べない。</summary>
/// <remarks>ハンドラーを付け忘れると、<c>Handled</c> と <c>Needs</c> が単一化できず FS0001 になる。</remarks>
let run (Row m: Row<Handled, Handled, 'a>) : 'a = Eff.run m

/// <summary><c>row { }</c> のビルダー。</summary>
type Builder() =
    /// <summary><c>return x</c></summary>
    member _.Return(x: 'a) : Row<'ask, 'emit, 'a> = Row(Eff.ret x)

    /// <summary><c>return! m</c></summary>
    member _.ReturnFrom(m: Row<'ask, 'emit, 'a>) = m

    /// <summary><c>let! x = m</c>。前後の区間が同じスロットを共有するので、単一化で状態が集まる。</summary>
    member _.Bind(Row m: Row<'ask, 'emit, 'a>, f: 'a -> Row<'ask, 'emit, 'b>) : Row<'ask, 'emit, 'b> =
        Row(
            m
            |> Eff.bind (fun x ->
                let (Row n) = f x
                n)
        )

    /// <summary>値を返さない本体。</summary>
    member _.Zero() : Row<'ask, 'emit, unit> = Row(Eff.ret ())

    /// <summary>本体を遅延する。</summary>
    member _.Delay(f: unit -> Row<'ask, 'emit, 'a>) : Row<'ask, 'emit, 'a> =
        Row(
            Eff.delay (fun () ->
                let (Row n) = f ()
                n)
        )

/// <summary>エフェクトの状態を追跡する計算を書くコンピュテーション式。</summary>
let row = Builder()

/// <summary>ask と emit を使う計算。注釈なしで <c>Row&lt;Needs&lt;'a&gt;, Needs&lt;'a&gt;, unit&gt;</c> と推論される。</summary>
let askEmit () =
    row {
        let! v = ask ()
        do! emit v
    }

/// <summary>ask だけを使う計算。使わない emit のスロットは多相のまま残り、<c>Row&lt;Needs&lt;string&gt;, 'a, int&gt;</c> と推論される。</summary>
let askOnly () =
    row {
        let! (v: string) = ask ()
        return v.Length
    }

/// <summary>すべてのエフェクトを処理して実行する。</summary>
/// <param name="out">出力先。</param>
let exampleAskEmit (out: Log) : unit =
    askEmit () |> runAsk "Ask Value" |> runEmit out |> run

/// <summary>ハンドラーの順序を入れ替えても型は通る。Koka の行と同じく順序に依存しない。</summary>
/// <param name="out">出力先。</param>
let exampleSwappedOrder (out: Log) : unit =
    askEmit () |> runEmit out |> runAsk "Ask Value" |> run

/// <summary>使わないエフェクトにはハンドラーが要らないことを示す。</summary>
let exampleAskOnly () : int = askOnly () |> runAsk "four" |> run
