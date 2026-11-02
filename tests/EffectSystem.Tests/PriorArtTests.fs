/// Orsak と FIO のサンプルの出力と、ライブラリの挙動。
/// Orsak と FIO はどちらも eff や Ref などの名前を持ち込むので、Tests.fs とファイルを分けている。
module EffectSystem.Tests.PriorArtTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open EffectSystem.PriorArt

/// サンプルの出力を行のリストとして受け取る。
let private capture (f: (string -> unit) -> unit) : string list =
    let lines = ResizeArray<string>()
    f lines.Add
    List.ofSeq lines

let orsakStyle =
    testList
        "Orsak の写し"
        [ testCase "askEmit"
          <| fun () -> Expect.equal (capture OrsakStyle.exampleAskEmit) [ "Start"; "Emit Ask Value"; "End" ] ""

          testCase "トランザクション: commit"
          <| fun () -> Expect.equal (capture OrsakStyle.transactionCommits) [ "begin"; "work"; "commit" ] ""

          testCase "トランザクション: 失敗なら rollback"
          <| fun () ->
              Expect.equal
                  (capture OrsakStyle.transactionFails)
                  [ "begin"; "work"; "rollback"; "failed: work failed" ]
                  ""

          testCase "and! は両方の失敗を + で合わせる"
          <| fun () ->
              let check sku quantity =
                  OrsakStyle.checkLine sku quantity |> OrsakStyle.run ()

              Expect.equal
                  (check "" 0)
                  (Error(OrsakStyle.Problems [ "sku is empty"; "quantity must be positive" ]))
                  "両方"

              Expect.equal (check "" 2) (Error(OrsakStyle.Problems [ "sku is empty" ])) "片方"
              Expect.equal (check "mug" 2) (Ok("mug", 2)) "成功" ]

let fioStyle =
    testList
        "FIO の写し"
        [ testCase "askEmit"
          <| fun () -> Expect.equal (capture FioStyle.exampleAskEmit) [ "Start"; "Emit Ask Value"; "End" ] ""

          testCase "トランザクション: commit"
          <| fun () -> Expect.equal (capture FioStyle.transactionCommits) [ "begin"; "work"; "commit" ] ""

          testCase "トランザクション: 失敗なら rollback"
          <| fun () ->
              Expect.equal
                  (capture FioStyle.transactionFails)
                  [ "begin"; "work"; "rollback"; "failed: work failed" ]
                  ""

          testCase "トランザクション: 中断でも rollback"
          <| fun () -> Expect.equal (capture FioStyle.transactionInterrupted) [ "begin"; "work"; "rollback" ] ""

          testCase "親が終わると子は中断され、親の結果は子の後始末の後に返る"
          <| fun () ->
              Expect.equal
                  (capture FioStyle.exampleChildInterrupted)
                  [ "child started"; "parent done"; "child released" ]
                  ""

          testCase "中断不可の領域で届いた中断は、領域の終わりに効く"
          <| fun () -> Expect.equal (capture FioStyle.exampleUninterruptible) [ "region start"; "region end" ] "" ]

// ---- Orsak ----

open Orsak

let private runOrsak (e: Effect<unit, 'a, 'e>) : Result<'a, 'e> = OrsakStyle.run () e

