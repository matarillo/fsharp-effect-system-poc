// rio-two-instantiations.fsx の回避策: 名前の違うインターフェイスに分ければ同時に要求できる。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"

open EffectSystem.Freer
open EffectSystem.Capability

type IAskAge =
    abstract AskAge: unit -> Eff<int>

type IAskName =
    abstract AskName: unit -> Eff<string>

let askAge<'R when 'R :> IAskAge> () : Rio<'R, int> = Rio(fun env -> env.AskAge())
let askName<'R when 'R :> IAskName> () : Rio<'R, string> = Rio(fun env -> env.AskName())

let askBoth () =
    rio {
        let! a = askAge ()
        let! b = askName ()
        return (a, b)
    }

type Env() =
    interface IAskAge with
        member _.AskAge() = Eff.ret 1

    interface IAskName with
        member _.AskName() = Eff.ret "x"

askBoth () |> Rio.run (Env()) |> Eff.run |> ignore
