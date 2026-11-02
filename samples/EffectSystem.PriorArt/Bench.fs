/// <summary>Orsak と FIO のコスト、スタックの限界、止まっている計算のメモリを、このプロジェクトの方式と同じ形で測る。</summary>
/// <remarks>
/// 厳密なベンチマークではなく、方式の間の桁の違いを見るためのものである。
/// 測り方は <c>EffectSystem.Samples.Bench</c> と同じで、比較のために <c>Rio</c> と <c>Zio</c> も同じ実行の中で測る。
/// <code lang="powershell">
/// dotnet run -c Release --project samples/EffectSystem.PriorArt -- bench
/// dotnet run -c Release --project samples/EffectSystem.PriorArt -- parked fio 100000
/// dotnet run -c Release --project samples/EffectSystem.PriorArt -- limit orsak-left 1000
/// </code>
/// </remarks>
/// <category index="3">計測と実行</category>
module EffectSystem.PriorArt.Bench

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open EffectSystem.Freer
open EffectSystem.Capability
open EffectSystem.Samples
open EffectSystem.Samples.ZioStyle

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

// ---- このプロジェクト ----

/// <summary>Rio で n 回操作する計算。</summary>
let rec private rioBody (remaining: int) (sum: int) : Rio<EffektStyle.IAsk<int>, int> =
    rio {
        if remaining = 0 then
            return sum
        else
            let! v = EffektStyle.ask ()
            return! rioBody (remaining - 1) (sum + v)
    }

/// <summary>Rio を、継続を取り出さずに答える能力で測る。</summary>
let private rioRet (n: int) =
    rioBody n 0
    |> Rio.run
        { new EffektStyle.IAsk<int> with
            member _.Ask() = Eff.ret 1 }
    |> Eff.run

/// <summary>失敗の型 <c>'E</c> を持つ計算で n 回操作する。</summary>
let rec private zioBody (remaining: int) (sum: int) : Zio<EffektStyle.IAsk<int>, string, int> =
    zio {
        if remaining = 0 then
            return sum
        else
            let! v = Zio.access (fun (env: EffektStyle.IAsk<int>) -> env.Ask())
            return! zioBody (remaining - 1) (sum + v)
    }

/// <summary><c>Zio</c> を、継続を取り出さずに答える能力で測る。</summary>
let private zioRet (n: int) =
    match
        zioBody n 0
        |> Zio.run
            { new EffektStyle.IAsk<int> with
                member _.Ask() = Eff.ret 1 }
        |> Eff.run
    with
    | Ok v -> v
    | Error e -> failwith e

/// <summary><c>bind</c> を左にネストさせた Freer の計算。</summary>
let rec private freerLeft (depth: int) : Eff<int> =
    eff {
        if depth = 0 then
            return 0
        else
            let! rest = freerLeft (depth - 1)
            let! (v: int) = Eff.perform Effects.Ask
            return rest + v
    }

/// <summary>Freer の左ネストを測る。</summary>
let private freerLeftNested (n: int) =
    freerLeft n |> Eff.handle [ Eff.fn (fun (_: Effects.Ask<int>) -> 1) ] |> Eff.run

// ---- Orsak ----
// Orsak の eff はここから先の Freer の eff を隠す。

open Orsak

/// <summary>計測に使うサービス。</summary>
type ICounter =
    /// <summary>1 を返す。</summary>
    abstract Next: unit -> int

/// <summary>計測に使う、<c>Task</c> を返すサービス。</summary>
type ICounterTask =
    /// <summary>完了した <c>Task</c> で 1 を返す。</summary>
    abstract Next: unit -> Task<int>

/// <summary><c>ICounter</c> の提供者。</summary>
type ICounterProvider =
    /// <summary>サービス。</summary>
    abstract Counter: ICounter

/// <summary><c>ICounterTask</c> の提供者。</summary>
type ICounterTaskProvider =
    /// <summary>サービス。</summary>
    abstract CounterTask: ICounterTask

/// <summary>計測に使う環境。</summary>
type CounterEnv() =
    let counter =
        { new ICounter with
            member _.Next() = 1 }

    let counterTask =
        { new ICounterTask with
            member _.Next() = Task.FromResult 1 }

    interface ICounterProvider with
        member _.Counter = counter

    interface ICounterTaskProvider with
        member _.CounterTask = counterTask

