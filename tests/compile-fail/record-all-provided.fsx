// 対照群: 必要なメンバーをすべて持つ環境なら型が通る。余分なフィールドがあってもよく、クラスでもよい。
// 環境は上位の型に上げずに受けるので、受け取った関数からも元の型のまま返せる。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"
#load "../../samples/EffectSystem.Samples/Effects.fs"
#load "../../samples/EffectSystem.Samples/EffektStyle.fs"
#load "../../samples/EffectSystem.Samples/RecordEnv.fs"

open EffectSystem.Freer
open EffectSystem.Capability
open EffectSystem.Samples.EffektStyle
open EffectSystem.Samples.RecordEnv

type ClassEnv(ask: IAsk<string>, emit: IEmit<string>) =
    member _.Ask = ask
    member _.Emit = emit

let inline withAsk (env: ^R) =
    (^R: (member Ask: IAsk<string>) env), env

handlers {
    let! ask = askHandlerDirect "x"
    let! emit = emitHandler ignore
    let _, kept = withAsk {| Ask = ask; Extra = 1 |}
    kept.Extra |> ignore

    return!
        eff {
            do! askEmit ignore |> Rio.run {| Ask = ask; Emit = emit |}
            do! askEmit ignore |> Rio.run {| Ask = ask; Emit = emit; Extra = 1 |}
            do! askEmit ignore |> Rio.run (ClassEnv(ask, emit))
        }
}
|> Eff.run
