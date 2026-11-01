// 対照群: すべてのエフェクトを処理すれば型が通る。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/StaticRow.fs"

open EffectSystem.Samples.StaticRow

askEmit () |> runAsk "Ask Value" |> runEmit ignore |> run
