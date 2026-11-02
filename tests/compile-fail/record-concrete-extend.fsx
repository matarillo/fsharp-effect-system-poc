// 対照群: 残りの環境の型が具体的なら、匿名レコードの copy-and-update で能力を足せる。行多相ではない。
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

let provideAsk (ask: IAsk<'a>) (env: {| Emit: IEmit<'a> |}) = {| env with Ask = ask |}

handlers {
    let! ask = askHandlerDirect "x"
    let! emit = emitHandler ignore
    return! askEmit ignore |> Rio.run (provideAsk ask {| Emit = emit |})
}
|> Eff.run
