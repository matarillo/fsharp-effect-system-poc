// 拡張メンバーはメンバー制約から見えない。既存の型に能力を後付けして環境にすることはできない。
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

module Ext =
    type System.Text.StringBuilder with
        member _.Ask =
            { new IAsk<string> with
                member _.Ask() = Eff.ret "x" }

open Ext

// 通常の呼び出しなら見える。
let visible = System.Text.StringBuilder().Ask

(ask (): Rio<_, string>) |> Rio.run (System.Text.StringBuilder()) |> Eff.run |> ignore
