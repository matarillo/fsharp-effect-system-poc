/// <summary>記事の Effekt の節を能力渡しで写す。</summary>
/// <remarks>
/// Effekt の <c>interface</c> は F# のインターフェイスに、<c>try { } with</c> は <c>reset</c> とオブジェクト式に、
/// <c>with</c> は <c>handlers { }</c> に対応する。
/// 再現できないのは能力の持ち出しの静的な禁止で、F# では実行時の <see cref="T:EffectSystem.Unhandled"/> になる。
/// </remarks>
/// <category index="2">6 言語の写し</category>
module EffectSystem.Samples.EffektStyle

open EffectSystem.Freer
open EffectSystem.Capability
open EffectSystem.Samples.Effects

/// <summary>値を取り出す能力。Effekt の <c>interface Ask[A] { def ask(): A }</c> に当たる。</summary>
/// <typeparam name="'a">取り出す値の型。</typeparam>
/// <remarks>
/// 操作をメソッドにしたので、型で探す必要がなくなる。この値を持っていることが、操作を呼べることの条件になる。
/// </remarks>
type IAsk<'a> =
    /// <summary>値を要求する。</summary>
    abstract Ask: unit -> Eff<'a>

/// <summary>値を発する能力。Effekt の <c>interface Emit[A] { def emit(value: A): Unit }</c> に当たる。</summary>
/// <typeparam name="'a">発する値の型。</typeparam>
type IEmit<'a> =
    /// <summary>値を発する。</summary>
    abstract Emit: 'a -> Eff<unit>

