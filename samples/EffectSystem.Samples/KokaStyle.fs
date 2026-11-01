/// <summary>記事の Koka の節を Freer エンジンで写す。</summary>
/// <remarks>
/// <c>ctl</c>、<c>fun</c>、<c>resume</c> の回数の違いは、そのまま再現できる。
/// 再現できないのは行多相で、Koka の <c>&lt;ask&lt;a&gt;, emit&lt;a&gt;, console&gt; ()</c> に当たる情報は型に残らない。
/// </remarks>
/// <category index="2">6 言語の写し</category>
module EffectSystem.Samples.KokaStyle

open EffectSystem
open EffectSystem.Freer
open EffectSystem.Samples.Effects

/// <summary>ask に固定値で答えるハンドラー。<c>ctl</c> で継続を明示的に呼ぶ版。</summary>
/// <remarks>
/// <code lang="koka"><![CDATA[
/// fun ask-handler(value : a, action : () -> <ask<a>|e> r) : e r
///   with ctl ask() resume(value)
///   action()
/// ]]></code>
/// </remarks>
/// <param name="value">ask の回答。</param>
/// <param name="action">ハンドラーを被せる計算。</param>
let askHandler (value: 'v) (action: Eff<'r>) : Eff<'r> =
    action |> Eff.handle [ Eff.ctl (fun (_: Ask<'v>) resume -> resume value) ]

/// <summary>ask に固定値で答えるハンドラー。<c>fun</c> で継続を省いた版。</summary>
/// <remarks>Koka の <c>with fun ask() value</c> に当たる。常に末尾で再開するので継続が要らない。</remarks>
/// <param name="value">ask の回答。</param>
/// <param name="action">ハンドラーを被せる計算。</param>
let askHandlerFn (value: 'v) (action: Eff<'r>) : Eff<'r> =
    action |> Eff.handle [ Eff.fn (fun (_: Ask<'v>) -> value) ]

/// <summary>emit を受けて出力し、再開するハンドラー。</summary>
/// <remarks>
/// <code lang="koka"><![CDATA[
/// fun emit-handler(action : () -> <emit<string>,console|e> r) : <console|e> r
///   with ctl emit(message)
///     println("Emit " ++ message)
///     resume(())
///   action()
/// ]]></code>
/// </remarks>
/// <param name="out">出力先。</param>
/// <param name="action">ハンドラーを被せる計算。</param>
let emitHandler (out: Log) (action: Eff<'r>) : Eff<'r> =
    action
    |> Eff.handle
        [ Eff.ctl (fun (Emit(message: string)) resume ->
              out ("Emit " + message)
              resume ()) ]

/// <summary>ask と emit を使う計算。</summary>
/// <remarks>
/// 行多相がないので <c>'v</c> はシグネチャの結果に現れない。Effekt の <c>askEmit[String]()</c> と同様に明示する。
/// <code lang="koka"><![CDATA[
/// fun ask-emit() : <ask<a>, emit<a>, console> ()
///   println("Start")
///   val v = ask()
///   emit(v)
///   println("End")
/// ]]></code>
/// </remarks>
/// <typeparam name="'v">ask の回答と emit の値の型。</typeparam>
/// <param name="out">出力先。</param>
let askEmit<'v> (out: Log) : Eff<unit> =
    eff {
        out "Start"
        let! (v: 'v) = Eff.perform Ask
        do! Eff.perform (Emit v)
        out "End"
    }

/// <summary>ハンドラーを被せて実行する。</summary>
/// <remarks>
/// パイプラインの左側が内側のハンドラーになる。Koka の <c>with</c> は上に書いたものが外側である。
/// <code lang="koka"><![CDATA[
/// pub fun example-ask-emit()
///   with ask-handler("Ask Value")
///   with emit-handler()
///   ask-emit()
/// ]]></code>
/// </remarks>
/// <param name="out">出力先。</param>
let exampleAskEmit (out: Log) : unit =
    askEmit<string> out |> emitHandler out |> askHandler "Ask Value" |> Eff.run

/// <summary><c>fun</c> 版のハンドラーで実行する。出力は <c>exampleAskEmit</c> と同じである。</summary>
/// <param name="out">出力先。</param>
let exampleAskEmitFn (out: Log) : unit =
    askEmit<string> out |> emitHandler out |> askHandlerFn "Ask Value" |> Eff.run

// ---- マルチショット: 記事の「継続の扱い」 ----

/// <summary>ask の継続を 2 回呼ぶハンドラー。継続がヒープにあるので何度でも呼べる。</summary>
/// <param name="value">ask の回答。</param>
/// <param name="action">ハンドラーを被せる計算。</param>
let askHandlerTwice (value: 'v) (action: Eff<'r>) : Eff<'r> =
    action
    |> Eff.handle
        [ Eff.ctl (fun (_: Ask<'v>) resume ->
              eff {
                  let! _ = resume value
                  return! resume value
              }) ]

/// <summary>emit の継続を 2 回呼ぶハンドラー。</summary>
/// <param name="out">出力先。</param>
/// <param name="action">ハンドラーを被せる計算。</param>
let emitHandlerTwice (out: Log) (action: Eff<'r>) : Eff<'r> =
    action
    |> Eff.handle
        [ Eff.ctl (fun (Emit(message: string)) resume ->
              out ("Emit " + message)

              eff {
                  let! _ = resume ()
                  return! resume ()
              }) ]

/// <summary>ask を 2 回再開する。ask の後ろの計算がすべて 2 回走る。</summary>
/// <remarks>
/// 出力は記事の Koka と同じである。
/// <code lang="text">
/// Start
/// Emit Ask Value
/// End
/// Emit Ask Value
/// End
/// </code>
/// </remarks>
/// <param name="out">出力先。</param>
let exampleAskTwice (out: Log) : unit =
    askEmit<string> out |> emitHandler out |> askHandlerTwice "Ask Value" |> Eff.run

/// <summary>emit を 2 回再開する。emit の後ろの計算だけが 2 回走る。</summary>
/// <remarks>
/// 出力は記事の Koka と同じである。
/// <code lang="text">
/// Start
/// Emit Ask Value
/// End
/// End
/// </code>
/// </remarks>
/// <param name="out">出力先。</param>
let exampleEmitTwice (out: Log) : unit =
    askEmit<string> out |> emitHandlerTwice out |> askHandler "Ask Value" |> Eff.run

/// <summary>両方を 2 回再開する。記事の Effekt の節は、この形で実行している。</summary>
/// <param name="out">出力先。</param>
let exampleBothTwice (out: Log) : unit =
    askEmit<string> out |> emitHandlerTwice out |> askHandlerTwice "Ask Value" |> Eff.run

// ---- おまけ: 再開可能な例外 ----

/// <summary>再開するかどうかをハンドラーへ伝える例外のような操作。</summary>
type ThrowResumable =
    /// <summary>メッセージと、再開してほしいかどうかを渡す。</summary>
    | ThrowResumable of message: string * needResume: bool

    interface Effect<unit>

/// <summary>メッセージを出力し、要求があれば再開するハンドラー。</summary>
/// <remarks>
/// 再開しなければ、例外と同じように残りの計算は走らない。
/// 「エフェクトは再開できる例外である」という説明は、この形を指している。
/// </remarks>
/// <param name="out">出力先。</param>
/// <param name="action">ハンドラーを被せる計算。</param>
let tryResumable (out: Log) (action: Eff<unit>) : Eff<unit> =
    action
    |> Eff.handle
        [ Eff.ctl (fun (ThrowResumable(message, needResume)) resume ->
              out message
              if needResume then resume () else Eff.ret ()) ]

/// <summary>結果が自然数でなければ操作を発行する減算。</summary>
/// <param name="x">被減数。</param>
/// <param name="y">減数。</param>
/// <param name="defaultValue">結果が自然数でないときの値。1 以上なら再開を求める。</param>
let subtractNaturalNumber (x: int) (y: int) (defaultValue: int) : Eff<int> =
    eff {
        let result = x - y

        if result <= 0 then
            do! Eff.perform (ThrowResumable($"{x} - {y} は自然数じゃないよ", defaultValue >= 1))
            return defaultValue
        else
            return result
    }

/// <summary>記事の 3 通りの減算を実行する。再開しなかった 2 番目だけ、「結果」の行が出ない。</summary>
/// <param name="out">出力先。</param>
let exampleSubtractNaturalNumber (out: Log) : unit =
    let attempt (label: string) x y defaultValue =
        out "------------"
        out label

        eff {
            let! r = subtractNaturalNumber x y defaultValue
            out $"結果: {r}"
        }
        |> tryResumable out
        |> Eff.run

        out "------------"

    attempt "2 - 1" 2 1 0
    attempt "2 - 3" 2 3 0
    attempt "2 - 3" 2 3 1
