module EffectSystem.Tests.Tests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Expecto
open FSharpPlus
open FSharpPlus.Data
open EffectSystem
open EffectSystem.Direct
open EffectSystem.Freer
open EffectSystem.Capability
open EffectSystem.Samples
open EffectSystem.Samples.Effects

/// サンプルの出力を行のリストとして受け取る。
let private capture (f: Log -> unit) : string list =
    let lines = ResizeArray<string>()
    f lines.Add
    List.ofSeq lines

/// 例外で終わるサンプルの、それまでの出力と例外を受け取る。
let private captureFailure (f: Log -> unit) : string list * exn =
    let lines = ResizeArray<string>()

    try
        f lines.Add
        failwith "例外を期待したが正常に終わった"
    with e ->
        (List.ofSeq lines, e)

let private startEmitEnd = [ "Start"; "Emit Ask Value"; "End" ]

/// 記事に掲載された実行結果をそのまま期待値にする。
let articleOutputs =
    testList
        "記事の実行結果"
        [ testCase "Koka: ask-emit"
          <| fun () -> Expect.equal (capture KokaStyle.exampleAskEmit) startEmitEnd ""

          testCase "Koka: fun で定義しても同じ"
          <| fun () -> Expect.equal (capture KokaStyle.exampleAskEmitFn) startEmitEnd ""

          testCase "Koka: ask を 2 回再開"
          <| fun () ->
              Expect.equal
                  (capture KokaStyle.exampleAskTwice)
                  [ "Start"; "Emit Ask Value"; "End"; "Emit Ask Value"; "End" ]
                  ""

          testCase "Koka: emit を 2 回再開"
          <| fun () ->
              Expect.equal (capture KokaStyle.exampleEmitTwice) [ "Start"; "Emit Ask Value"; "End"; "End" ] ""

          testCase "Koka: 再開可能な例外"
          <| fun () ->
              Expect.equal
                  (capture KokaStyle.exampleSubtractNaturalNumber)
                  [ "------------"
                    "2 - 1"
                    "結果: 1"
                    "------------"
                    "------------"
                    "2 - 3"
                    "2 - 3 は自然数じゃないよ"
                    "------------"
                    "------------"
                    "2 - 3"
                    "2 - 3 は自然数じゃないよ"
                    "結果: 1"
                    "------------" ]
                  ""

          let bothTwice =
              [ "Start"; "Emit Ask Value"; "End"; "End"; "Emit Ask Value"; "End"; "End" ]

          testCase "Effekt: askEmit"
          <| fun () -> Expect.equal (capture EffektStyle.exampleAskEmit) startEmitEnd ""

          testCase "Effekt: 両方を 2 回再開"
          <| fun () -> Expect.equal (capture EffektStyle.exampleBothTwice) bothTwice ""

          testCase "Effekt の出力は Freer でも同じ"
          <| fun () -> Expect.equal (capture KokaStyle.exampleBothTwice) bothTwice ""

          testCase "Unison: emitAsk"
          <| fun () -> Expect.equal (capture UnisonStyle.exampleEmitAsk) startEmitEnd ""

          testCase "Unison: ask を 2 回再開"
          <| fun () ->
              Expect.equal
                  (capture UnisonStyle.exampleAskTwice)
                  [ "Start"; "Emit Ask Value"; "End"; "Emit Ask Value"; "End" ]
                  ""

          testCase "OCaml: ask_emit"
          <| fun () -> Expect.equal (capture OCamlStyle.exampleAskEmit) startEmitEnd ""

          testCase "OCaml: ハンドラーの付け忘れは ask の位置で Unhandled"
          <| fun () ->
              let lines, e = captureFailure OCamlStyle.exampleMissingHandler
              Expect.equal lines [ "Start" ] ""

              match e with
              | Unhandled(:? Ask<string>) -> ()
              | other -> failtestf "Unhandled(Ask) を期待: %O" other

          testCase "OCaml: 2 回再開は ContinuationAlreadyResumed"
          <| fun () ->
              let lines, e = captureFailure OCamlStyle.exampleResumeTwice
              Expect.equal lines [ "--------"; "Start"; "Emit Ask Value"; "End" ] ""

              match e with
              | ContinuationAlreadyResumed -> ()
              | other -> failtestf "ContinuationAlreadyResumed を期待: %O" other

          testCase "OCaml: Shallow の State"
          <| fun () ->
              Expect.equal
                  (capture OCamlStyle.exampleState)
                  [ "Got value: Hello, world!"; "Got new value: <<<Hello, world!>>>" ]
                  ""

          testCase "Polysemy: askEmit"
          <| fun () -> Expect.equal (capture PolysemyStyle.exampleAskEmit) [ "Start"; "Ask Value"; "End" ] ""

          testCase "PureScript: askEmit"
          <| fun () -> Expect.equal (capture PureScriptStyle.exampleAskEmit) startEmitEnd "" ]

