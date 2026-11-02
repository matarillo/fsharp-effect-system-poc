// 失敗の型が + を持たないと and! はコンパイルエラーになる。合わせる手段がないので、2 つ目の失敗を捨てずには書けない。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/ZioStyle.fs"

open EffectSystem.Samples.ZioStyle

let both () =
    zio {
        let! a = reserve ""
        and! b = reserve ""
        return a + b
    }
