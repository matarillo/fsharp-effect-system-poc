// 処理済みのエフェクトをもう一度処理しようとするとコンパイルエラーになる。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/StaticRow.fs"

open EffectSystem.Samples.StaticRow

askEmit () |> runAsk "a" |> runAsk "b" |> runEmit ignore |> run