/// マニュアルの REPL の結果を期待値にする。
let manualOutputs =
    testList
        "OCaml マニュアル 12.24"
        [ testCase "24.1 Basics"
          <| fun () -> Expect.equal (OCamlManual.basics ()) 3 ""

          testCase "24.2 Concurrency"
          <| fun () -> Expect.equal (OCamlManual.concurrency ()) (42, 0) ""

          let threads =
              [ "[t1] Sending 0"; "[t2] Sending 1"; "[t2] received 0"; "[t1] received 1" ]

          testCase "24.3 User-level threads"
          <| fun () -> Expect.equal (capture OCamlManual.userLevelThreads) threads ""

          testCase "24.3 discontinue で finally が走る"
          <| fun () ->
              Expect.equal
                  (capture OCamlManual.resumingWithAnException)
                  [ "open"; "close"; "ImproperSynchronization" ]
                  ""

          testCase "24.4 Control inversion (list)"
          <| fun () -> Expect.equal (OCamlManual.controlInversionList ()) [ 1; 2; 3 ] ""

          testCase "24.4 Control inversion (string)"
          <| fun () -> Expect.equal (OCamlManual.controlInversionString ()) [ 'O'; 'C'; 'a'; 'm'; 'l' ] ""

          testCase "24.4 列は使い捨て"
          <| fun () ->
              let outcome =
                  try
                      OCamlManual.ephemeralSequence ()
                      "no failure"
                  with ContinuationAlreadyResumed ->
                      "already resumed"

              Expect.equal outcome "already resumed" ""

          testCase "24.4 との差: 同じ要素型の invert を入れ子にすると混ざる"
          <| fun () -> Expect.equal (OCamlManual.nestedInvertSameType ()) [ 10; 20 ] ""

          testCase "24.5 Nesting handlers"
          <| fun () -> Expect.equal (OCamlManual.baz ()) "Hello, world!" ""

          testCase "24.5 Unhandled effects"
          <| fun () ->
              let handled =
                  try
                      OCamlManual.bar ()
                  with Unhandled(:? OCamlManual.F) ->
                      "Saw Effect.Unhandled exception"

              Expect.equal handled "Saw Effect.Unhandled exception" ""

          testCase "24.5 Linear continuations"
          <| fun () ->
              let e =
                  try
                      OCamlManual.linearContinuations () |> ignore
                      None
                  with
                  | ContinuationAlreadyResumed -> Some "already resumed"

              Expect.equal e (Some "already resumed") ""

          testCase "24.6 プロトコルに従う"
          <| fun () ->
              Expect.equal
                  (capture OCamlManual.shallowFollowsProtocol)
                  [ "Send 42"; "Recv: 42"; "Send 43"; "Recv: 43" ]
                  ""

          testCase "24.6 プロトコル違反"
          <| fun () ->
              let lines, e = captureFailure OCamlManual.shallowViolatesProtocol
              Expect.equal lines [ "Send 0"; "Send 1" ] ""
              Expect.equal e.Message "protocol violation" ""

          testCase "24.6 他の操作は外側へ転送される"
          <| fun () -> Expect.equal (OCamlManual.shallowForwardsOtherEffects ()) "forwarded" "" ]