/// Orsak を使う場合に知っておくべき挙動。版を上げて変わったらここで分かる。
let orsakBehaviour =
    testList
        "Orsak 0.7.0 の挙動"
        [ testCase "組み立てただけでは動かず、同じ値を実行のたびに動かす"
          <| fun () ->
              let mutable hits = 0

              let m: Effect<unit, int, string> =
                  eff {
                      hits <- hits + 1
                      return hits
                  }

              Expect.equal hits 0 "組み立て時には動かない"
              Expect.equal (runOrsak m) (Ok 1) "1 回目"
              Expect.equal (runOrsak m) (Ok 2) "2 回目"

          testCase "let! は最初の失敗で止まる"
          <| fun () ->
              let started = ResizeArray<string>()

              let check (name: string) : Effect<unit, int, OrsakStyle.Problems> =
                  eff {
                      started.Add name
                      return! Error(OrsakStyle.Problems [ name ])
                  }

              let both =
                  eff {
                      let! a = check "A"
                      let! b = check "B"
                      return a + b
                  }

              Expect.equal (runOrsak both) (Error(OrsakStyle.Problems [ "A" ])) ""
              Expect.equal (List.ofSeq started) [ "A" ] "B は始まらない"

          testCase "race は負けた側を止めない"
          <| fun () ->
              let gate = TaskCompletionSource()
              use finished = new ManualResetEventSlim(false)

              let loser: Effect<unit, string, string> =
                  eff {
                      try
                          do! gate.Task
                          return "loser"
                      finally
                          finished.Set()
                  }

              Expect.equal (runOrsak (Effect.race (Effect.ret "winner") loser)) (Ok "winner") ""
              Expect.isFalse finished.IsSet "race が返った時点で負けた側は止まっていない"
              gate.SetResult()
              Expect.isTrue (finished.Wait 5000) "負けた側は最後まで走る"

          testCase "timeout は計算を止めない"
          <| fun () ->
              let gate = TaskCompletionSource()
              use finished = new ManualResetEventSlim(false)

              let slow: Effect<unit, string, string> =
                  eff {
                      try
                          do! gate.Task
                          return "slow"
                      finally
                          finished.Set()
                  }

              Expect.equal (runOrsak (Effect.timeout (TimeSpan.FromMilliseconds 20.0) "timeout" slow)) (Error "timeout") ""
              Expect.isFalse finished.IsSet "時間切れの時点で計算は止まっていない"
              gate.SetResult()
              Expect.isTrue (finished.Wait 5000) "計算は最後まで走る"

          testCase "失敗は値として戻るので finally と use を通る"
          <| fun () ->
              let log = ResizeArray<string>()

              let m: Effect<unit, unit, string> =
                  eff {
                      use _ =
                          { new IDisposable with
                              member _.Dispose() = log.Add "dispose" }

                      try
                          return! Error "boom"
                      finally
                          log.Add "finally"
                  }

              Expect.equal (runOrsak m) (Error "boom") ""
              Expect.equal (List.ofSeq log) [ "finally"; "dispose" ] ""

          testCase "例外は失敗にならず、タスクの例外になる"
          <| fun () ->
              let m: Effect<unit, unit, string> = eff { failwith "thrown" }
              let e = Expect.throwsC (fun () -> runOrsak m |> ignore) id
              Expect.equal e.Message "thrown" ""

          testCase "finally の例外は本体の例外を消す"
          <| fun () ->
              let m: Effect<unit, unit, string> =
                  eff {
                      try
                          failwith "body"
                      finally
                          failwith "finalizer"
                  }

              let e = Expect.throwsC (fun () -> runOrsak m |> ignore) id
              Expect.equal e.Message "finalizer" "後始末の例外だけが残る"
              Expect.isNull e.InnerException "本体の例外は辿れない"

          testCase "環境のトークンを渡した待機は、トークンの取り消しで終わり、finally が走る"
          <| fun () ->
              use source = new CancellationTokenSource()
              use started = new ManualResetEventSlim(false)
              let log = ResizeArray<string>()

              let env =
                  { new ICancellationProvider with
                      member _.Source = source }

              let m =
                  eff {
                      let! token = CancellationSource.getToken ()

                      try
                          started.Set()
                          do! Task.Delay(Timeout.Infinite, token)
                          return "not reached"
                      finally
                          log.Add "finally"
                  }

              let running = Task.Run(fun () -> OrsakStyle.run env m)
              Expect.isTrue (started.Wait 5000) "待機が始まる"
              source.Cancel()
              Expect.throwsT<TaskCanceledException> (fun () -> running.GetAwaiter().GetResult() |> ignore) "失敗ではなく例外になる"
              Expect.equal (List.ofSeq log) [ "finally" ] ""

          testCase "par と whenAll は同時の失敗を 1 つしか残さない"
          <| fun () ->
              let failing (name: string) : Effect<unit, int, string> = eff { return! Error name }
              Expect.equal (runOrsak (Effect.par [ failing "A"; failing "B" ])) (Error "A") "par"
              Expect.equal (runOrsak (Effect.whenAll [ failing "A"; failing "B" ])) (Error "A") "whenAll" ]

