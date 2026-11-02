// 環境が型変数のままの計算を引数で受けて、2 種類の環境で動かす。引数の時点で環境の型が 1 つに決まるので書けない。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/EffektStyle.fs"
#load "../../samples/EffectSystem.Samples/RecordEnv.fs"

open EffectSystem.Freer
open EffectSystem.Capability
open EffectSystem.Samples.EffektStyle

let inline runBoth (ask: IAsk<string>) m =
    eff {
        let! a = m |> Rio.run {| Ask = ask |}
        let! b = m |> Rio.run {| Ask = ask; Extra = 1 |}
        return (a, b)
    }
