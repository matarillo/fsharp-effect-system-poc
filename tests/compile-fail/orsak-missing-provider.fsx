// 環境が IEmitProvider を実装していないと Effect.run の呼び出しがコンパイルエラーになる。rio-missing-emit.fsx と同じ。
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

askEmit () |> Effect.run (Env()) |> ignore
