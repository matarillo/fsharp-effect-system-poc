/// <summary>操作 1 回あたりの大まかなコストとスタックの限界を測る。</summary>
/// <remarks>
/// 厳密なベンチマークではなく、エンジン間の桁の違いを見るためのものである。
/// <code lang="powershell">
/// dotnet run -c Release --project samples/EffectSystem.Samples -- bench
/// dotnet run -c Release --project samples/EffectSystem.Samples -- limit direct-ctl 100000
/// </code>
/// </remarks>
/// <category index="5">計測と実行</category>
module EffectSystem.Samples.Bench

open System.Diagnostics
open System.Threading
open EffectSystem
open EffectSystem.Direct
open EffectSystem.Freer
open EffectSystem.Capability
open EffectSystem.Samples.Effects
open EffectSystem.Samples.ZioStyle

/// <summary>基準として測るインターフェイス呼び出し。</summary>
type ITick =
    /// <summary>1 を返す。</summary>
    abstract Tick: unit -> int

/// <summary>1 回捨てて JIT の安定を待ち、残り 3 回の最良値を操作あたりの時間として出力する。</summary>
let private measure (name: string) (n: int) (f: int -> int) =
    f n |> ignore

    let best =
        [ 1..3 ]
        |> List.map (fun _ ->
            let sw = Stopwatch.StartNew()
            let result = f n
            sw.Stop()

            if result <> n then
                failwithf "%s: expected %d but got %d" name n result

            sw.Elapsed.TotalNanoseconds / float n)
        |> List.min

    printfn "| %s | %d | %s |" name n (best.ToString("N1"))

/// <summary>基準。インターフェイスを n 回呼ぶ。</summary>
let private baseline (n: int) =
    let tick =
        { new ITick with
            member _.Tick() = 1 }

    let mutable sum = 0

    for _ in 1..n do
        sum <- sum + tick.Tick()

    sum

/// <summary>Direct エンジンで n 回操作する計算。</summary>
let private directBody (n: int) () =
    let mutable sum = 0

    for _ in 1..n do
        sum <- sum + perform (Ask: Ask<int>)

    sum

/// <summary>Direct エンジンの <c>fn</c> を測る。</summary>
let private directFn (n: int) =
    Deep.tryWith (directBody n) [ Deep.fn (fun (_: Ask<int>) -> 1) ]

/// <summary>Direct エンジンの <c>ctl</c> を測る。</summary>
let private directCtl (n: int) =
    Deep.tryWith (directBody n) [ Deep.ctl (fun (_: Ask<int>) k -> Deep.resume k 1) ]

/// <summary>Freer エンジンで n 回操作する計算。</summary>
let rec private freerBody (remaining: int) (sum: int) : Eff<int> =
    eff {
        if remaining = 0 then
            return sum
        else
            let! (v: int) = Eff.perform Ask
            return! freerBody (remaining - 1) (sum + v)
    }

/// <summary>Freer エンジンの <c>fn</c> を測る。</summary>
let private freerFn (n: int) =
    freerBody n 0 |> Eff.handle [ Eff.fn (fun (_: Ask<int>) -> 1) ] |> Eff.run

/// <summary>Freer エンジンの <c>ctl</c> を測る。</summary>
let private freerCtl (n: int) =
    freerBody n 0
    |> Eff.handle [ Eff.ctl (fun (_: Ask<int>) resume -> resume 1) ]
    |> Eff.run

/// <summary>末尾の <c>do!</c> で再帰する計算。</summary>
let rec private tailDo (remaining: int) : Eff<unit> =
    eff {
        if remaining > 0 then
            do! Eff.perform (Emit 1)
            do! tailDo (remaining - 1)
    }

/// <summary>末尾の <c>do!</c> の再帰を測る。ビルダーの <c>ReturnFromFinal</c> があるので、コストは深さによらない。</summary>
let private freerTailDo (n: int) =
    let mutable sum = 0

    tailDo n
    |> Eff.handle [ Eff.fn (fun (Emit(v: int)) -> sum <- sum + v) ]
    |> Eff.run

    sum