/// Direct エンジン固有の意味論。
let directSemantics =
    testList
        "Direct"
        [ testCase "ハンドラー内の perform は外側のハンドラーへ行く"
          <| fun () ->
              let result =
                  Deep.tryWith
                      (fun () ->
                          Deep.tryWith
                              (fun () ->
                                  let (v: string) = perform Ask
                                  v + "!")
                              [ Deep.ctl (fun (_: Ask<string>) k ->
                                    let (outer: string) = perform Ask
                                    Deep.resume k (outer + "-inner")) ])
                      [ Deep.ctl (fun (_: Ask<string>) k -> Deep.resume k "outer") ]

              Expect.equal result "outer-inner!" ""

          testCase "転送先の discontinue が元の perform に届く"
          <| fun () ->
              let result =
                  Deep.tryWith
                      (fun () ->
                          Deep.tryWith
                              (fun () ->
                                  try
                                      let (v: string) = perform Ask
                                      v
                                  with e ->
                                      "caught " + e.Message)
                              [])
                      [ Deep.ctl (fun (_: Ask<string>) k -> Deep.discontinue k (exn "boom")) ]

              Expect.equal result "caught boom" ""

          testCase "fn の例外は perform の位置に届く"
          <| fun () ->
              let result =
                  Deep.tryWith
                      (fun () ->
                          try
                              let (v: string) = perform Ask
                              v
                          with e ->
                              "caught " + e.Message)
                      [ Deep.fn (fun (_: Ask<string>) -> failwith "boom") ]

              Expect.equal result "caught boom" ""

          testCase "exnc は計算の例外を受ける"
          <| fun () ->
              let result =
                  Deep.matchWith
                      (fun () -> failwith "boom")
                      { retc = fun () -> "value"
                        exnc = fun e -> "exn " + e.Message
                        effc = [] }

              Expect.equal result "exn boom" ""

          testCase "Shallow: discontinueWith は perform の位置で例外にする"
          <| fun () ->
              let handler: Shallow.Handler<string, string> =
                  { retc = id
                    exnc = fun e -> "exn " + e.Message
                    effc =
                      [ Shallow.ctl (fun (_: Ask<string>) k ->
                            Shallow.discontinueWith
                                k
                                (exn "boom")
                                { retc = id
                                  exnc = fun e -> "second handler saw " + e.Message
                                  effc = [] }) ] }

              let result =
                  Shallow.continueWith
                      (Shallow.fiber (fun () ->
                          let (v: string) = perform Ask
                          v))
                      ()
                      handler

              Expect.equal result "second handler saw boom" ""

          testCase "操作はスレッド境界を越えない"
          <| fun () ->
              let inner =
                  Deep.tryWith
                      (fun () ->
                          try
                              Task.Run(fun () -> (perform Ask: string)).Result
                          with :? AggregateException as e ->
                              match e.InnerException with
                              | Unhandled _ -> "unhandled on another thread"
                              | other -> other.Message)
                      [ Deep.fn (fun (_: Ask<string>) -> "handled") ]

              Expect.equal inner "unhandled on another thread" ""

          testCase "fn は操作の回数だけスタックを伸ばさない"
          <| fun () ->
              let n = 100_000

              let sum =
                  Deep.tryWith
                      (fun () ->
                          let mutable s = 0

                          for _ in 1..n do
                              s <- s + perform (Ask: Ask<int>)

                          s)
                      [ Deep.fn (fun (_: Ask<int>) -> 1) ]

              Expect.equal sum n "" ]

