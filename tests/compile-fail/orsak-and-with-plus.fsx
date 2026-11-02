// 対照群: 失敗の型が + を持てば、and! で両方の失敗を残せる。
#r "nuget: Orsak, 0.7.0"

open Orsak

type Problems =
    | Problems of string list

    static member (+)(Problems a, Problems b) = Problems(a @ b)

let check (name: string) : Effect<unit, int, Problems> = eff { return! Error(Problems [ name ]) }

let both () =
    eff {
        let! a = check "A"
        and! b = check "B"
        return a + b
    }
