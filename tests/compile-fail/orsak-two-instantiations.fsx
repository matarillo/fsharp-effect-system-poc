// 同じジェネリックな提供者の別のインスタンスを、1 つの環境の型に同時に要求する。rio-two-instantiations.fsx と同じ形。
// 2 つの環境の型変数が 1 つに単一化できず、Bind が多重定義されているので FS0041 として報告される。
#r "nuget: Orsak, 0.7.0"

open Orsak

type IAsk<'a> =
    abstract Ask: unit -> 'a

type IAskProvider<'a> =
    abstract Ask: IAsk<'a>

let ask () =
    Effect.Create(fun (p: #IAskProvider<'v>) -> p.Ask.Ask())

let askBoth () : Effect<_, int * string, string> =
    eff {
        let! (a: int) = ask ()
        let! (b: string) = ask ()
        return (a, b)
    }