/// Freer エンジン固有の意味論。
let freerSemantics =
    testList
        "Freer"
        [ testCase "ハンドラー内の perform は外側のハンドラーへ行く"
          <| fun () ->
              let result =
                  eff {
                      let! (v: string) = Eff.perform Ask
                      return v + "!"
                  }
                  |> Eff.handle
                      [ Eff.ctl (fun (_: Ask<string>) resume ->
                            eff {
                                let! (outer: string) = Eff.perform Ask
                                return! resume (outer + "-inner")
                            }) ]
                  |> Eff.handle [ Eff.fn (fun (_: Ask<string>) -> "outer") ]
                  |> Eff.run

              Expect.equal result "outer-inner!" ""

          testCase "ハンドラーがなければ Unhandled"
          <| fun () ->
              let outcome =
                  try
                      Eff.perform (Ask: Ask<string>) |> Eff.run
                  with Unhandled(:? Ask<string>) ->
                      "unhandled"

              Expect.equal outcome "unhandled" ""

          testCase "ハンドラーを被せるまで何も実行されない"
          <| fun () ->
              let lines, count =
                  let lines = ResizeArray<string>()
                  let count = PolysemyStyle.exampleDataNotExecution lines.Add
                  (List.ofSeq lines, count)

              Expect.equal count 2 ""

              Expect.equal
                  lines
                  [ "built"; "Start"; "Ask Value"; "End"; "Start"; "Ask Value"; "End" ]
                  "組み立て時には出力されず、同じ値を 2 回実行できる"

          testCase "順序で意味が変わる: State が内側なら分岐ごとの状態"
          <| fun () ->
              Expect.equal
                  (Advanced.stateInsideNonDet ())
                  [ ((true, 0), 1); ((false, 0), 1) ]
                  ""

          testCase "順序で意味が変わる: State が外側なら共有状態"
          <| fun () -> Expect.equal (Advanced.stateOutsideNonDet ()) ([ (true, 0); (false, 1) ], 2) ""

          testCase "for はマルチショットに耐える"
          <| fun () ->
              let lines = ResizeArray<string>()

              eff {
                  for x in [ 1; 2 ] do
                      let! b = Eff.perform Advanced.Choose
                      lines.Add $"{x}{b}"
              }
              |> Advanced.runAll
              |> Eff.run
              |> ignore

              Expect.equal
                  (List.ofSeq lines)
                  [ "1True"; "2True"; "2False"; "1False"; "2True"; "2False" ]
                  ""

          testCase "let mutable は再開をまたいで共有される"
          <| fun () ->
              // 再開のたびに複製されるのは継続のクロージャだけで、捕捉された参照セルは 1 つのまま。
              let result =
                  eff {
                      let mutable n = 0
                      let! _ = Eff.perform Advanced.Choose
                      n <- n + 1
                      return n
                  }
                  |> Advanced.runAll
                  |> Eff.run

              Expect.equal result [ 1; 2 ] "分岐ごとに独立なら [1; 1] になるはず"

          testCase "引数で渡す状態は分岐ごとに独立"
          <| fun () ->
              let result =
                  eff {
                      let n = 0
                      let! _ = Eff.perform Advanced.Choose
                      return n + 1
                  }
                  |> Advanced.runAll
                  |> Eff.run

              Expect.equal result [ 1; 1 ] ""

          testCase "ジェネレーターは永続的"
          <| fun () -> Expect.equal (Advanced.persistentSequence ()) (2, 2) ""

          testCase "finally は再開ごとに走る"
          <| fun () ->
              Expect.equal
                  (capture Advanced.finallyWithMultiShot)
                  [ "use first"; "release"; "use second"; "release" ]
                  ""

          testCase "再開せず捨てると finally は走らない"
          <| fun () -> Expect.equal (capture Advanced.finallySkippedWhenAbandoned) [] ""

          testCase "Discontinue なら finally と catch が走る"
          <| fun () ->
              Expect.equal (capture Advanced.finallyWithDiscontinue) [ "release"; "caught unwind" ] ""

          let rec tick (remaining: int) (sum: int) : Eff<int> =
              eff {
                  if remaining = 0 then
                      return sum
                  else
                      let! (v: int) = Eff.perform Ask
                      return! tick (remaining - 1) (sum + v)
              }

          testCase "ctl の末尾再開はスタックを伸ばさない"
          <| fun () ->
              let n = 1_000_000

              let sum =
                  tick n 0
                  |> Eff.handle [ Eff.ctl (fun (_: Ask<int>) resume -> resume 1) ]
                  |> Eff.run

              Expect.equal sum n ""

          testCase "fn の答えで進めてもスタックを伸ばさない"
          <| fun () ->
              let n = 1_000_000
              let sum = tick n 0 |> Eff.handle [ Eff.fn (fun (_: Ask<int>) -> 1) ] |> Eff.run
              Expect.equal sum n ""

          testCase "複数のハンドラーを重ねてもスタックを伸ばさない"
          <| fun () ->
              let n = 1_000_000

              let sum =
                  tick n 0
                  |> Eff.handle [ Eff.ctl (fun (_: Emit<string>) resume -> resume ()) ]
                  |> Eff.handle [ Eff.ctl (fun (_: Get<string>) resume -> resume "") ]
                  |> Eff.handle [ Eff.ctl (fun (_: Ask<int>) resume -> resume 1) ]
                  |> Eff.run

              Expect.equal sum n ""

          testCase "fn の例外は perform の位置に届く"
          <| fun () ->
              let result =
                  eff {
                      try
                          let! (v: string) = Eff.perform Ask
                          return v
                      with e ->
                          return "caught " + e.Message
                  }
                  |> Eff.handle [ Eff.fn (fun (_: Ask<string>) -> failwith "boom") ]
                  |> Eff.run

              Expect.equal result "caught boom" ""

          testCase "同じ操作を扱う分岐は先に書いたものが選ばれる"
          <| fun () ->
              let result =
                  Eff.perform Ask
                  |> Eff.handle
                      [ Eff.fn (fun (_: Ask<string>) -> "first")
                        Eff.ctl (fun (_: Ask<string>) resume -> resume "second") ]
                  |> Eff.run

              Expect.equal result "first" ""

          let rec tailDo (remaining: int) : Eff<unit> =
              eff {
                  if remaining > 0 then
                      do! Eff.perform (Emit 1)
                      do! tailDo (remaining - 1)
              }

          /// 計算を実行して、操作 1 回あたりの割り当てを返す。
          let bytesPerOperation (n: int) =
              let before = GC.GetAllocatedBytesForCurrentThread()
              tailDo n |> Eff.handle [ Eff.fn (fun (Emit(_: int)) -> ()) ] |> Eff.run
              float (GC.GetAllocatedBytesForCurrentThread() - before) / float n

          testCase "末尾の do! の再帰はスタックを伸ばさない"
          <| fun () ->
              let mutable count = 0

              tailDo 1_000_000
              |> Eff.handle [ Eff.fn (fun (Emit(v: int)) -> count <- count + v) ]
              |> Eff.run

              Expect.equal count 1_000_000 ""

          testCase "末尾の do! の再帰は操作あたりのコストが深さによらない"
          <| fun () ->
              // ReturnFromFinal がなければ、bind が積み上がって 8 倍近くになる。
              let shallow = bytesPerOperation 1_000
              let deep = bytesPerOperation 8_000
              Expect.isLessThan deep (shallow * 1.5) $"深さ 1000 で {shallow} B、深さ 8000 で {deep} B"

          testCase "try の中の return! は末尾として扱われず、例外を捕捉する"
          <| fun () ->
              let failing: Eff<string> =
                  eff {
                      failwith "boom"
                      return ""
                  }

              let result =
                  eff {
                      try
                          return! failing
                      with e ->
                          return "caught " + e.Message
                  }
                  |> Eff.run

              Expect.equal result "caught boom" ""

          testCase "try の中の do! の後で finally が走る"
          <| fun () ->
              let lines = ResizeArray<string>()

              eff {
                  try
                      do! eff { lines.Add "body" }
                  finally
                      lines.Add "finally"
              }
              |> Eff.run

              Expect.equal (List.ofSeq lines) [ "body"; "finally" ] "" ]

