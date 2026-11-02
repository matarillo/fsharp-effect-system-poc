// 失敗しない計算を値にすると、値の制限で失敗の型が 1 つに固定され、2 つ目の型でコンパイルエラーになる。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/ZioStyle.fs"

open EffectSystem.Samples.ZioStyle

let succeeds = zio { return 1 }
let asString: Zio<unit, string, int> = succeeds
let asInt: Zio<unit, int, int> = succeeds
