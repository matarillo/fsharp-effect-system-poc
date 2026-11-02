// 対照群: 失敗しない計算を () を受ける関数にすると、どの失敗の型にも合う。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/ZioStyle.fs"

open EffectSystem.Samples.ZioStyle

let succeeds () = zio { return 1 }
let asString: Zio<unit, string, int> = succeeds ()
let asInt: Zio<unit, int, int> = succeeds ()