let capabilitySemantics =
    testList
        "Capability"
        [ testCase "同じ型のハンドラーでも能力で指定できる"
          <| fun () -> Expect.equal (EffektStyle.exampleLexicalAddressing ()) ("outer", "inner") ""

          testCase "型で探す Freer では常に内側が選ばれる"
          <| fun () ->
              let result =
                  eff {
                      let! (a: string) = Eff.perform Ask
                      let! (b: string) = Eff.perform Ask
                      return (a, b)
                  }
                  |> KokaStyle.askHandler "inner"
                  |> KokaStyle.askHandler "outer"
                  |> Eff.run

              Expect.equal result ("inner", "inner") ""

          testCase "持ち出した能力は実行時に Unhandled"
          <| fun () ->
              let result =
                  try
                      EffektStyle.exampleEscapedCapability ()
                  with Unhandled _ ->
                      "unhandled"

              Expect.equal result "unhandled" ""

          testCase "インターフェイス制約を行にしたハンドラー"
          <| fun () -> Expect.equal (capture EffektStyle.exampleAskEmitRow) startEmitEnd ""

          testCase "末尾で 1 回再開するだけの能力は Eff.ret で答えても結果が同じ"
          <| fun () ->
              let lines =
                  capture (fun out ->
                      handlers {
                          let! ask = EffektStyle.askHandlerDirect "Ask Value"
                          let! emit = EffektStyle.emitHandler out
                          return! EffektStyle.askEmit ask emit out
                      }
                      |> Eff.run)

              Expect.equal lines startEmitEnd ""

          testCase "Eff.ret で答える能力は持ち出しても Unhandled にならない"
          <| fun () ->
              let leaked = EffektStyle.askHandlerDirect "x" (fun ask -> Eff.ret ask) |> Eff.run
              Expect.equal (leaked.Ask() |> Eff.run) "x" ""

          testCase "Rio の末尾の do! の再帰はスタックを伸ばさない"
          <| fun () ->
              let rec loop (remaining: int) : Rio<EffektStyle.IEmit<int>, unit> =
                  rio {
                      if remaining > 0 then
                          do! Rio(fun env -> env.Emit 1)
                          do! loop (remaining - 1)
                  }

              let mutable count = 0

              let env =
                  { new EffektStyle.IEmit<int> with
                      member _.Emit v =
                          count <- count + v
                          Eff.ret () }

              loop 1_000_000 |> Rio.run env |> Eff.run
              Expect.equal count 1_000_000 "" ]