/// <summary><c>bind</c> を左にネストさせる。<c>Bound</c> の節を右へ付け替えるので、操作あたりのコストが深さに比例しない。</summary>
let rec private leftNested (depth: int) : Eff<int> =
    eff {
        if depth = 0 then
            return 0
        else
            let! rest = leftNested (depth - 1)
            let! (v: int) = Eff.perform Ask
            return rest + v
    }

/// <summary>左ネストの計算を測る。</summary>
let private freerLeftNested (n: int) =
    leftNested n |> Eff.handle [ Eff.fn (fun (_: Ask<int>) -> 1) ] |> Eff.run

/// <summary>遅延を挟まずに <c>Eff.bind</c> を左へ積む計算を測る。組み立てのときに深さの分の節ができる。</summary>
let private freerFold (n: int) =
    List.replicate n ()
    |> List.fold
        (fun acc () -> acc |> Eff.bind (fun s -> Eff.perform Ask |> Eff.map (fun (v: int) -> s + v)))
        (Eff.ret 0)
    |> Eff.handle [ Eff.fn (fun (_: Ask<int>) -> 1) ]
    |> Eff.run

/// <summary>各段に <c>try</c> を持つ左ネスト。操作が全段の <c>try</c> を通るので、操作あたりのコストが深さに比例する。</summary>
let rec private leftTry (depth: int) : Eff<int> =
    eff {
        if depth = 0 then
            return 0
        else
            try
                let! rest = leftTry (depth - 1)
                let! (v: int) = Eff.perform Ask
                return rest + v
            finally
                ()
    }

/// <summary>各段に <c>try</c> を持つ左ネストを測る。</summary>
let private freerLeftTry (n: int) =
    leftTry n |> Eff.handle [ Eff.fn (fun (_: Ask<int>) -> 1) ] |> Eff.run

/// <summary>観測するたびに、残りの計算の後ろへ <c>bind</c> を足す計算を測る。</summary>
let private freerAlternate (n: int) =
    let rec go (m: Eff<int>) =
        match Eff.view m with
        | Return x -> x
        | Eff.Op((_: Ask<int>), k) -> go (k 1 |> Eff.bind Eff.ret)
        | Operation(e, _) -> raise (Unhandled e)

    go (freerBody n 0)

/// <summary>能力渡しの計測に使う能力。</summary>
type private ICounter =
    /// <summary>1 を返す操作。</summary>
    abstract Ask: unit -> Eff<int>

/// <summary>能力を使って n 回操作する計算。</summary>
let rec private capabilityBody (c: ICounter) (remaining: int) (sum: int) : Eff<int> =
    eff {
        if remaining = 0 then
            return sum
        else
            let! v = c.Ask()
            return! capabilityBody c (remaining - 1) (sum + v)
    }

/// <summary>能力渡しを測る。</summary>
let private capability (n: int) =
    reset (fun prompt ->
        capabilityBody
            { new ICounter with
                member _.Ask() = shift prompt (fun resume -> resume 1) }
            n
            0)
    |> Eff.run

/// <summary>末尾で 1 回再開するだけの能力を、継続を取り出さずに答えて測る。</summary>
let private capabilityRet (n: int) =
    capabilityBody
        { new ICounter with
            member _.Ask() = Eff.ret 1 }
        n
        0
    |> Eff.run

/// <summary>Rio で n 回操作する計算。環境はインターフェイスで受ける。</summary>
let rec private rioBody (remaining: int) (sum: int) : Rio<EffektStyle.IAsk<int>, int> =
    rio {
        if remaining = 0 then
            return sum
        else
            let! v = EffektStyle.ask ()
            return! rioBody (remaining - 1) (sum + v)
    }

/// <summary>Rio を、継続を取り出す能力で測る。</summary>
let private rioShift (n: int) =
    reset (fun prompt ->
        rioBody n 0
        |> Rio.run
            { new EffektStyle.IAsk<int> with
                member _.Ask() = shift prompt (fun resume -> resume 1) })
    |> Eff.run

