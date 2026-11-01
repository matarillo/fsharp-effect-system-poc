// 環境が IEmit を実装していないと Rio.run の呼び出しがコンパイルエラーになる。
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

let askEmit () =
    rio {
        let! v = ask ()
        do! emit v
    }

type Env() =
    interface IAsk<string> with
        member _.Ask() = Eff.ret "x"

askEmit () |> Rio.run (Env()) |> Eff.run
