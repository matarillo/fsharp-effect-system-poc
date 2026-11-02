// 子が、転送クラスの列挙していない能力を要求するとコンパイルエラーになる。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"

open EffectSystem.Freer
open EffectSystem.Capability

type IAsk<'a> =
    abstract Ask: unit -> Eff<'a>

type IEmit<'a> =
    abstract Emit: 'a -> Eff<unit>

type ILog =
    abstract Log: string -> Eff<unit>

let ask<'R, 'v when 'R :> IAsk<'v>> () : Rio<'R, 'v> = Rio(fun env -> env.Ask())
let logIt<'R when 'R :> ILog> (s: string) : Rio<'R, unit> = Rio(fun env -> env.Log s)

type AskOver<'R, 'v when 'R :> IEmit<'v>>(ask: IAsk<'v>, rest: 'R) =
    interface IAsk<'v> with
        member _.Ask() = ask.Ask()

    interface IEmit<'v> with
        member _.Emit v = rest.Emit v

let local =
    { new IAsk<string> with
        member _.Ask() = Eff.ret "local" }

let child () =
    rio {
        let! (v: string) = ask ()
        do! logIt v
    }

let parent () =
    Rio.provideSome (fun env -> AskOver<_, string>(local, env)) (child ())
