/// <summary>ZIO の失敗の型 <c>E</c> を、<c>Rio</c> に型引数を足して写す。</summary>
/// <remarks>
/// <para>
/// 失敗を値として計算の中を戻すので、<c>finally</c> の区間を通る。継続を捨てる失敗と違い、後始末が走る。
/// 型に現れる部分は <c>Rio</c> と同じ構造で、F# に変性がないので <c>'E</c> は自動では広がらない。
/// 両方の失敗を残す <c>and!</c> は、Orsak に倣って失敗の型の <c>+</c> で 1 つに合わせる。
/// </para>
/// <para>
/// 本体には入れていない。<c>bind</c> ごとに <c>Result</c> を作るので、操作 1 回が <c>Rio</c> より遅くなる（<c>bench</c> の <c>Zio (Eff.ret)</c>）。
/// </para>
/// </remarks>
/// <category index="4">発展</category>
module EffectSystem.Samples.ZioStyle

open EffectSystem.Freer
open EffectSystem.Samples.Effects

/// <summary>環境 <c>'R</c> を必要とし、<c>'E</c> で失敗するか <c>'a</c> で成功する計算。ZIO の <c>ZIO[R, E, A]</c> に当たる。</summary>
/// <typeparam name="'R">環境の型。</typeparam>
/// <typeparam name="'E">失敗の型。</typeparam>
/// <typeparam name="'a">成功の型。</typeparam>
[<Struct; NoEquality; NoComparison>]
type Zio<'R, 'E, 'a> =
    /// <summary>環境を受け取って、失敗か成功を返す計算を作る関数。</summary>
    | Zio of ('R -> Eff<Result<'a, 'E>>)

/// <summary><see cref="T:EffectSystem.Samples.ZioStyle.Zio`3"/> の関数群。</summary>
[<RequireQualifiedAccess>]
module Zio =

    /// <summary>環境を与えて、Freer の計算に戻す。</summary>
    let run (env: 'R) (Zio f: Zio<'R, 'E, 'a>) : Eff<Result<'a, 'E>> = f env

    /// <summary>成功する計算を作る。ZIO の <c>ZIO.succeed</c> に当たる。</summary>
    let succeed (x: 'a) : Zio<'R, 'E, 'a> = Zio(fun _ -> Eff.ret (Ok x))

    /// <summary>失敗する計算を作る。ZIO の <c>ZIO.fail</c> に当たる。</summary>
    let fail (e: 'E) : Zio<'R, 'E, 'a> = Zio(fun _ -> Eff.ret (Error e))

    /// <summary>環境の能力を呼ぶ計算を作る。ZIO の <c>ZIO.serviceWithZIO</c> に当たる。</summary>
    let access (f: 'R -> Eff<'a>) : Zio<'R, 'E, 'a> =
        Zio(fun env -> f env |> Eff.map Ok)

    /// <summary>計算をつなげる。失敗したら後ろの計算を飛ばす。</summary>
    let bind (f: 'a -> Zio<'R, 'E, 'b>) (m: Zio<'R, 'E, 'a>) : Zio<'R, 'E, 'b> =
        Zio(fun env ->
            run env m
            |> Eff.bind (function
                | Ok x -> run env (f x)
                | Error e -> Eff.ret (Error e)))

    /// <summary>失敗から回復する。ZIO の <c>catchAll</c> に当たる。結果の失敗の型は <c>'E</c> と無関係になる。</summary>
    let catchAll (h: 'E -> Zio<'R, 'E2, 'a>) (m: Zio<'R, 'E, 'a>) : Zio<'R, 'E2, 'a> =
        Zio(fun env ->
            run env m
            |> Eff.bind (function
                | Ok x -> Eff.ret (Ok x)
                | Error e -> run env (h e)))

    /// <summary>失敗の型を変える。ZIO の <c>mapError</c> に当たる。</summary>
    let mapError (f: 'E -> 'E2) (m: Zio<'R, 'E, 'a>) : Zio<'R, 'E2, 'a> = catchAll (f >> fail) m

    /// <summary>両方の計算を順に実行し、両方が失敗したら失敗を <c>+</c> で合わせる。ZIO の <c>validate</c> に当たる。</summary>
    /// <remarks>
    /// <para>
    /// <c>bind</c> と違い、前の計算が失敗しても後の計算を実行する。
    /// ZIO は 2 つの失敗を <c>Cause</c> に残すので <c>E</c> に条件がないが、ここでは失敗のスロットが 1 つなので、
    /// 失敗の型に <c>+</c> を求めて 1 つの値に合わせる。これは Orsak の <c>and!</c> と同じ解決である。
    /// </para>
    /// <para>Orsak は 2 つの計算を同時に始めるが、Freer の上では順に実行する。</para>
    /// </remarks>
    /// <param name="m1">前の計算。</param>
    /// <param name="m2">後の計算。</param>
    let inline validate (m1: Zio<'R, ^E, 'a>) (m2: Zio<'R, ^E, 'b>) : Zio<'R, ^E, 'a * 'b> =
        Zio(fun env ->
            run env m1
            |> Eff.bind (fun r1 ->
                run env m2
                |> Eff.map (fun r2 ->
                    match r1, r2 with
                    | Ok a, Ok b -> Ok(a, b)
                    | Error e, Ok _
                    | Ok _, Error e -> Error e
                    | Error e1, Error e2 -> Error(e1 + e2))))

    /// <summary>成功でも失敗でも後始末を走らせる。ZIO の <c>ensuring</c> に当たる。</summary>
    let ensuring (fin: unit -> unit) (m: Zio<'R, 'E, 'a>) : Zio<'R, 'E, 'a> =
        Zio(fun env ->
            eff {
                try
                    return! run env m
                finally
                    fin ()
            })

    /// <summary><c>zio { }</c> のビルダー。</summary>
    type Builder() =
        /// <summary><c>return x</c></summary>
        member _.Return(x: 'a) : Zio<'R, 'E, 'a> = succeed x

        /// <summary><c>return! m</c></summary>
        member _.ReturnFrom(m: Zio<'R, 'E, 'a>) : Zio<'R, 'E, 'a> = m

        /// <summary>末尾にある <c>return! m</c> と <c>do! m</c>。理由は <c>eff { }</c> のビルダーと同じである。</summary>
        member _.ReturnFromFinal(m: Zio<'R, 'E, 'a>) : Zio<'R, 'E, 'a> = m

        /// <summary><c>let! x = m</c></summary>
        member _.Bind(m: Zio<'R, 'E, 'a>, f: 'a -> Zio<'R, 'E, 'b>) : Zio<'R, 'E, 'b> = bind f m

        /// <summary><c>let! x = m1 and! y = m2</c>。<c>Zio.validate</c> で両方を実行する。</summary>
        member inline _.MergeSources(m1: Zio<'R, ^E, 'a>, m2: Zio<'R, ^E, 'b>) : Zio<'R, ^E, 'a * 'b> = validate m1 m2

        /// <summary>値を返さない本体。</summary>
        member _.Zero() : Zio<'R, 'E, unit> = succeed ()

        /// <summary>本体を遅延する。</summary>
        member _.Delay(f: unit -> Zio<'R, 'E, 'a>) : Zio<'R, 'E, 'a> =
            Zio(fun env -> Eff.delay (fun () -> run env (f ())))

        /// <summary>文を順に実行する。</summary>
        member _.Combine(a: Zio<'R, 'E, unit>, b: Zio<'R, 'E, 'a>) : Zio<'R, 'E, 'a> = bind (fun () -> b) a

/// <summary><see cref="T:EffectSystem.Samples.ZioStyle.Zio`3"/> を書くコンピュテーション式。</summary>
let zio = Zio.Builder()

/// <summary>失敗で抜けても <c>ensuring</c> の後始末が走る。</summary>
/// <param name="out">出力先。</param>
let exampleEnsuring (out: Log) : unit =
    let result =
        zio {
            do! Zio.fail "boom"
            out "not reached"
            return "ok"
        }
        |> Zio.ensuring (fun () -> out "release")
        |> Zio.catchAll (fun e -> Zio.succeed ("failed: " + e))
        |> Zio.run ()
        |> Eff.run

    match result with
    | Ok v -> out v
    | Error() -> ()

/// <summary>失敗しない計算。<c>'E</c> が自由な型変数のまま残り、どの失敗の型にも合う。ZIO の <c>Nothing</c> の役を果たす。</summary>
/// <remarks>値にすると値の制限で 1 つの型に固定されるので、<c>()</c> を受ける関数にする。</remarks>
let never () = zio { return 1 }

/// <summary>在庫の失敗。</summary>
type StockError =
    /// <summary>在庫がない。</summary>
    | OutOfStock

/// <summary>支払いの失敗。</summary>
type PaymentError =
    /// <summary>支払いを断られた。</summary>
    | Declined

/// <summary>注文全体の失敗。ZIO なら <c>E</c> は最小上界へ広がるが、F# では共通の共用体へ手で移す。</summary>
type OrderError =
    /// <summary>在庫の失敗。</summary>
    | Stock of StockError
    /// <summary>支払いの失敗。</summary>
    | Payment of PaymentError

/// <summary>在庫を確保する。</summary>
/// <param name="sku">商品。空なら失敗する。</param>
let reserve (sku: string) =
    zio {
        if sku = "" then
            return! Zio.fail OutOfStock
        else
            return sku.Length
    }

/// <summary>支払う。</summary>
/// <param name="amount">金額。100 を超えると失敗する。</param>
let charge (amount: int) =
    zio {
        if amount > 100 then
            return! Zio.fail Declined
        else
            return $"receipt {amount}"
    }

/// <summary>失敗の型が違う計算を、<c>mapError</c> で共通の共用体へ移してつなげる。</summary>
/// <param name="sku">商品。</param>
let placeOrder (sku: string) =
    zio {
        let! amount = reserve sku |> Zio.mapError Stock
        return! charge amount |> Zio.mapError Payment
    }

/// <summary><c>catchAll</c> で失敗の型を消す。行と違ってスロットが 1 つなので、消す操作が普通の関数になる。</summary>
/// <param name="sku">商品。</param>
let reserveOrZero (sku: string) =
    reserve sku |> Zio.catchAll (fun _ -> Zio.succeed 0)

/// <summary>入力の誤りの一覧。<c>+</c> で合わせられるので、<c>and!</c> の失敗の型に使える。</summary>
type Problems =
    /// <summary>誤りの説明の一覧。</summary>
    | Problems of string list

    /// <summary>2 つの一覧をつなげる。</summary>
    static member (+)(Problems a, Problems b) = Problems(a @ b)

/// <summary>商品の指定を確かめる。</summary>
/// <param name="sku">商品。空なら失敗する。</param>
let checkSku (sku: string) =
    zio {
        if sku = "" then
            return! Zio.fail (Problems [ "sku is empty" ])
        else
            return sku
    }

/// <summary>数量を確かめる。</summary>
/// <param name="quantity">数量。正でなければ失敗する。</param>
let checkQuantity (quantity: int) =
    zio {
        if quantity <= 0 then
            return! Zio.fail (Problems [ "quantity must be positive" ])
        else
            return quantity
    }

/// <summary>注文の行を確かめる。<c>and!</c> で両方を実行するので、誤りがすべて揃う。</summary>
/// <remarks>
/// Orsak のドキュメントの Errors の例と同じ形である。
/// <code lang="fsharp"><![CDATA[
/// // Orsak
/// let checkLine sku quantity = eff {
///     let! sku = checkSku sku
///     and! quantity = checkQuantity quantity
///     return sku, quantity
/// }
/// ]]></code>
/// <c>let!</c> で書くと、最初の誤りで止まる。
/// </remarks>
/// <param name="sku">商品。</param>
/// <param name="quantity">数量。</param>
let checkLine (sku: string) (quantity: int) =
    zio {
        let! sku = checkSku sku
        and! quantity = checkQuantity quantity
        return sku, quantity
    }
