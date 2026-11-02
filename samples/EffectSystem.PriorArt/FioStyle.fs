/// <summary>FIO で、記事の例、途中で 1 回の例、ZIO の検証の中断の例を書く。</summary>
/// <remarks>
/// <para>
/// FIO の <c>FIO&lt;'A, 'E&gt;</c> は閉じた命令の木で、ランタイムがファイバーごとの可変のスタックに継続を積みながら解釈する。
/// ZIO の実行ループと同じ構造で、ZIO の検証の試作（単一スレッド）を、スレッドプールの上のマルチスレッドにしたものに当たる。
/// 利用者が足せる命令もハンドラーもないので、利用者は操作の意味を定義できない。
/// </para>
/// <para>
/// 環境の型引数はない。能力は普通の引数で渡すので、<c>EffektStyle.askHandlerDirect</c> と同じ能力渡しになる。
/// </para>
/// </remarks>
/// <category index="2">FIO</category>
module EffectSystem.PriorArt.FioStyle

open System
open FIO.DSL
open FIO.Runtime.Direct
open EffectSystem.Samples.Effects

/// <summary>計算を新しいファイバーで動かし、結果を待つ。</summary>
/// <remarks>
/// ランタイムは <c>DirectRuntime</c> を使う。ファイバーをスレッドプールのタスクとして動かす、FIO の基準のランタイムである。
/// 結果はファイバーとその子がすべて後始末を終えてから返る。
/// </remarks>
/// <param name="effect">動かす計算。</param>
let run (effect: FIO<'a, 'e>) : FiberResult<'a, 'e> =
    use runtime = new DirectRuntime()
    (runtime.Run effect).UnsafeResult()

// ---- 記事の例 ----

/// <summary>値を取り出す能力。メソッドが <c>FIO</c> を返す。</summary>
/// <typeparam name="'a">取り出す値の型。</typeparam>
type IAsk<'a> =
    /// <summary>値を要求する。</summary>
    abstract Ask: unit -> FIO<'a, exn>

/// <summary>値を発する能力。</summary>
/// <typeparam name="'a">発する値の型。</typeparam>
type IEmit<'a> =
    /// <summary>値を発する。</summary>
    abstract Emit: 'a -> FIO<unit, exn>

/// <summary>記事の askEmit。必要な能力が引数に現れる。<c>EffektStyle.askEmit</c> と同じ形である。</summary>
/// <param name="ask">値を取り出す能力。</param>
/// <param name="emit">値を発する能力。</param>
/// <param name="out">出力先。</param>
let askEmit (ask: IAsk<'a>) (emit: IEmit<'a>) (out: Log) : FIO<unit, exn> =
    fio {
        out "Start"
        let! v = ask.Ask()
        do! emit.Emit v
        out "End"
    }

/// <summary>能力を作って askEmit を実行する。</summary>
/// <param name="out">出力先。</param>
let exampleAskEmit (out: Log) : unit =
    let ask =
        { new IAsk<string> with
            member _.Ask() = FIO.succeed "Ask Value" }

    let emit =
        { new IEmit<string> with
            member _.Emit v = FIO.succeedWith (fun () -> out ("Emit " + v)) }

    match run (askEmit ask emit out) with
    | Succeeded() -> ()
    | result -> failwith $"%A{result}"

// ---- 途中で 1 回 ----

/// <summary>計算をトランザクションの中で動かす。成功したら commit し、失敗でも中断でも rollback する。</summary>
/// <remarks>
/// <para>
/// <c>FIO.acquireReleaseWith</c> は獲得と解放を中断不可の領域で動かし、獲得したものを必ず解放する。
/// <c>Advanced.transaction</c> は継続を捨てられると commit も rollback も走らないが、ここでは外からの中断でも rollback が走る。
/// </para>
/// <para>
/// FIO 0.6.0 の <c>acquireReleaseExitWith</c> は終わり方を解放に渡すが、0.5.0 にはないので、commit したかどうかを自分で持つ。
/// </para>
/// </remarks>
/// <param name="out">出力先。</param>
/// <param name="body">トランザクションの中の計算。</param>
let transaction (out: Log) (body: FIO<'a, 'e>) : FIO<'a, 'e> =
    FIO.acquireReleaseWith
        (FIO.succeedWith (fun () ->
            out "begin"
            ref false))
        (fun committed ->
            FIO.succeedWith (fun () ->
                if not committed.Value then
                    out "rollback"))
        (fun committed ->
            body.FlatMap(fun r ->
                FIO.succeedWith (fun () ->
                    out "commit"
                    committed.Value <- true
                    r)))

/// <summary>作業が成功するトランザクション。</summary>
/// <param name="out">出力先。</param>
let transactionCommits (out: Log) : unit =
    match run (transaction out (FIO.succeedWith (fun () -> out "work"))) with
    | Succeeded() -> ()
    | result -> out $"%A{result}"

/// <summary>作業が型付きの失敗で終わるトランザクション。</summary>
/// <param name="out">出力先。</param>
let transactionFails (out: Log) : unit =
    let work: FIO<unit, string> =
        fio {
            out "work"
            return! FIO.fail "work failed"
        }

    match run (transaction out work) with
    | Failed e -> out $"failed: {e}"
    | result -> out $"%A{result}"

/// <summary>作業の途中で外から中断されるトランザクション。中断でも rollback が走る。</summary>
/// <param name="out">出力先。</param>
let transactionInterrupted (out: Log) : unit =
    let started = Channel<unit>()

    let work: FIO<unit, exn> =
        fio {
            out "work"
            do! started.Write(())
            return! FIO.never ()
        }

    let parent: FIO<unit, exn> =
        fio {
            let! fiber = (transaction out work).Fork()
            do! started.Read()
            do! fiber.InterruptNow()
        }

    run parent |> ignore

// ---- 中断と構造化並行性 ----

/// <summary>親が正常に終わると、子は中断され、親の結果は子の後始末の後に返る。ZIO の <c>fork</c> と同じである。</summary>
/// <remarks>F# の <c>Async.StartChild</c> の子は、親が終わっても走り続ける（ZIO の検証の 6.2 節）。</remarks>
/// <param name="out">出力先。</param>
let exampleChildInterrupted (out: Log) : unit =
    let started = Channel<unit>()

    let child: FIO<unit, exn> =
        (fio {
            out "child started"
            do! started.Write(())
            return! FIO.never ()
        })
            .Ensuring(FIO.succeedWith (fun () -> out "child released"))

    let parent: FIO<unit, exn> =
        fio {
            let! _ = child.Fork()
            do! started.Read()
            out "parent done"
        }

    run parent |> ignore

/// <summary>中断不可の領域で届いた中断は、領域の終わりに効く。ZIO の <c>uninterruptible</c> と同じである。</summary>
/// <param name="out">出力先。</param>
let exampleUninterruptible (out: Log) : unit =
    let started = Channel<unit>()

    let body: FIO<unit, exn> =
        fio {
            do!
                FIO.uninterruptible (
                    fio {
                        out "region start"
                        do! started.Write(())
                        do! FIO.sleep (TimeSpan.FromMilliseconds 50.0)
                        out "region end"
                    }
                )

            out "after region"
        }

    let parent: FIO<unit, exn> =
        fio {
            let! fiber = body.Fork()
            do! started.Read()
            do! fiber.InterruptNow()
        }

    run parent |> ignore
