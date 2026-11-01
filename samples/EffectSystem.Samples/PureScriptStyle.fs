/// <summary>記事の PureScript（Run）の節を FSharpPlus で写す。実装は <c>EffectSystem.Plus.RunStyle</c> にある。</summary>
/// <remarks>継続 <c>next</c> を 0 回、1 回、2 回呼ぶ例を並べ、呼ぶ回数が API の性質であることを示す。</remarks>
/// <category index="2">6 言語の写し</category>
module EffectSystem.Samples.PureScriptStyle

open FSharpPlus.Data
open EffectSystem.Plus.RunStyle
open EffectSystem.Samples.Effects

/// <summary>ハンドラーを重ねて実行する。</summary>
/// <remarks>PureScript の <c>askEmit # runAsk "Ask Value" # runEmit # runBaseEffect</c> に当たる。</remarks>
/// <param name="out">出力先。</param>
let exampleAskEmit (out: Log) : unit =
    askEmit<string> |> runAsk "Ask Value" |> runEmit |> runBase out

/// <summary>同じ記述を標準出力ではなく <c>Writer</c> へ解釈する。</summary>
/// <returns>出力されるはずの行。</returns>
let exampleToWriter () : string list =
    askEmit<string> |> runAsk "Ask Value" |> runEmit |> toWriter |> Writer.run |> snd

/// <summary>Run のエンコーディングのままで継続を 2 回呼ぶ。</summary>
/// <param name="out">出力先。</param>
let exampleAskTwice (out: Log) : unit =
    askEmit<string> |> runAskTwice "Ask Value" |> runEmit |> runBase out

/// <summary>継続を呼ばずに打ち切る。出力は <c>Start</c> の 1 行だけになる。</summary>
/// <param name="out">出力先。</param>
let exampleAskAbort (out: Log) : unit =
    askEmit<string> |> runAskAbort |> runEmit |> runBase out