let staticRow =
    testList
        "静的な行"
        [ testCase "askEmit"
          <| fun () -> Expect.equal (capture StaticRow.exampleAskEmit) [ "Emit Ask Value" ] ""

          testCase "ハンドラーの順序は入れ替えられる"
          <| fun () -> Expect.equal (capture StaticRow.exampleSwappedOrder) [ "Emit Ask Value" ] ""

          testCase "使わない側のスロットは多相のまま"
          <| fun () -> Expect.equal (StaticRow.exampleAskOnly ()) 4 "" ]

let asyncOneShot =
    testList
        "Async one-shot"
        [ testCase "24.3 と同じ順序"
          <| fun () ->
              Expect.equal
                  (capture AsyncOneShot.userLevelThreads)
                  [ "[t1] Sending 0"; "[t2] Sending 1"; "[t2] received 0"; "[t1] received 1" ]
                  ""

          testCase "discontinue で finally が走る"
          <| fun () ->
              Expect.equal
                  (capture AsyncOneShot.resumingWithAnException)
                  [ "open"; "close"; "ImproperSynchronization" ]
                  ""

          testCase "2 回の再開は FSharp.Core が拒否する"
          <| fun () -> Expect.throwsT<InvalidOperationException> AsyncOneShot.resumeTwice "" ]

/// 記事の「継続は暗黙で 1 回だけ」が API の性質であることを、同じエンコーディングで確かめる。
let runEncoding =
    testList
        "Run のエンコーディング"
        [ testCase "next を 2 回呼べる"
          <| fun () ->
              Expect.equal
                  (capture PureScriptStyle.exampleAskTwice)
                  [ "Start"; "Emit Ask Value"; "End"; "Emit Ask Value"; "End" ]
                  ""

          testCase "next を呼ばなければ打ち切られる"
          <| fun () -> Expect.equal (capture PureScriptStyle.exampleAskAbort) [ "Start" ] ""

          testCase "同じ記述を Writer へ解釈できる"
          <| fun () -> Expect.equal (PureScriptStyle.exampleToWriter ()) startEmitEnd "" ]

type private AskF<'v, 'next> = EffectSystem.Plus.RunStyle.AskF<'v, 'next>
type private LogF<'next> = EffectSystem.Plus.RunStyle.LogF<'next>

