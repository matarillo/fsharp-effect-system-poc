/// <summary>PureScript の Run を FSharpPlus の <c>Free</c> で写す。</summary>
/// <remarks>
/// <para>
/// <b>Functor の形はそのまま持ち込める。</b>
/// F# にも PureScript にも GADT がないので、PureScript も F# も <c>AskF v a = Ask (v -> a)</c> という形で回答型を表す。
/// 継続 <c>next</c> はただの関数なので、呼ぶ回数はハンドラーが決める。
/// 「Run の継続は暗黙で 1 回だけ」という説明は <c>interpret</c> という API の性質で、この形の制約ではない。
/// </para>
/// <para>
/// <b>行多相は持ち込めない。</b>
/// <c>Run (ASK v + r)</c> の代わりに、残りの行を手書きの和で表す。型から 1 段ずつ消える様子は再現できるが、
/// エフェクトを足すたびに和の型とすべてのハンドラーが変わる。
/// FSharpPlus の <c>Coproduct</c> は使っていない。入れ子にすると、最適化なしのビルドでは実行時に失敗することがあるからである。
/// 再現条件はテストで固定している。
/// </para>
/// <para>
/// <b>副作用はすべて操作にする。</b>
/// <c>Free</c> の <c>monad { }</c> は最初の区間を遅延しない。<c>eff { }</c> と違い、最初の <c>let!</c> より前のコードは組み立て時に走る。
/// そのためログも <see cref="T:EffectSystem.Plus.RunStyle.LogF`1"/> という操作で表している。
/// </para>
/// </remarks>
/// <namespacedoc>
///   <summary>FSharpPlus を使う移植。本体の <c>EffectSystem</c> は FSharpPlus に依存しないので、別のプロジェクトに分けている。</summary>
/// </namespacedoc>
module EffectSystem.Plus.RunStyle

open FSharpPlus
open FSharpPlus.Data

/// <summary>ask 操作の Functor。PureScript の <c>data AskF v a = Ask (v -> a)</c> に当たる。</summary>
/// <typeparam name="'v">回答の型。</typeparam>
/// <typeparam name="'next">回答を受け取った後の計算の型。</typeparam>
/// <remarks>回答型を GADT で表せないので、「回答を受け取って続きを返す関数」を持つ。これが継続である。</remarks>
type AskF<'v, 'next> =
    /// <summary>回答を待って止まっている。</summary>
    | Ask of ('v -> 'next)

    /// <summary>続きの計算を変換する。FSharpPlus の <c>map</c> が SRTP で解決する。</summary>
    static member Map(Ask k, f: 'next -> 'u) = Ask(k >> f)

/// <summary>ログ出力の操作。PureScript の <c>liftEffect (log ...)</c> に当たる。</summary>
/// <typeparam name="'next">出力の後の計算の型。</typeparam>
/// <remarks>回答がないので、続きの計算を関数ではなく値で持つ。</remarks>
type LogF<'next> =
    /// <summary>文字列を出力してから続ける。</summary>
    | Log of string * 'next

    /// <summary>続きの計算を変換する。</summary>
    static member Map(Log(m, next), f: 'next -> 'u) = Log(m, f next)

/// <summary>emit とログからなる行。<c>Run (EMIT v + EFFECT)</c> に当たる。</summary>
/// <typeparam name="'v">emit する値の型。</typeparam>
/// <typeparam name="'next">続きの計算の型。</typeparam>
type EmitLogF<'v, 'next> =
    /// <summary>値を発してから続ける。</summary>
    | Emit of 'v * 'next
    /// <summary>ログの操作。</summary>
    | EmitLog of LogF<'next>

    /// <summary>続きの計算を変換する。</summary>
    static member Map(x: EmitLogF<'v, 'next>, f: 'next -> 'u) : EmitLogF<'v, 'u> =
        match x with
        | Emit(v, next) -> Emit(v, f next)
        | EmitLog(Log(m, next)) -> EmitLog(Log(m, f next))

/// <summary>ask と残りの行からなる全体の行。<c>Run (ASK v + EMIT v + EFFECT)</c> に当たる。</summary>
/// <typeparam name="'v">回答と emit の値の型。</typeparam>
/// <typeparam name="'next">続きの計算の型。</typeparam>
type AskEmitLogF<'v, 'next> =
    /// <summary>ask の操作。</summary>
    | AskOp of AskF<'v, 'next>
    /// <summary>残りの行の操作。<c>runAsk</c> の後もそのまま残る。</summary>
    | Rest of EmitLogF<'v, 'next>

    /// <summary>続きの計算を変換する。</summary>
    static member Map(x: AskEmitLogF<'v, 'next>, f: 'next -> 'u) : AskEmitLogF<'v, 'u> =
        match x with
        | AskOp(Ask k) -> AskOp(Ask(k >> f))
        | Rest r -> Rest(EmitLogF<'v, 'next>.Map(r, f))

