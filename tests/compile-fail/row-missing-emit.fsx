// emit のハンドラーを付け忘れると run の呼び出しがコンパイルエラーになる。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/StaticRow.fs"

open EffectSystem.Samples.StaticRow

askEmit () |> runAsk "Ask Value" |> run
