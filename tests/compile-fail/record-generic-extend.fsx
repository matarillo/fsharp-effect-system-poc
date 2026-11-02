// 能力を足して、残りの環境をそのまま渡す。行を 1 段消すこの関数は、環境の型が不明なので書けない。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/EffektStyle.fs"
#load "../../samples/EffectSystem.Samples/RecordEnv.fs"

open EffectSystem.Capability
open EffectSystem.Samples.EffektStyle

let inline provideAsk (ask: IAsk<'a>) (Rio f) =
    Rio(fun env -> f {| env with Ask = ask |})
