/// <summary>記事の Haskell（Polysemy）の節を写す。<c>interpret</c> の分岐には継続が現れない。</summary>
/// <remarks>
/// Extensible Effects の実装方式は Freer と同じなので、そのまま写せる。
/// 再現できないのは <c>Member</c> 制約による行で、エフェクトの集合は型に残らない。
/// </remarks>
/// <category index="2">6 言語の写し</category>
module EffectSystem.Samples.PolysemyStyle

open EffectSystem.Freer
open EffectSystem.Samples.Effects

/// <summary>ask に固定値で答えるインタープリター。</summary>
/// <remarks>
/// <code lang="haskell"><![CDATA[
/// runAsk :: v -> Sem (Ask v ': r) a -> Sem r a
/// runAsk value = interpret $ \case
///   Ask -> pure value
/// ]]></code>
/// </remarks>
/// <param name="value">ask の回答。</param>
/// <param name="m">解釈する計算。</param>
let runAsk (value: 'v) (m: Eff<'a>) : Eff<'a> =
    m |> Eff.handle [ Eff.interpret (fun (_: Ask<'v>) -> Eff.ret value) ]

/// <summary>emit を受けて出力するインタープリター。</summary>
/// <remarks>
/// <code lang="haskell"><![CDATA[
/// runEmit = interpret $ \case
///   Emit a -> embed $ putStrLn a
/// ]]></code>
/// </remarks>
/// <param name="out">出力先。</param>
/// <param name="m">解釈する計算。</param>
let runEmit (out: Log) (m: Eff<'a>) : Eff<'a> =
    m
    |> Eff.handle
        [ Eff.interpret (fun (Emit(a: string)) ->
              out a
              Eff.ret ()) ]

/// <summary>ask と emit を使う計算。</summary>
/// <param name="out">出力先。</param>
let askEmit (out: Log) : Eff<unit> =
    eff {
        out "Start"
        let! (v: string) = Eff.perform Ask
        do! Eff.perform (Emit v)
        out "End"
    }

/// <summary>インタープリターを重ねて実行する。</summary>
/// <remarks>Haskell の <c>runM . runAsk "Ask Value" . runEmit $ askEmit</c> に当たる。</remarks>
/// <param name="out">出力先。</param>
let exampleAskEmit (out: Log) : unit =
    askEmit out |> runEmit out |> runAsk "Ask Value" |> Eff.run

/// <summary>操作を呼んだ時点では何も実行されないことを示す。記事の「処理フロー」の節が述べている性質である。</summary>
/// <remarks>組み立てるだけなら出力は 0 行で、<c>Eff.run</c> で初めて走る。同じ値は何度でも実行できる。</remarks>
/// <param name="out">出力先。</param>
/// <returns>実行した回数。</returns>
let exampleDataNotExecution (out: Log) : int =
    let program = askEmit out |> runEmit out |> runAsk "Ask Value"
    out "built"
    Eff.run program
    Eff.run program
    2
