// 同じジェネリックインターフェイスの別のインスタンスを、1 つの型引数に同時に要求する。
// Koka の <ask<int>, ask<string>> に当たる形。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"

open EffectSystem.Freer
open EffectSystem.Capability

type IAsk<'a> =
    abstract Ask: unit -> Eff<'a>

let ask<'R, 'v when 'R :> IAsk<'v>> () : Rio<'R, 'v> = Rio(fun env -> env.Ask())

let askBoth () =
    rio {
        let! (a: int) = ask ()
        let! (b: string) = ask ()
        return (a, b)
    }

type Env() =
    interface IAsk<int> with
        member _.Ask() = Eff.ret 1

    interface IAsk<string> with
        member _.Ask() = Eff.ret "x"

askBoth () |> Rio.run (Env()) |> Eff.run |> ignore
