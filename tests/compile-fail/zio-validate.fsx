// 対照群: 失敗の型が + を持てば、and! で両方の失敗を残せる。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/ZioStyle.fs"

open EffectSystem.Samples.ZioStyle

let line () =
    zio {
        let! sku = checkSku ""
        and! quantity = checkQuantity 0
        return sku, quantity
    }
