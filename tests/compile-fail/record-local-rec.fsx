// 対照群: inline の内側の局所の let rec なら、環境を型変数のまま再帰できる。
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

let inline loop (n: int) =
    let rec go i =
        rio {
            if i > 0 then
                do! emit (string i)
                do! go (i - 1)
        }

    go n

handlers {
    let! emit = emitHandler ignore
    return! loop 3 |> Rio.run {| Emit = emit |}
}
|> Eff.run