/// <summary>ask を要求する計算。</summary>
let ask<'v> : Free<AskEmitLogF<'v, 'v>, 'v> = Free.liftF (AskOp(Ask id))

/// <summary>値を発する計算。</summary>
let emit (v: 'v) : Free<AskEmitLogF<'v, unit>, unit> = Free.liftF (Rest(Emit(v, ())))

/// <summary>ログを出力する計算。</summary>
let log<'v> (m: string) : Free<AskEmitLogF<'v, unit>, unit> =
    Free.liftF (Rest(EmitLog(Log(m, ()))))

/// <summary>ask と emit を使う計算。記事の <c>askEmit</c> に当たる。</summary>
let askEmit<'v> : Free<AskEmitLogF<'v, unit>, unit> =
    monad {
        do! log<'v> "Start"
        let! v = ask<'v>
        do! emit v
        do! log<'v> "End"
    }

/// <summary>ask を解釈して行から消すハンドラー。<c>runAsk :: v -> Run (ASK v + r) ~> Run r</c> に当たる。</summary>
/// <remarks>引数と戻り値の型を比べると、<c>AskOp</c> の段が消えている。</remarks>
/// <param name="value">ask の回答。</param>
/// <param name="m">解釈する計算。</param>
let rec runAsk (value: 'v) (m: Free<AskEmitLogF<'v, unit>, unit>) : Free<EmitLogF<'v, unit>, unit> =
    match Free.run m with
    | Choice1Of2() -> Free.Pure()
    | Choice2Of2(AskOp(Ask next)) -> runAsk value (next value)
    | Choice2Of2(Rest r) -> Roll(EmitLogF<'v, _>.Map(r, runAsk value))

/// <summary>emit をログへ置き換えて行から消すハンドラー。<c>runEmit :: Run (EMIT String + EFFECT) ~> Run EFFECT</c> に当たる。</summary>
/// <param name="m">解釈する計算。</param>
let rec runEmit (m: Free<EmitLogF<string, unit>, unit>) : Free<LogF<unit>, unit> =
    match Free.run m with
    | Choice1Of2() -> Free.Pure()
    | Choice2Of2(Emit(v, next)) -> Roll(Log("Emit " + v, runEmit next))
    | Choice2Of2(EmitLog(Log(s, next))) -> Roll(Log(s, runEmit next))

/// <summary>最後の解釈。PureScript の <c>runBaseEffect</c> に当たる。</summary>
/// <param name="out">ログの出力先。</param>
/// <param name="m">実行する計算。</param>
let rec runBase (out: string -> unit) (m: Free<LogF<unit>, unit>) : unit =
    match Free.run m with
    | Choice1Of2() -> ()
    | Choice2Of2(Log(s, next)) ->
        out s
        runBase out next

/// <summary>同じ記述を純粋な <c>Writer</c> へ畳み込む。本番とテストでハンドラーを差し替える例である。</summary>
/// <remarks><c>Free.fold</c> が任意のモナドへ解釈できることが、FSharpPlus を使う主な利点である。</remarks>
/// <param name="m">解釈する計算。</param>
let toWriter (m: Free<LogF<unit>, unit>) : Writer<string list, unit> =
    m |> Free.fold (fun (Log(s, next)) -> Writer.tell [ s ] |> map (fun () -> next))

/// <summary>継続 <c>next</c> を 2 回呼ぶハンドラー。</summary>
/// <remarks>Run の <c>runChoose</c> が <c>k true</c> と <c>k false</c> を両方呼ぶのと同じことを、このエンコーディングのままで行っている。</remarks>
/// <param name="value">ask の回答。</param>
/// <param name="m">解釈する計算。</param>
let rec runAskTwice (value: 'v) (m: Free<AskEmitLogF<'v, unit>, unit>) : Free<EmitLogF<'v, unit>, unit> =
    match Free.run m with
    | Choice1Of2() -> Free.Pure()
    | Choice2Of2(AskOp(Ask next)) ->
        monad {
            do! runAskTwice value (next value)
            return! runAskTwice value (next value)
        }
    | Choice2Of2(Rest r) -> Roll(EmitLogF<'v, _>.Map(r, runAskTwice value))

/// <summary>継続 <c>next</c> を呼ばずに打ち切るハンドラー。</summary>
/// <remarks>Run の <c>runExcept</c> が継続を呼ばないのと同じである。</remarks>
/// <param name="m">解釈する計算。</param>
let rec runAskAbort (m: Free<AskEmitLogF<'v, unit>, unit>) : Free<EmitLogF<'v, unit>, unit> =
    match Free.run m with
    | Choice1Of2() -> Free.Pure()
    | Choice2Of2(AskOp(Ask _)) -> Free.Pure()
    | Choice2Of2(Rest r) -> Roll(EmitLogF<'v, _>.Map(r, runAskAbort))
