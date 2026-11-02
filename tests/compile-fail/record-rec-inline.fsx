// 環境を型変数のまま扱う計算は inline が要るが、inline は再帰できない。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/EffektStyle.fs"
#load "../../samples/EffectSystem.Samples/RecordEnv.fs"

open EffectSystem.Capability
open EffectSystem.Samples.RecordEnv

let rec inline loop (n: int) =
    rio {
        if n > 0 then
            do! emit (string n)
            do! loop (n - 1)
    }
