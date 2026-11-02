// 対照群: インターフェイス制約は抽象メンバーの型に書ける。計算をインターフェイスの背後に隠す形。
#load "../../src/EffectSystem/Core.fs"
#load "../../src/EffectSystem/Freer.fs"
#load "../../src/EffectSystem/Capability.fs"

open EffectSystem.Freer
open EffectSystem.Capability

type IAsk<'a> =
    abstract Ask: unit -> Eff<'a>

let ask<'R, 'v when 'R :> IAsk<'v>> () : Rio<'R, 'v> = Rio(fun env -> env.Ask())

type IService =
    abstract Run<'R when 'R :> IAsk<string>> : unit -> Rio<'R, string>

let service =
    { new IService with
        member _.Run() = ask () }

type Env() =
    interface IAsk<string> with
        member _.Ask() = Eff.ret "x"

service.Run() |> Rio.run (Env()) |> Eff.run |> ignore
