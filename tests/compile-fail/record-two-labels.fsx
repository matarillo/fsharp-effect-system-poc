// rio-two-instantiations.fsx の回避策: メンバーの名前を分ければ、同じジェネリックインターフェイスのまま同時に要求できる。
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
    let! age = askHandlerDirect 1
    let! name = askHandlerDirect "x"
    return! askBoth () |> Rio.run {| Age = age; Name = name |}
}
|> Eff.run
|> ignore
