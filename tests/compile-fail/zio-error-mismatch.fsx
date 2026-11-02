// 失敗の型が違う計算は、そのままではつなげられない。ZIO と違い、E が上界へ広がらない。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/ZioStyle.fs"

open EffectSystem.Samples.ZioStyle

let reserveThenCharge () =
    zio {
        let! amount = reserve "mug"
        return! charge amount
    }