/// <summary>環境のサービスを呼ぶ計算。</summary>
let private next () =
    Effect.Create(fun (p: #ICounterProvider) -> p.Counter.Next())

/// <summary>環境の、<c>Task</c> を返すサービスを呼ぶ計算。</summary>
let private nextTask () =
    Effect.Create(fun (p: #ICounterTaskProvider) -> p.CounterTask.Next())

/// <summary>Orsak の計算を同期に実行し、成功の値を取り出す。</summary>
let private runOrsak (env: 'r) (e: Effect<'r, int, string>) =
    match (Effect.run env e).AsTask().Result with
    | Ok v -> v
    | Error e -> failwith e

/// <summary>末尾の <c>return!</c> で n 回操作する計算。Orsak はスレッドごとのトランポリンで、50 段ごとにスタックを戻す。</summary>
let rec private orsakTailBody (remaining: int) (sum: int) : Effect<CounterEnv, int, string> =
    eff {
        if remaining = 0 then
            return sum
        else
            let! v = next ()
            return! orsakTailBody (remaining - 1) (sum + v)
    }

/// <summary><c>while</c> で n 回操作する計算。1 つのステートマシンの中で回る。</summary>
let private orsakWhileBody (n: int) : Effect<CounterEnv, int, string> =
    eff {
        let mutable sum = 0
        let mutable i = 0

        while i < n do
            let! v = next ()
            sum <- sum + v
            i <- i + 1

        return sum
    }

/// <summary><c>while</c> で、<c>Task</c> を返すサービスを n 回呼ぶ計算。</summary>
let private orsakWhileTaskBody (n: int) : Effect<CounterEnv, int, string> =
    eff {
        let mutable sum = 0
        let mutable i = 0

        while i < n do
            let! v = nextTask ()
            sum <- sum + v
            i <- i + 1

        return sum
    }

/// <summary><c>bind</c> を左にネストさせた Orsak の計算。完了したタスクは同じスタックの上で続きを動かすので、深さの分だけスタックが伸びる。</summary>
let rec private orsakLeft (depth: int) : Effect<'r, int, string> =
    eff {
        if depth = 0 then
            return 0
        else
            let! rest = orsakLeft (depth - 1)
            let! v = Effect.ret 1
            return rest + v
    }

/// <summary>遅延を挟まずに <c>Effect.bind</c> を左へ積む計算。</summary>
let private orsakFold (n: int) : Effect<'r, int, string> =
    List.replicate n ()
    |> List.fold (fun acc () -> acc |> Effect.bind (fun s -> Effect.ret 1 |> Effect.map (fun v -> s + v))) (Effect.ret 0)

/// <summary>比較の基準になる、標準の <c>task { }</c> の左ネスト。</summary>
let rec private taskLeft (depth: int) : Task<int> =
    task {
        if depth = 0 then
            return 0
        else
            let! rest = taskLeft (depth - 1)
            let! v = Task.FromResult 1
            return rest + v
    }

// ---- FIO ----

open FIO.DSL
open FIO.Runtime
open FIO.Runtime.Direct
open FIO.Runtime.WorkStealing

/// <summary>FIO の計算を新しいファイバーで動かし、成功の値を取り出す。</summary>
let private runFio (runtime: FIORuntime) (e: FIO<int, exn>) =
    match (runtime.Run e).UnsafeResult() with
    | Succeeded v -> v
    | result -> failwithf "%A" result

/// <summary>計測に使う能力。</summary>
let private counter =
    { new ICounter with
        member _.Next() = 1 }

/// <summary>末尾の <c>return!</c> で n 回、能力を呼ぶ計算。</summary>
let rec private fioTailBody (remaining: int) (sum: int) : FIO<int, exn> =
    fio {
        if remaining = 0 then
            return sum
        else
            let! v = FIO.succeedWith counter.Next
            return! fioTailBody (remaining - 1) (sum + v)
    }

/// <summary><c>bind</c> を左にネストさせた FIO の計算。</summary>
let rec private fioLeft (depth: int) : FIO<int, exn> =
    fio {
        if depth = 0 then
            return 0
        else
            let! rest = fioLeft (depth - 1)
            let! v = FIO.succeedWith counter.Next
            return rest + v
    }

/// <summary>遅延を挟まずに <c>FlatMap</c> を左へ積む計算。</summary>
let private fioFold (n: int) : FIO<int, exn> =
    List.replicate n ()
    |> List.fold
        (fun (acc: FIO<int, exn>) () -> acc.FlatMap(fun s -> (FIO.succeedWith counter.Next).Map(fun v -> s + v)))
        (FIO.succeed 0)

/// <summary>コールバックを同期に呼んで再開する計算を n 回。<c>FIO.async</c> は <c>TaskCompletionSource</c> を介して待つ。</summary>
let private fioAsyncCallback (runtime: FIORuntime) (n: int) =
    let rec go remaining sum : FIO<int, exn> =
        fio {
            if remaining = 0 then
                return sum
            else
                let! v = FIO.async (fun k -> k (Ok 1)) id
                return! go (remaining - 1) (sum + v)
        }

    runFio runtime (go n 0)

/// <summary>他のファイバーへ譲る計算を n 回。FIO 0.5.0 の <c>yieldNow</c> は <c>Task.Run</c> を待つ。</summary>
let private fioYield (runtime: FIORuntime) (n: int) =
    let rec go remaining sum : FIO<int, exn> =
        fio {
            if remaining = 0 then
                return sum
            else
                do! FIO.yieldNow ()
                return! go (remaining - 1) (sum + 1)
        }

    runFio runtime (go n 0)

/// <summary>子のファイバーを作って結果を待つ計算を n 回。</summary>
let private fioForkJoin (runtime: FIORuntime) (n: int) =
    let rec go remaining sum : FIO<int, exn> =
        fio {
            if remaining = 0 then
                return sum
            else
                let! fiber = (FIO.succeed 1: FIO<int, exn>).Fork()
                let! v = fiber.Join()
                return! go (remaining - 1) (sum + v)
        }

    runFio runtime (go n 0)

/// <summary>比較の基準。<c>Async.FromContinuations</c> のコールバックを同期に呼んで再開する。</summary>
let private asyncFromContinuations (n: int) =
    async {
        let mutable sum = 0

        for _ in 1..n do
            let! v = Async.FromContinuations(fun (ok, _, _) -> ok 1)
            sum <- sum + v

        return sum
    }
    |> Async.RunSynchronously

/// <summary>比較の基準。<c>Async.StartChild</c> で子を始めて待つ。</summary>
let private asyncStartChild (n: int) =
    async {
        let mutable sum = 0

        for _ in 1..n do
            let! wait = Async.StartChild(async { return 1 })
            let! v = wait
            sum <- sum + v

        return sum
    }
    |> Async.RunSynchronously

/// <summary>すべてを測り、Markdown の表として出力する。</summary>
/// <remarks>WorkStealingRuntime のワーカーが他の行の計測に混ざらないように、そのランタイムは最後にまとめて作る。</remarks>
let run () =
    printfn "| engine | N | ns/op |"
    printfn "|---|---:|---:|"
    measure "Rio (Eff.ret)" 2_000_000 rioRet
    measure "Zio (Eff.ret)" 2_000_000 zioRet
    measure "Orsak: service call, tail return!" 2_000_000 (fun n -> runOrsak (CounterEnv()) (orsakTailBody n 0))
    measure "Orsak: service call, while" 2_000_000 (fun n -> runOrsak (CounterEnv()) (orsakWhileBody n))
    measure "Orsak: service returns Task, while" 2_000_000 (fun n -> runOrsak (CounterEnv()) (orsakWhileTaskBody n))
    measure "Async.FromContinuations (sync)" 1_000_000 asyncFromContinuations
    measure "Async.StartChild + await" 200_000 asyncStartChild
    measure "Freer left-nested, depth 4000" 4_000 freerLeftNested
    measure "Freer left-nested, depth 256000" 256_000 freerLeftNested
    measure "Orsak left-nested, depth 500" 500 (fun n -> runOrsak () (orsakLeft n))

    do
        use direct = new DirectRuntime()
        measure "FIO Direct: succeedWith, tail return!" 2_000_000 (fun n -> runFio direct (fioTailBody n 0))
        measure "FIO Direct: async callback (sync)" 1_000_000 (fioAsyncCallback direct)
        measure "FIO Direct: yieldNow" 1_000_000 (fioYield direct)
        measure "FIO Direct: fork + join" 200_000 (fioForkJoin direct)
        measure "FIO Direct left-nested, depth 4000" 4_000 (fun n -> runFio direct (fioLeft n))
        measure "FIO Direct left-nested, depth 256000" 256_000 (fun n -> runFio direct (fioLeft n))
        measure "FIO Direct fold, depth 256000" 256_000 (fun n -> runFio direct (fioFold n))

    do
        use stealing = new WorkStealingRuntime()
        measure "FIO WorkStealing: succeedWith, tail return!" 2_000_000 (fun n -> runFio stealing (fioTailBody n 0))
        measure "FIO WorkStealing: async callback (sync)" 1_000_000 (fioAsyncCallback stealing)
        measure "FIO WorkStealing: yieldNow" 1_000_000 (fioYield stealing)
        measure "FIO WorkStealing: fork + join" 200_000 (fioForkJoin stealing)
        measure "FIO WorkStealing left-nested, depth 4000" 4_000 (fun n -> runFio stealing (fioLeft n))
        measure "FIO WorkStealing left-nested, depth 256000" 256_000 (fun n -> runFio stealing (fioLeft n))

/// <summary>GC を済ませてから、使用中のメモリを測る。</summary>
let private liveBytes () =
    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()
    GC.GetTotalMemory true

/// <summary>止まっている FIO のファイバーのメモリと、作成と中断の時間を測る。</summary>
/// <param name="count">ファイバーの数。</param>
/// <param name="shared">真なら全員で 1 本のチャンネルを待つ。偽なら <c>FIO.never</c> で待つ（呼ぶたびにチャンネルを作る）。</param>
let private parkedFio (count: int) (shared: bool) =
    let runtimes: (string * (unit -> FIORuntime)) list =
        [ "Direct", (fun () -> new DirectRuntime()); "WorkStealing", (fun () -> new WorkStealingRuntime()) ]

    for name, create in runtimes do
        use runtime = create ()
        use started = new CountdownEvent(count)
        use release = new ManualResetEventSlim(false)
        let channel = Channel<unit>()

        let child: FIO<unit, exn> =
            (FIO.succeedWith (fun () -> started.Signal() |> ignore))
                .FlatMap(fun () -> if shared then channel.Read() else FIO.never ())

        let parent: FIO<int, exn> =
            fio {
                let fibers = ResizeArray()

                for _ in 1..count do
                    let! fiber = child.Fork()
                    fibers.Add fiber

                do! FIO.succeedWith (fun () -> release.Wait())

                for fiber in fibers do
                    do! fiber.InterruptNow()

                return fibers.Count
            }

        let before = liveBytes ()
        let sw = Stopwatch.StartNew()
        let root = runtime.Run parent
        started.Wait()
        let forked = sw.ElapsedMilliseconds
        let after = liveBytes ()
        sw.Restart()
        release.Set()
        let result = root.UnsafeResult()

        printfn
            "FIO %s (shared channel: %b): %d fibers, %.0f bytes each, fork and start %d ms, interrupt and unwind %d ms (%A)"
            name
            shared
            count
            (float (after - before) / float count)
            forked
            sw.ElapsedMilliseconds
            result

/// <summary>完了していないタスクを待つ Orsak の計算のメモリを測る。</summary>
/// <param name="count">計算の数。</param>
let private parkedOrsak (count: int) =
    let source = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    let waitOne: Effect<unit, int, string> =
        eff {
            let! v = source.Task
            return v
        }

    let before = liveBytes ()
    let tasks = Array.init count (fun _ -> (Effect.run () waitOne).AsTask())
    let after = liveBytes ()
    let sw = Stopwatch.StartNew()
    source.SetResult 1
    Task.WaitAll(tasks |> Array.map (fun t -> t :> Task))

    printfn
        "Orsak: %d effects, %.0f bytes each, release %d ms"
        count
        (float (after - before) / float count)
        sw.ElapsedMilliseconds

/// <summary>止まっている計算のメモリを測る。</summary>
/// <remarks>FIO のランタイムの後片付けと重ならないように、Orsak は別の実行で測る。</remarks>
/// <param name="library"><c>fio</c> または <c>orsak</c>。</param>
/// <param name="count">計算の数。</param>
let parked (library: string) (count: int) =
    match library with
    | "fio" ->
        parkedFio count false
        parkedFio count true
    | "orsak" -> parkedOrsak count
    | other -> failwithf "unknown library: %s" other

/// <summary>1 MB のスタックで動かして、スタックの限界を調べる。</summary>
/// <remarks>
/// 溢れるとプロセスごと落ちるので、1 回の実行で 1 つの N だけ試す。
/// FIO の計算はランタイムのスレッドで動くので、1 MB のスタックの条件は当てはまらない。継続をヒープのスタックに積むので、深さによらず完走するかを見る。
/// </remarks>
/// <param name="engine"><c>orsak-tail</c>、<c>orsak-while</c>、<c>orsak-left</c>、<c>orsak-fold</c>、<c>task-left</c>、<c>fio-tail</c>、<c>fio-left</c>、<c>fio-fold</c> のいずれか。</param>
/// <param name="n">操作の回数、または再帰の深さ。</param>
let limit (engine: string) (n: int) =
    let onDirect (e: FIO<int, exn>) =
        use runtime = new DirectRuntime()
        runFio runtime e

    let f: int -> int =
        match engine with
        | "orsak-tail" -> fun n -> runOrsak (CounterEnv()) (orsakTailBody n 0)
        | "orsak-while" -> fun n -> runOrsak (CounterEnv()) (orsakWhileBody n)
        | "orsak-left" -> fun n -> runOrsak () (orsakLeft n)
        | "orsak-fold" -> fun n -> runOrsak () (orsakFold n)
        | "task-left" -> fun n -> (taskLeft n).Result
        | "fio-tail" -> fun n -> onDirect (fioTailBody n 0)
        | "fio-left" -> fun n -> onDirect (fioLeft n)
        | "fio-fold" -> fun n -> onDirect (fioFold n)
        | other -> failwithf "unknown engine: %s" other

    let result = ref 0
    let thread = Thread((fun () -> result.Value <- f n), 1024 * 1024)
    thread.Start()
    thread.Join()
    printfn "%s %d -> %d" engine n result.Value