// ジェネリックな 'v で入れ子の Coproduct の右側を通る持ち上げ。下のテストで挙動を固定する。
let private nestedCoproductRight<'v>
    : Free<Coproduct<AskF<'v, unit>, Coproduct<LogF<unit>, LogF<unit>>>, unit> =
    Free.liftF (InR(InR(EffectSystem.Plus.RunStyle.Log("x", ()))))

let private nestedCoproductConcrete
    : Free<Coproduct<AskF<string, unit>, Coproduct<LogF<unit>, LogF<unit>>>, unit> =
    Free.liftF (InR(InR(EffectSystem.Plus.RunStyle.Log("x", ()))))

/// FSharpPlus を使う場合に知っておくべき挙動。版を上げて変わったらここで分かる。
let fsharpPlusBehaviour =
    testList
        "FSharpPlus 1.9.1 の挙動"
        [ testCase "monad CE の先頭区間は組み立て時に実行される"
          <| fun () ->
              let mutable hits = 0

              let (_: Free<AskF<string, string>, string>) =
                  monad {
                      hits <- hits + 1
                      let! v = Free.liftF (EffectSystem.Plus.RunStyle.Ask id)
                      return v
                  }

              Expect.equal hits 1 ""

          testCase "eff CE の先頭区間は実行まで遅延される"
          <| fun () ->
              let mutable hits = 0

              let m =
                  eff {
                      hits <- hits + 1
                      return ()
                  }

              Expect.equal hits 0 "組み立て時には動かない"
              Eff.run m
              Eff.run m
              Expect.equal hits 2 "実行のたびに動く"

          testCase "入れ子の Coproduct: 具体型なら動く"
          <| fun () -> nestedCoproductConcrete |> ignore

          testCase "入れ子の Coproduct: ジェネリックだと最適化なしで実行時に失敗する"
          <| fun () ->
#if DEBUG
              Expect.throwsT<NotSupportedException> (fun () -> nestedCoproductRight<string> |> ignore) ""
#else
              nestedCoproductRight<string> |> ignore
#endif
        ]

let private repositoryRoot =
    let rec up (d: DirectoryInfo) =
        if File.Exists(Path.Combine(d.FullName, "EffectSystem.slnx")) then
            d.FullName
        else
            up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

/// スクリプトを型検査だけして、終了コードと診断メッセージを返す。
let private typeCheck (script: string) : int * string =
    let path = Path.Combine(repositoryRoot, "tests", "compile-fail", script)

    let info =
        ProcessStartInfo("dotnet", $"fsi --nologo --quiet --exec \"{path}\"")

    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use p = Process.Start info
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    (p.ExitCode, out.Result + err.Result)

let private shouldCompile (script: string) =
    testCase $"{script} は通る"
    <| fun () ->
        let code, output = typeCheck script
        Expect.equal code 0 output

let private shouldReject (script: string) (diagnostic: string) (mentions: string) =
    testCase $"{script} は {diagnostic} で拒否される"
    <| fun () ->
        let code, output = typeCheck script
        Expect.notEqual code 0 "コンパイルエラーを期待"
        Expect.stringContains output diagnostic output
        Expect.stringContains output mentions output

/// 同じ準備で通るスクリプトと拒否されるスクリプトを並べ、失敗の理由が狙いどおりであることを確かめる。
let staticSafety =
    testSequenced
    <| testList
        "コンパイル時の検査"
        [ shouldCompile "row-all-handled.fsx"
          shouldReject "row-missing-emit.fsx" "FS0001" "Needs"
          shouldReject "row-handled-twice.fsx" "FS0001" "Needs"
          shouldCompile "rio-all-provided.fsx"
          shouldReject "rio-missing-emit.fsx" "FS0193" "IEmit"
          shouldReject "rio-two-instantiations.fsx" "FS0001" "int"
          shouldCompile "rio-distinct-interfaces.fsx" ]

[<EntryPoint>]
let main argv =
    testList
        "EffectSystem"
        [ articleOutputs
          manualOutputs
          directSemantics
          freerSemantics
          capabilitySemantics
          staticRow
          asyncOneShot
          runEncoding
          fsharpPlusBehaviour
          staticSafety ]
    |> runTestsWithCLIArgs [] argv
