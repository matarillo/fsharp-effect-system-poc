/// <summary>記事の OCaml の節を Direct エンジンで写す。計算は普通の関数のまま書ける。</summary>
/// <remarks>
/// 記事は OCaml の <c>match_with</c> のレコード形式を使っているので、同じ形の <c>Deep.matchWith</c> で写した。
/// <c>Deep.tryWith</c> は OCaml 5.3 以降の <c>try ... with effect ...</c> に当たる。
/// </remarks>
/// <category index="2">6 言語の写し</category>
module EffectSystem.Samples.OCamlStyle

open EffectSystem
open EffectSystem.Direct
open EffectSystem.Samples.Effects

/// <summary>ask に固定値で答えるハンドラーを被せて実行する。</summary>
/// <remarks>
/// <code lang="ocaml"><![CDATA[
/// let run_ask (f: unit -> 'a) ~(env: string) : 'a =
///   match_with f ()
///   { retc = Fun.id;
///     exnc = raise;
///     effc = (fun (type b) (eff: b Effect.t) ->
///       match eff with
///       | Ask -> Some (fun (k: (b, _) continuation) -> continue k env)
///       | _ -> None) }
/// ]]></code>
/// </remarks>
/// <param name="env">ask の回答。</param>
/// <param name="f">実行する計算。</param>
let runAsk (env: 'v) (f: unit -> 'a) : 'a =
    Deep.matchWith
        f
        { retc = id
          exnc = rethrow
          effc = [ Deep.ctl (fun (_: Ask<'v>) k -> Deep.resume k env) ] }

/// <summary>emit を受けて出力するハンドラーを被せて実行する。</summary>
/// <param name="out">出力先。</param>
/// <param name="f">実行する計算。</param>
let runEmit (out: Log) (f: unit -> 'a) : 'a =
    Deep.matchWith
        f
        { retc = id
          exnc = rethrow
          effc =
            [ Deep.ctl (fun (Emit(value: string)) k ->
                  out ("Emit " + value)
                  Deep.resume k ()) ] }

/// <summary>ask と emit を使う計算。普通の関数である。</summary>
/// <remarks>
/// 型は <c>unit -> unit</c> で、エフェクトが型に現れないのも OCaml と同じである。
/// ハンドラーがなければ実行時に <see cref="T:EffectSystem.Unhandled"/> になる。
/// </remarks>
/// <param name="out">出力先。</param>
let askEmit (out: Log) () : unit =
    out "Start"
    let value: string = perform Ask
    perform (Emit value)
    out "End"

/// <summary>ハンドラーを被せて実行する。</summary>
/// <remarks>OCaml の <c>run_ask (fun () -> run_emit ask_emit) ~env:"Ask Value"</c> に当たる。</remarks>
/// <param name="out">出力先。</param>
let exampleAskEmit (out: Log) : unit =
    runAsk "Ask Value" (fun () -> runEmit out (askEmit out))

/// <summary><c>run_ask</c> を付け忘れた場合。<see cref="T:EffectSystem.Unhandled"/> が ask の位置で発生する。</summary>
/// <param name="out">出力先。</param>
let exampleMissingHandler (out: Log) : unit = runEmit out (askEmit out)

/// <summary>継続を 2 回再開する。<see cref="T:EffectSystem.ContinuationAlreadyResumed"/> が発生する。</summary>
/// <param name="out">出力先。</param>
let exampleResumeTwice (out: Log) : unit =
    let runAskTwice (env: string) (f: unit -> unit) : unit =
        Deep.tryWith
            f
            [ Deep.ctl (fun (_: Ask<string>) k ->
                  Deep.resume k env
                  Deep.resume k env) ]

    out "--------"
    runAskTwice "Ask Value" (fun () -> runEmit out (askEmit out))
    out "--------"

/// <summary>State を Shallow ハンドラーで実装する。状態を引数で持ち回り、再開のたびに次のハンドラーを渡す。</summary>
/// <remarks>
/// OCaml の <c>let rec loop : type a r. ...</c> に当たる多相再帰は、F# ではモジュール直下で型引数を明示して書く。
/// F# の局所関数には明示的な型引数を書けない。
/// </remarks>
let rec private stateLoop<'s, 'x, 'r> (state: 's) (k: Shallow.Continuation<'x, 'r>) (x: 'x) : 'r =
    Shallow.continueWith
        k
        x
        { retc = id
          exnc = rethrow
          effc =
            [ Shallow.ctl (fun (_: Get<'s>) k -> stateLoop<'s, 's, 'r> state k state)
              Shallow.ctl (fun (Put(value: 's)) k -> stateLoop<'s, unit, 'r> value k ()) ] }

/// <summary>状態を与えて計算を実行する。記事の <c>module State (S : sig type t end)</c> の <c>run</c> に当たる。</summary>
/// <remarks>OCaml では型に依存しないために functor が要るが、F# の <c>Get</c> と <c>Put</c> はジェネリックなので不要である。</remarks>
/// <param name="init">状態の初期値。</param>
/// <param name="f">実行する計算。</param>
let runState (init: 's) (f: unit -> 'r) : 'r =
    stateLoop<'s, unit, 'r> init (Shallow.fiber f) ()

/// <summary>状態を読み書きする計算を実行する。</summary>
/// <remarks>
/// <code lang="text">
/// Got value: Hello, world!
/// Got new value: &lt;&lt;&lt;Hello, world!&gt;&gt;&gt;
/// </code>
/// </remarks>
/// <param name="out">出力先。</param>
let exampleState (out: Log) : unit =
    runState "Hello, world!" (fun () ->
        let value: string = perform Get
        out $"Got value: {value}"
        perform (Put $"<<<{value}>>>")
        let newValue: string = perform Get
        out $"Got new value: {newValue}")
