// 失敗の型が + を持たないと and! はコンパイルエラーになる。zio-validate-without-plus.fsx と同じ。
#r "nuget: Orsak, 0.7.0"

open Orsak

type Problem = Problem of string

let check (name: string) : Effect<unit, int, Problem> = eff { return! Error(Problem name) }

let both () =
    eff {
        let! a = check "A"
        and! b = check "B"
        return a + b
    }
