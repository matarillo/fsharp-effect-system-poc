// 対照群: 必要な提供者をすべて実装した環境なら型が通る。Orsak の環境は Rio と同じインターフェイス制約で集まる。
#r "nuget: Orsak, 0.7.0"

open Orsak

type IAsk<'a> =
    abstract Ask: unit -> 'a

type IEmit<'a> =
    abstract Emit: 'a -> unit

type IAskProvider<'a> =
    abstract Ask: IAsk<'a>

type IEmitProvider<'a> =
    abstract Emit: IEmit<'a>

let ask () =
    Effect.Create(fun (p: #IAskProvider<'v>) -> p.Ask.Ask())

let emit (v: 'v) =
    Effect.Create(fun (p: #IEmitProvider<'v>) -> p.Emit.Emit v)

let askEmit () : Effect<_, unit, string> =
    eff {
        let! v = ask ()
        do! emit v
    }

type Env() =
    interface IAskProvider<string> with
        member _.Ask =
            { new IAsk<string> with
                member _.Ask() = "x" }

    interface IEmitProvider<string> with
        member _.Emit =
            { new IEmit<string> with
                member _.Emit _ = () }

askEmit () |> Effect.run (Env()) |> ignore
