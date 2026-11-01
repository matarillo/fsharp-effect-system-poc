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

/// <summary><c>bind</c> を左にネストさせる。Freer の既知の弱点で、全体のコストが深さの 2 乗に近づく。</summary>
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

/// <summary><c>Async.FromContinuations</c> を測る。コールバックの中で同期的に再開する、最も速い経路である。</summary>
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
    measure "FSharpPlus Free (Free.run)" 1_000_000 fsharpPlusFree
    measure "Async.FromContinuations" 2_000_000 asyncOneShot
    measure "Direct + fn (thread handoff)" 100_000 directFn
    measure "Direct + ctl (thread handoff)" 20_000 directCtl
    measure "Direct: fiber create + finish" 5_000 directFiberCreation
    measure "Freer left-nested, depth 1000" 1_000 freerLeftNested
    measure "Freer left-nested, depth 4000" 4_000 freerLeftNested
    measure "Freer tail do!, depth 1000" 1_000 freerTailDo
    measure "Freer tail do!, depth 4000" 4_000 freerTailDo

/// <summary>比較の基準になる、通常の非末尾再帰。</summary>
let rec private plainRecursion (depth: int) : int =
    if depth = 0 then 0 else plainRecursion (depth - 1) + 1

/// <summary>1 MB のスタックで動かして、スタックの限界を調べる。</summary>
/// <remarks>溢れるとプロセスごと落ちるので、1 回の実行で 1 つの N だけ試す。</remarks>
/// <param name="engine"><c>plain-recursion</c>、<c>direct-fn</c>、<c>direct-ctl</c>、<c>freer-fn</c>、<c>freer-ctl</c>、<c>freer-left</c>、<c>freer-tail-do</c> のいずれか。</param>
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
        | "freer-tail-do" -> freerTailDo
        | other -> failwithf "unknown engine: %s" other

    let result = ref 0
    let thread = Thread((fun () -> result.Value <- f n), 1024 * 1024)
    thread.Start()
    thread.Join()
    printfn "%s %d -> %d" engine n result.Value