/// <summary>Rio を、継続を取り出さずに答える能力で測る。</summary>
let private rioRet (n: int) =
    rioBody n 0
    |> Rio.run
        { new EffektStyle.IAsk<int> with
            member _.Ask() = Eff.ret 1 }
    |> Eff.run

/// <summary>失敗の型 <c>'E</c> を持つ計算で n 回操作する。環境はインターフェイスで受ける。</summary>
let rec private zioBody (remaining: int) (sum: int) : Zio<EffektStyle.IAsk<int>, string, int> =
    zio {
        if remaining = 0 then
            return sum
        else
            let! v = Zio.access (fun (env: EffektStyle.IAsk<int>) -> env.Ask())
            return! zioBody (remaining - 1) (sum + v)
    }

/// <summary>失敗の型 <c>'E</c> を持つ計算を、継続を取り出さずに答える能力で測る。</summary>
let private zioRet (n: int) =
    let result =
        zioBody n 0
        |> Zio.run
            { new EffektStyle.IAsk<int> with
                member _.Ask() = Eff.ret 1 }
        |> Eff.run

    match result with
    | Ok v -> v
    | Error e -> failwith e

/// <summary>レコードの環境で n 回操作する計算。環境は匿名レコードで閉じる。</summary>
let rec private recordBody (remaining: int) (sum: int) : Rio<{| Ask: EffektStyle.IAsk<int> |}, int> =
    rio {
        if remaining = 0 then
            return sum
        else
            let! v = RecordEnv.ask ()
            return! recordBody (remaining - 1) (sum + v)
    }

/// <summary>レコードの環境を、継続を取り出す能力で測る。</summary>
let private recordShift (n: int) =
    reset (fun prompt ->
        recordBody n 0
        |> Rio.run
            {| Ask =
                { new EffektStyle.IAsk<int> with
                    member _.Ask() = shift prompt (fun resume -> resume 1) } |})
    |> Eff.run

/// <summary>レコードの環境を、継続を取り出さずに答える能力で測る。</summary>
let private recordRet (n: int) =
    recordBody n 0
    |> Rio.run
        {| Ask =
            { new EffektStyle.IAsk<int> with
                member _.Ask() = Eff.ret 1 } |}
    |> Eff.run

/// <summary><c>AsyncOneShot.capture</c> を測る。コールバックの中で同期的に再開する、最も速い経路で、トークンへの登録を含む。</summary>
let private asyncOneShot (n: int) =
    let body =
        async {
            let mutable sum = 0

            for _ in 1..n do
                let! v = AsyncOneShot.capture (fun k -> k.Resume 1)
                sum <- sum + v

            return sum
        }

    Async.RunSynchronously body

/// <summary>FSharpPlus の <c>Free</c> で n 回操作する計算。CE を使わず <c>Free.bind</c> を直接呼ぶ。</summary>
let rec private freeBody (remaining: int) (sum: int) : FSharpPlus.Data.Free<EffectSystem.Plus.RunStyle.AskF<int, int>, int> =
    if remaining = 0 then
        FSharpPlus.Data.Free.Pure sum
    else
        FSharpPlus.Data.Free.bind
            (fun v -> freeBody (remaining - 1) (sum + v))
            (FSharpPlus.Data.Free.liftF (EffectSystem.Plus.RunStyle.Ask id))

/// <summary>FSharpPlus の <c>Free</c> を測る。ハンドラーの型による検索も遅延の足場もない、1 エフェクト専用のループである。</summary>
let private fsharpPlusFree (n: int) =
    let rec loop (m: FSharpPlus.Data.Free<EffectSystem.Plus.RunStyle.AskF<int, int>, int>) =
        match FSharpPlus.Data.Free.run m with
        | Choice1Of2 a -> a
        | Choice2Of2(EffectSystem.Plus.RunStyle.Ask k) -> loop (k 1)

    loop (freeBody n 0)