/// <summary>ask に固定値で答えるハンドラー。能力を作って計算へ渡す。</summary>
/// <remarks>
/// <code lang="scala"><![CDATA[
/// def askHandler[R, A](a : A) { action: () => R / Ask[A] }: R / {} = {
///   try { action() } with Ask[A] { def ask() = resume(a) }
/// }
/// ]]></code>
/// </remarks>
/// <param name="a">ask の回答。</param>
/// <param name="action">能力を受け取る計算。</param>
let askHandler (a: 'a) (action: IAsk<'a> -> Eff<'r>) : Eff<'r> =
    reset (fun prompt ->
        action
            { new IAsk<'a> with
                member _.Ask() = shift prompt (fun resume -> resume a) })

/// <summary><c>askHandler</c> と同じ答えを、継続を取り出さずに返すハンドラー。</summary>
/// <remarks>
/// 末尾で 1 回再開するだけの操作なら、<c>shift</c> も <c>reset</c> も要らない。
/// 計算から見た結果は同じで、操作のコストは約 5 分の 1 になる（<c>bench</c> の <c>Capability (Eff.ret)</c>）。
/// 区切りがなくなるので、持ち出した能力は <see cref="T:EffectSystem.Unhandled"/> にならず、そのまま答えを返す。
/// </remarks>
/// <param name="a">ask の回答。</param>
/// <param name="action">能力を受け取る計算。</param>
let askHandlerDirect (a: 'a) (action: IAsk<'a> -> Eff<'r>) : Eff<'r> =
    action
        { new IAsk<'a> with
            member _.Ask() = Eff.ret a }

/// <summary>emit を受けて出力し、再開するハンドラー。</summary>
/// <param name="out">出力先。</param>
/// <param name="action">能力を受け取る計算。</param>
let emitHandler (out: Log) (action: IEmit<string> -> Eff<'r>) : Eff<'r> =
    reset (fun prompt ->
        action
            { new IEmit<string> with
                member _.Emit value =
                    shift prompt (fun resume ->
                        out ("Emit " + value)
                        resume ()) })

/// <summary>ask と emit を使う計算。必要な能力が引数に現れる。</summary>
/// <remarks>
/// 能力を持っていなければ呼び出せないので、ハンドラーの付け忘れはコンパイルエラーになる。
/// Effekt の <c>def askEmit[A](): Unit / { Ask[A], Emit[A] }</c> に当たる。
/// </remarks>
/// <param name="ask">値を取り出す能力。</param>
/// <param name="emit">値を発する能力。</param>
/// <param name="out">出力先。</param>
let askEmit (ask: IAsk<'a>) (emit: IEmit<'a>) (out: Log) : Eff<unit> =
    eff {
        out "Start"
        let! v = ask.Ask()
        do! emit.Emit v
        out "End"
    }

/// <summary>ハンドラーを平らに重ねて実行する。</summary>
/// <remarks>
/// <code lang="scala"><![CDATA[
/// def exampleAskEmit() = {
///   with askHandler("Ask Value")
///   with emitHandler
///   askEmit[String]()
/// }
/// ]]></code>
/// </remarks>
/// <param name="out">出力先。</param>
let exampleAskEmit (out: Log) : unit =
    handlers {
        let! ask = askHandler "Ask Value"
        let! emit = emitHandler out
        return! askEmit ask emit out
    }
    |> Eff.run

// ---- マルチショット ----

/// <summary>ask の継続を 2 回呼ぶハンドラー。能力渡しでも Freer と同じく何度でも再開できる。</summary>
/// <param name="a">ask の回答。</param>
/// <param name="action">能力を受け取る計算。</param>
let askHandlerTwice (a: 'a) (action: IAsk<'a> -> Eff<'r>) : Eff<'r> =
    reset (fun prompt ->
        action
            { new IAsk<'a> with
                member _.Ask() =
                    shift prompt (fun resume ->
                        eff {
                            let! _ = resume a
                            return! resume a
                        }) })

/// <summary>emit の継続を 2 回呼ぶハンドラー。</summary>
/// <param name="out">出力先。</param>
/// <param name="action">能力を受け取る計算。</param>
let emitHandlerTwice (out: Log) (action: IEmit<string> -> Eff<'r>) : Eff<'r> =
    reset (fun prompt ->
        action
            { new IEmit<string> with
                member _.Emit value =
                    shift prompt (fun resume ->
                        out ("Emit " + value)

                        eff {
                            let! _ = resume ()
                            return! resume ()
                        }) })

/// <summary>両方を 2 回再開する。出力は記事の Effekt と同じである。</summary>
/// <remarks>
/// <code lang="text">
/// Start
/// Emit Ask Value
/// End
/// End
/// Emit Ask Value
/// End
/// End
/// </code>
/// </remarks>
/// <param name="out">出力先。</param>
let exampleBothTwice (out: Log) : unit =
    handlers {
        let! ask = askHandlerTwice "Ask Value"
        let! emit = emitHandlerTwice out
        return! askEmit ask emit out
    }
    |> Eff.run

// ---- 字句スコープ: 型ではなく能力でハンドラーを指す ----

/// <summary>同じ型のハンドラーを 2 つ重ねても、内側の計算はどちらでも指定して呼べる。</summary>
/// <remarks>
/// 結果は <c>("outer", "inner")</c> になる。型で探す Freer や OCaml では常に一番内側が選ばれるので、
/// <c>("inner", "inner")</c> になり、この書き分けはできない。
/// </remarks>
let exampleLexicalAddressing () : string * string =
    handlers {
        let! outer = askHandler "outer"
        let! inner = askHandler "inner"

        return!
            eff {
                let! a = outer.Ask()
                let! b = inner.Ask()
                return (a, b)
            }
    }
    |> Eff.run

/// <summary>能力をハンドラーの外へ持ち出して使う。実行時に <see cref="T:EffectSystem.Unhandled"/> になる。</summary>
/// <remarks>Effekt は型のキャプチャ追跡でこれを禁じるが、F# の型では表せない。</remarks>
let exampleEscapedCapability () : string =
    let leaked = askHandler "x" (fun ask -> Eff.ret ask) |> Eff.run
    leaked.Ask() |> Eff.run

// ---- インターフェイス制約をエフェクト行として使う ----

/// <summary>環境から値を取り出す計算。環境の型に <c>IAsk</c> の制約を加える。</summary>
/// <typeparam name="'R">環境の型。</typeparam>
/// <typeparam name="'v">取り出す値の型。</typeparam>
let ask<'R, 'v when 'R :> IAsk<'v>> () : Rio<'R, 'v> = Rio(fun env -> env.Ask())

/// <summary>環境を使って値を発する計算。環境の型に <c>IEmit</c> の制約を加える。</summary>
/// <typeparam name="'R">環境の型。</typeparam>
/// <typeparam name="'v">発する値の型。</typeparam>
/// <param name="v">発する値。</param>
let emit<'R, 'v when 'R :> IEmit<'v>> (v: 'v) : Rio<'R, unit> = Rio(fun env -> env.Emit v)

/// <summary>能力の引数を省いた計算。必要な能力の一覧が型として推論される。</summary>
/// <remarks>
/// 注釈なしで次の型に推論される。Koka の <c>&lt;ask&lt;a&gt;, emit&lt;a&gt;, console&gt; ()</c> に最も近い姿である。
/// <code lang="fsharp"><![CDATA[
/// val askEmitRow: out: Log -> Rio<'a,unit> when 'a :> IEmit<'b> and 'a :> IAsk<'b>
/// ]]></code>
/// </remarks>
/// <param name="out">出力先。</param>
let askEmitRow (out: Log) =
    rio {
        out "Start"
        let! v = ask ()
        do! emit v
        out "End"
    }

/// <summary>行を閉じる具体型。計算が必要とするインターフェイスをすべて実装する。</summary>
/// <remarks>
/// 必要なインターフェイスが欠けていれば <c>Rio.run</c> の呼び出しがコンパイルエラーになる。
/// F# のクラスは条件付きでインターフェイスを実装できないので、能力の数だけ転送を書く必要がある。
/// </remarks>
type AppEnv(ask: IAsk<string>, emit: IEmit<string>) =
    interface IAsk<string> with
        member _.Ask() = ask.Ask()

    interface IEmit<string> with
        member _.Emit v = emit.Emit v

/// <summary>ハンドラーで作った能力を環境にまとめて、行を閉じて実行する。</summary>
/// <param name="out">出力先。</param>
let exampleAskEmitRow (out: Log) : unit =
    handlers {
        let! ask = askHandler "Ask Value"
        let! emit = emitHandler out
        return! Rio.run (AppEnv(ask, emit)) (askEmitRow out)
    }
    |> Eff.run
