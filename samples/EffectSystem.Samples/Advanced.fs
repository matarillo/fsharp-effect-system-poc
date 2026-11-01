/// <summary>記事では触れられていないが、エミュレーションの評価に必要な例。</summary>
/// <remarks>
/// マルチショットの本来の用途、ハンドラーの順序が持つ意味、再開の回数とリソースの関係を扱う。
/// いずれも Freer エンジンの継続がヒープ上の関数であることから導かれる。
/// </remarks>
/// <category index="4">発展</category>
module EffectSystem.Samples.Advanced

open EffectSystem
open EffectSystem.Freer
open EffectSystem.Samples.Effects

// ---- State × NonDet: ハンドラーの順序が意味を変える ----

/// <summary>真か偽かを選ぶ操作。ハンドラーが継続を両方の値で再開すると、非決定計算になる。</summary>
type Choose =
    /// <summary>選択を要求する。</summary>
    | Choose

    interface Effect<bool>

/// <summary>状態を引数で持ち回るハンドラー。</summary>
/// <remarks>
/// <c>Eff.view</c> で 1 歩ずつ進め、再帰の引数に状態を持つ。OCaml の Shallow 版の <c>loop</c> と同じ構造である。
/// 可変の参照を使わないので、外側のハンドラーが継続を複製すれば状態も一緒に複製される。
/// </remarks>
/// <param name="s">今の状態。</param>
/// <param name="m">ハンドラーを被せる計算。</param>
let rec runState (s: 's) (m: Eff<'a>) : Eff<'a * 's> =
    Eff.delay (fun () ->
        match Eff.view m with
        | Return a -> Eff.ret (a, s)
        | Eff.Op((_: Get<'s>), k) -> runState s (k s)
        | Eff.Op((Put(s': 's)), k) -> runState s' (k ())
        | other -> Eff.forward other (runState s))

/// <summary>すべての分岐の結果を集めるハンドラー。マルチショットの本来の用途である。</summary>
/// <remarks>継続を <c>true</c> と <c>false</c> の両方で再開し、結果を連結する。</remarks>
/// <param name="m">ハンドラーを被せる計算。</param>
let runAll (m: Eff<'a>) : Eff<'a list> =
    m
    |> Eff.handleWith
        (fun a -> Eff.ret [ a ])
        [ Eff.ctl (fun (_: Choose) resume ->
              eff {
                  let! t = resume true
                  let! f = resume false
                  return t @ f
              }) ]

/// <summary>分岐してから状態を読み、1 増やす計算。ハンドラーの順序の違いを見るための題材である。</summary>
let choiceThenCount: Eff<bool * int> =
    eff {
        let! b = Eff.perform Choose
        let! (s: int) = Eff.perform Get
        do! Eff.perform (Put(s + 1))
        return (b, s)
    }

/// <summary>State が内側。外側の <c>runAll</c> が捕捉する継続に状態が含まれるので、分岐ごとに状態が複製される。</summary>
/// <returns><c>[((true, 0), 1); ((false, 0), 1)]</c></returns>
let stateInsideNonDet () : ((bool * int) * int) list =
    choiceThenCount |> runState 0 |> runAll |> Eff.run

/// <summary>State が外側。継続に状態が含まれないので、分岐をまたいで状態が共有される。</summary>
/// <returns><c>([(true, 0); (false, 1)], 2)</c></returns>
let stateOutsideNonDet () : (bool * int) list * int =
    choiceThenCount |> runAll |> runState 0 |> Eff.run

// ---- ジェネレーター: Freer なら永続的 ----

/// <summary>要素を 1 つ差し出す操作。</summary>
/// <typeparam name="'a">要素の型。</typeparam>
type Next<'a> =
    /// <summary>要素を渡す。</summary>
    | Next of 'a

    interface Effect<unit>

/// <summary>消費者が 1 つずつ取り出す列の節。</summary>
/// <typeparam name="'a">要素の型。</typeparam>
type Node<'a> =
    /// <summary>列の終わり。</summary>
    | Nil
    /// <summary>要素と、残りの列を取り出す関数。</summary>
    | Cons of 'a * (unit -> Node<'a>)

/// <summary>マニュアル 24.4 の <c>invert</c> の Freer 版。同じ末尾を何度たどってもよい。</summary>
/// <remarks>継続がヒープ上の関数なので、Direct 版のように列が使い捨てにならない。</remarks>
/// <param name="iter">要素を押し出す計算。</param>
let invert (iter: ('a -> Eff<unit>) -> Eff<unit>) : unit -> Node<'a> =
    let rec toNode (m: Eff<unit>) () : Node<'a> =
        match Eff.view m with
        | Return() -> Nil
        | Eff.Op((Next(v: 'a)), k) -> Cons(v, toNode (k ()))
        | Operation(e, _) -> raise (Unhandled e)

    toNode (iter (Next >> Eff.perform))

/// <summary>同じ末尾を 2 回たどる。どちらも同じ要素が得られる。</summary>
/// <returns><c>(2, 2)</c></returns>
let persistentSequence () : int * int =
    let source: unit -> Node<int> =
        invert (fun push ->
            eff {
                for x in [ 1; 2; 3 ] do
                    do! push x
            })

    match source () with
    | Cons(_, rest) ->
        let head =
            function
            | Cons(v, _) -> v
            | Nil -> -1

        (head (rest ()), head (rest ()))
    | Nil -> (-1, -1)

// ---- finally と再開の回数 ----

/// <summary>再開ごとに <c>finally</c> が走る。リソースを持つ計算をマルチショットで再開する危険の実例である。</summary>
/// <remarks>OCaml がワンショットを選んだ理由の 1 つが、この線形リソースの問題である。</remarks>
/// <param name="out">出力先。</param>
let finallyWithMultiShot (out: Log) : unit =
    eff {
        try
            let! (v: string) = Eff.perform Ask
            out $"use {v}"
        finally
            out "release"
    }
    |> Eff.handle
        [ Eff.ctl (fun (_: Ask<string>) resume ->
              eff {
                  do! resume "first"
                  return! resume "second"
              }) ]
    |> Eff.run

/// <summary>再開せずに捨てると <c>finally</c> は走らない。</summary>
/// <param name="out">出力先。</param>
let finallySkippedWhenAbandoned (out: Log) : unit =
    eff {
        try
            let! (v: string) = Eff.perform Ask
            out $"use {v}"
        finally
            out "release"
    }
    |> Eff.handle [ Eff.ctl (fun (_: Ask<string>) _ -> Eff.ret ()) ]
    |> Eff.run

/// <summary><c>Discontinue</c> で再開すれば、<c>perform</c> の位置で例外になり、<c>finally</c> と <c>with</c> が走る。</summary>
/// <param name="out">出力先。</param>
let finallyWithDiscontinue (out: Log) : unit =
    eff {
        try
            try
                let! (v: string) = Eff.perform Ask
                out $"use {v}"
            finally
                out "release"
        with e ->
            out $"caught {e.Message}"
    }
    |> Eff.handle
        [ Eff.ctlK (fun (_: Ask<string>) k -> k.Discontinue(exn "unwind")) ]
    |> Eff.run