/// <summary>Direct エンジンのファイバーの生成と終了を測る。スレッドを消費するので、生成自体のコストが要点になる。</summary>
let private directFiberCreation (n: int) =
    let mutable sum = 0

    for _ in 1..n do
        sum <- sum + Deep.tryWith (fun () -> 1) []

    sum

/// <summary>すべてのエンジンを測り、Markdown の表として出力する。</summary>
let run () =
    printfn "| engine | N | ns/op |"
    printfn "|---|---:|---:|"
    measure "baseline: interface call" 100_000_000 baseline
    measure "Freer + fn" 2_000_000 freerFn
    measure "Freer + ctl" 2_000_000 freerCtl
    measure "Capability (reset/shift)" 2_000_000 capability
    measure "Capability (Eff.ret)" 2_000_000 capabilityRet
    measure "Rio (reset/shift)" 2_000_000 rioShift
    measure "Rio (Eff.ret)" 2_000_000 rioRet
    measure "Zio (Eff.ret)" 2_000_000 zioRet
    measure "RecordEnv (reset/shift)" 2_000_000 recordShift
    measure "RecordEnv (Eff.ret)" 2_000_000 recordRet
    measure "FSharpPlus Free (Free.run)" 1_000_000 fsharpPlusFree
    measure "Async one-shot (capture)" 2_000_000 asyncOneShot
    measure "Direct + fn (thread handoff)" 100_000 directFn
    measure "Direct + ctl (thread handoff)" 20_000 directCtl
    measure "Direct: fiber create + finish" 5_000 directFiberCreation
    measure "Freer left-nested, depth 1000" 1_000 freerLeftNested
    measure "Freer left-nested, depth 4000" 4_000 freerLeftNested
    measure "Freer left-nested, depth 16000" 16_000 freerLeftNested
    measure "Freer left-nested, depth 64000" 64_000 freerLeftNested
    measure "Freer left-nested, depth 256000" 256_000 freerLeftNested
    measure "Freer fold, depth 4000" 4_000 freerFold
    measure "Freer try at every level, depth 250" 250 freerLeftTry
    measure "Freer try at every level, depth 1000" 1_000 freerLeftTry
    measure "Freer alternating view and bind, depth 4000" 4_000 freerAlternate
    measure "Freer alternating view and bind, depth 256000" 256_000 freerAlternate
    measure "Freer tail do!, depth 1000" 1_000 freerTailDo
    measure "Freer tail do!, depth 4000" 4_000 freerTailDo

/// <summary>比較の基準になる、通常の非末尾再帰。</summary>
let rec private plainRecursion (depth: int) : int =
    if depth = 0 then 0 else plainRecursion (depth - 1) + 1

/// <summary>1 MB のスタックで動かして、スタックの限界を調べる。</summary>
/// <remarks>溢れるとプロセスごと落ちるので、1 回の実行で 1 つの N だけ試す。</remarks>
/// <param name="engine"><c>plain-recursion</c>、<c>direct-fn</c>、<c>direct-ctl</c>、<c>freer-fn</c>、<c>freer-ctl</c>、<c>freer-left</c>、<c>freer-fold</c>、<c>freer-try</c>、<c>freer-alternate</c>、<c>freer-tail-do</c> のいずれか。</param>
/// <param name="n">操作の回数、または再帰の深さ。</param>
let limit (engine: string) (n: int) =
    let f =
        match engine with
        | "plain-recursion" -> plainRecursion
        | "direct-fn" -> directFn
        | "direct-ctl" -> directCtl
        | "freer-fn" -> freerFn
        | "freer-ctl" -> freerCtl
        | "freer-left" -> freerLeftNested
        | "freer-fold" -> freerFold
        | "freer-try" -> freerLeftTry
        | "freer-alternate" -> freerAlternate
        | "freer-tail-do" -> freerTailDo
        | other -> failwithf "unknown engine: %s" other

    let result = ref 0
    let thread = Thread((fun () -> result.Value <- f n), 1024 * 1024)
    thread.Start()
    thread.Join()
    printfn "%s %d -> %d" engine n result.Value
