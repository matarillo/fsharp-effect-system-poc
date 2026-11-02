// 対照群: 提供者の名前を分ければ、型が同じサービスも同時に要求できる。Orsak の慣習の形で、rio-distinct-interfaces.fsx に当たる。
#r "nuget: Orsak, 0.7.0"

open Orsak

type IAsk<'a> =
    abstract Ask: unit -> 'a

type IAgeProvider =
    abstract Age: IAsk<int>

type INameProvider =
    abstract Name: IAsk<string>

let age () =
    Effect.Create(fun (p: #IAgeProvider) -> p.Age.Ask())

let name () =
    Effect.Create(fun (p: #INameProvider) -> p.Name.Ask())

let askBoth () : Effect<_, int * string, string> =
    eff {
        let! a = age ()
        let! b = name ()
        return (a, b)
    }

type Env() =
    interface IAgeProvider with
        member _.Age =
            { new IAsk<int> with
                member _.Ask() = 1 }

    interface INameProvider with
        member _.Name =
            { new IAsk<string> with
                member _.Ask() = "x" }

askBoth () |> Effect.run (Env()) |> ignore
