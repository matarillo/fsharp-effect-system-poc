// inline を付けない計算。定義は通るが、最初の使用で環境の型が固定され、別の環境では使えない。
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

let askOnly () =
    rio {
        let! (v: string) = ask ()
        return v
    }

handlers {
    let! ask = askHandlerDirect "x"

    return!
        eff {
            let! _ = askOnly () |> Rio.run {| Ask = ask |}
            return! askOnly () |> Rio.run {| Ask = ask; Extra = 1 |}
        }
}
|> Eff.run
|> ignore