// ---- FIO ----

open FIO.DSL
open FIO.Runtime.Direct

/// 欠陥で中断された結果なら、その例外のメッセージを返す。
let private defectMessage (result: FiberResult<'a, 'e>) : string option =
    match result with
    | Interrupted ex ->
        match ex.cause with
        | Defect e -> Some e.Message
        | _ -> None
    | _ -> None

/// FIO を使う場合に知っておくべき挙動。版を上げて変わったらここで分かる。
let fioBehaviour =
    testList
        "FIO 0.5.0-beta の挙動"
        [ testCase "同じ値を実行のたびに動かす"
          <| fun () ->
              let mutable hits = 0

              let m: FIO<int, string> =
                  fio {
                      hits <- hits + 1
                      return hits
                  }

              Expect.equal hits 0 "組み立て時には動かない"
              Expect.equal (FioStyle.run m) (Succeeded 1) "1 回目"
              Expect.equal (FioStyle.run m) (Succeeded 2) "2 回目"

          testCase "ForkDaemon の子は親が終わっても走り続ける"
          <| fun () ->
              use runtime = new DirectRuntime()
              let gate = TaskCompletionSource<unit>()
              use finished = new ManualResetEventSlim(false)

              let child: FIO<unit, exn> =
                  (FIO.awaitTask gate.Task id).FlatMap(fun () -> FIO.succeedWith (fun () -> finished.Set()))

              let parent: FIO<unit, exn> =
                  fio {
                      let! _ = child.ForkDaemon()
                      return ()
                  }

              Expect.equal ((runtime.Run parent).UnsafeResult()) (Succeeded()) ""
              Expect.isFalse finished.IsSet "親の終了時点では子は終わっていない"
              gate.SetResult()
              Expect.isTrue (finished.Wait 5000) "親の終了後に子が終わる"

          testCase "親を中断すると子も中断される"
          <| fun () ->
              use runtime = new DirectRuntime()
              use started = new ManualResetEventSlim(false)
              use released = new ManualResetEventSlim(false)

              let child: FIO<unit, exn> =
                  (FIO.succeedWith (fun () -> started.Set()))
                      .FlatMap(fun () -> FIO.never ())
                      .Ensuring(FIO.succeedWith (fun () -> released.Set()))

              let parent: FIO<unit, exn> =
                  fio {
                      let! fiber = child.Fork()
                      return! fiber.Join()
                  }

              let root = runtime.Run parent
              Expect.isTrue (started.Wait 5000) "子が動き出す"
              (runtime.Run(root.InterruptNow(): FIO<unit, exn>)).UnsafeResult() |> ignore

              match root.UnsafeResult() with
              | Interrupted _ -> ()
              | r -> failtest $"中断を期待: %A{r}"

              Expect.isTrue (released.Wait 5000) "子の後始末が走る"

          testCase "中断したファイバーの結果は、その後始末より先に返る"
          <| fun () ->
              let started = Channel<unit>()
              let gate = TaskCompletionSource<unit>()
              use released = new ManualResetEventSlim(false)

              let child: FIO<unit, exn> =
                  (fio {
                      do! started.Write(())
                      return! FIO.never ()
                  })
                      .Ensuring((FIO.awaitTask gate.Task id).FlatMap(fun () -> FIO.succeedWith (fun () -> released.Set())))

              let parent: FIO<bool * bool, exn> =
                  fio {
                      let! fiber = child.Fork()
                      do! started.Read()
                      let! result = fiber.InterruptAwaitNow()
                      let releasedAtResult = released.IsSet
                      gate.SetResult()

                      let interrupted =
                          match result with
                          | Interrupted _ -> true
                          | _ -> false

                      return interrupted, releasedAtResult
                  }

              Expect.equal (FioStyle.run parent) (Succeeded(true, false)) "後始末の前に Interrupted が返る"
              Expect.isTrue released.IsSet "親は子の後始末を待ってから終わる"

          testCase "Timeout は計算を中断する"
          <| fun () ->
              use released = new ManualResetEventSlim(false)

              let slow: FIO<unit, exn> =
                  (FIO.never ()).Ensuring(FIO.succeedWith (fun () -> released.Set()))

              Expect.equal (FioStyle.run (slow.Timeout(TimeSpan.FromMilliseconds 20.0))) (Succeeded None) ""
              Expect.isTrue released.IsSet "後始末が走っている"

          testCase "Race の負けた側は中断される"
          <| fun () ->
              use released = new ManualResetEventSlim(false)

              let loser: FIO<string, exn> =
                  (FIO.never ()).Ensuring(FIO.succeedWith (fun () -> released.Set()))

              Expect.equal (FioStyle.run ((FIO.succeed "winner").Race loser)) (Succeeded "winner") ""
              Expect.isTrue released.IsSet "後始末が走っている"

          testCase "ZipPar は同時の失敗を 1 つしか残さない。ZipParError は両方を残す"
          <| fun () ->
              let failing (both: CountdownEvent) (name: string) : FIO<int, string> =
                  fio {
                      do!
                          FIO.succeedWith (fun () ->
                              both.Signal() |> ignore
                              both.Wait 5000 |> ignore)

                      return! FIO.fail name
                  }

              use both = new CountdownEvent(2)

              match FioStyle.run ((failing both "A").ZipPar(failing both "B")) with
              | Failed e -> Expect.contains [ "A"; "B" ] e "片方だけが残る"
              | r -> failtest $"失敗を期待: %A{r}"

              use both2 = new CountdownEvent(2)

              Expect.equal
                  (FioStyle.run ((failing both2 "A").ZipParError(failing both2 "B")))
                  (Failed("A", "B"))
                  "両方が残る"

          testCase "本体と後始末が失敗すると、本体の失敗が残る"
          <| fun () ->
              let m: FIO<unit, string> = (FIO.fail "body").Ensuring(FIO.fail "finalizer")
              Expect.equal (FioStyle.run m) (Failed "body") ""

          testCase "本体が成功して後始末が失敗すると、後始末の失敗になる"
          <| fun () ->
              let m: FIO<int, string> = (FIO.succeed 1).Ensuring(FIO.fail "finalizer")
              Expect.equal (FioStyle.run m) (Failed "finalizer") ""

          testCase "後始末の例外は欠陥になり、本体の失敗を消す"
          <| fun () ->
              let m: FIO<unit, string> =
                  (FIO.fail "body").Ensuring(FIO.succeedWith (fun () -> failwith "finalizer"))

              Expect.equal (defectMessage (FioStyle.run m)) (Some "finalizer") ""

          testCase "FIO.async のコールバックの 2 回目は無視される"
          <| fun () ->
              let m: FIO<int, string> =
                  FIO.async
                      (fun k ->
                          k (Ok 1)
                          k (Ok 2))
                      (fun e -> e.Message)

              Expect.equal (FioStyle.run m) (Succeeded 1) ""

          testCase "計算の中の例外は欠陥になり、try/with では捕まらない"
          <| fun () ->
              let m: FIO<string, string> =
                  fio {
                      try
                          let! (x: int) = FIO.succeed 1

                          if x = 1 then
                              failwith "thrown"

                          return "not reached"
                      with e ->
                          return "caught " + e
                  }

              Expect.equal (defectMessage (FioStyle.run m)) (Some "thrown") ""

          testCase "try/finally の後始末は FIO で書き、型付きの失敗でも走る"
          <| fun () ->
              let log = ResizeArray<string>()

              let m: FIO<unit, string> =
                  fio {
                      try
                          return! FIO.fail "boom"
                      finally
                          FIO.succeedWith (fun () -> log.Add "finally")
                  }

              Expect.equal (FioStyle.run m) (Failed "boom") ""
              Expect.equal (List.ofSeq log) [ "finally" ] ""

          testCase "try/with は型付きの失敗を捕まえる"
          <| fun () ->
              let m: FIO<string, string> =
                  fio {
                      try
                          return! FIO.fail "typed"
                      with e ->
                          return "caught " + e
                  }

              Expect.equal (FioStyle.run m) (Succeeded "caught typed") "" ]
