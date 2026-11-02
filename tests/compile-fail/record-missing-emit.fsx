// 環境に Emit がないと Rio.run の呼び出しがコンパイルエラーになる。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/EffektStyle.fs"
#load "../../samples/EffectSystem.Samples/RecordEnv.fs"

open EffectSystem.Freer
open EffectSystem.Capability
open EffectSystem.Samples.EffektStyle
open EffectSystem.Samples.RecordEnv

handlers {
    let! ask = askHandlerDirect "x"
    return! askEmit ignore |> Rio.run {| Ask = ask |}
}
|> Eff.run
