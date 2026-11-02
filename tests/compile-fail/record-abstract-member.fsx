// rio-abstract-member.fsx をメンバー制約に置き換える。抽象メンバーは inline にできないので書けない。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/EffektStyle.fs"
#load "../../samples/EffectSystem.Samples/RecordEnv.fs"

open EffectSystem.Capability
open EffectSystem.Samples.EffektStyle

type IService =
    abstract Run< ^R when ^R: (member Ask: IAsk<string>)> : unit -> Rio< ^R, string>
