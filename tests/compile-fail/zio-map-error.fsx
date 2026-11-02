// 対照群: 失敗の型が違う計算も、mapError で共通の共用体へ移せばつなげられる。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/ZioStyle.fs"

open EffectSystem.Samples.ZioStyle

let reserveThenCharge () =
    zio {
        let! amount = reserve "mug" |> Zio.mapError Stock
        return! charge amount |> Zio.mapError Payment
    }
