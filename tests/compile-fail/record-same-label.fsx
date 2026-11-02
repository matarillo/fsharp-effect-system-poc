// 同じ名前のメンバーを別の型で 2 回要求する。メンバー制約は名前で 1 つに単一化される。
// rio-two-instantiations.fsx をそのままメンバー制約に置き換えた形。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/EffektStyle.fs"
#load "../../samples/EffectSystem.Samples/RecordEnv.fs"

open EffectSystem.Capability
open EffectSystem.Samples.RecordEnv

let inline askTwoTypes () =
    rio {
        let! (a: int) = ask ()
        let! (b: string) = ask ()
        return (a, b)
    }
