// 対照群: 残りの行を列挙した転送クラスを provideSome に渡せば、親の行から IAsk が消える。
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
        do! emit v
    }

let parent () =
    Rio.provideSome (fun env -> AskOver<_, string>(local, env)) (child ())

type Env() =
    interface IEmit<string> with
        member _.Emit _ = Eff.ret ()

parent () |> Rio.run (Env()) |> Eff.run
