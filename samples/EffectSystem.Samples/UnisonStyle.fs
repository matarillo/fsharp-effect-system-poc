/// <summary>記事の Unison の節を写す。<c>Request</c> のパターンマッチと、手で書く再帰がそのまま対応する。</summary>
/// <remarks>
/// Unison のハンドラーは <c>handle k v with ask.handler v</c> と再帰を手で書くので、実質的に Shallow ハンドラーである。
/// F# では <c>Eff.view</c> で次の 1 歩を覗き、<c>Eff.Op</c> パターンで受ける。
/// Unison の <c>Request</c> は扱わない操作を自動で転送するが、F# では <c>Eff.forward</c> を明示する。
/// </remarks>
/// <category index="2">6 言語の写し</category>
module EffectSystem.Samples.UnisonStyle

open EffectSystem.Freer
open EffectSystem.Samples.Effects

/// <summary>ask に固定値で答えるハンドラー。</summary>
/// <remarks>
/// <code lang="unison"><![CDATA[
/// ask.handler v = cases
///   { a }            -> a
///   { Ask.ask -> k } -> handle k v with ask.handler v
/// ]]></code>
/// </remarks>
/// <param name="v">ask の回答。</param>
/// <param name="m">ハンドラーを被せる計算。</param>
let rec askHandler (v: 'v) (m: Eff<'a>) : Eff<'a> =
    Eff.delay (fun () ->
        match Eff.view m with
        | Return a -> Eff.ret a
        | Eff.Op((_: Ask<'v>), k) -> askHandler v (k v)
        | other -> Eff.forward other (askHandler v))

/// <summary>emit を受けて出力し、再開するハンドラー。</summary>
/// <remarks>
/// <code lang="unison"><![CDATA[
/// emit.handler = cases
///   { a }                  -> a
///   { Emit.emit msg -> k } -> handle
///     printLine("Emit " ++ msg)
///     k ()
///   with emit.handler
/// ]]></code>
/// </remarks>
/// <param name="out">出力先。</param>
/// <param name="m">ハンドラーを被せる計算。</param>
let rec emitHandler (out: Log) (m: Eff<'a>) : Eff<'a> =
    Eff.delay (fun () ->
        match Eff.view m with
        | Return a -> Eff.ret a
        | Eff.Op((Emit(msg: string)), k) ->
            out ("Emit " + msg)
            emitHandler out (k ())
        | other -> Eff.forward other (emitHandler out))

/// <summary>ask と emit を使う計算。</summary>
/// <param name="out">出力先。</param>
let emitAsk (out: Log) : Eff<unit> =
    eff {
        out "Start"
        let! (askValue: string) = Eff.perform Ask
        do! Eff.perform (Emit askValue)
        out "End"
    }

/// <summary>ハンドラーを被せて実行する。</summary>
/// <remarks>
/// <code lang="unison"><![CDATA[
/// handle
///   handle composed.emitAsk()
///   with ask.handler "Ask Value"
/// with emit.handler
/// ]]></code>
/// </remarks>
/// <param name="out">出力先。</param>
let exampleEmitAsk (out: Log) : unit =
    emitAsk out |> askHandler "Ask Value" |> emitHandler out |> Eff.run

/// <summary>ask の継続を 2 回呼ぶハンドラー。継続 <c>k</c> は普通の関数なので、もう一度呼ぶだけで済む。</summary>
/// <remarks>
/// <code lang="unison"><![CDATA[
/// { Ask.ask -> k } ->
///   _ = handle k v with ask.handler v
///   handle k v with ask.handler v
/// ]]></code>
/// </remarks>
/// <param name="v">ask の回答。</param>
/// <param name="m">ハンドラーを被せる計算。</param>
let rec askHandlerTwice (v: 'v) (m: Eff<unit>) : Eff<unit> =
    Eff.delay (fun () ->
        match Eff.view m with
        | Return a -> Eff.ret a
        | Eff.Op((_: Ask<'v>), k) ->
            eff {
                do! askHandlerTwice v (k v)
                return! askHandlerTwice v (k v)
            }
        | other -> Eff.forward other (askHandlerTwice v))

/// <summary>ask を 2 回再開する。出力は記事の Unison と同じである。</summary>
/// <param name="out">出力先。</param>
let exampleAskTwice (out: Log) : unit =
    emitAsk out |> askHandlerTwice "Ask Value" |> emitHandler out |> Eff.run
