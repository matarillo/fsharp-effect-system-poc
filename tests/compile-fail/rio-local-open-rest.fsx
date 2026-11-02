// 残りの行を列挙しない転送クラスは、外側の環境の能力を転送できないのでコンパイルエラーになる。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"

open EffectSystem.Freer
open EffectSystem.Capability

type IAsk<'a> =
    abstract Ask: unit -> Eff<'a>

type IEmit<'a> =
    abstract Emit: 'a -> Eff<unit>

let ask<'R, 'v when 'R :> IAsk<'v>> () : Rio<'R, 'v> = Rio(fun env -> env.Ask())
let emit<'R, 'v when 'R :> IEmit<'v>> (v: 'v) : Rio<'R, unit> = Rio(fun env -> env.Emit v)

type AskOverOpen<'R, 'v>(ask: IAsk<'v>, rest: 'R) =
    interface IAsk<'v> with
        member _.Ask() = ask.Ask()

let local =
    { new IAsk<string> with
        member _.Ask() = Eff.ret "local" }

let child () =
    rio {
        let! (v: string) = ask ()
        do! emit v
    }

let parent () =
    Rio.provideSome (fun env -> AskOverOpen<_, string>(local, env)) (child ())
